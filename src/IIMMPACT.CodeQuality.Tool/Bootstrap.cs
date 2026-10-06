/// <summary>
/// Bootstrap source validation: a baseline may only be proposed when every handwritten
/// compile input, project file and Directory.Build file matches the approved Git revision
/// as checked out, and the compile set is the one that revision evaluates to.
/// </summary>
internal static class Bootstrap
{
    /// <summary>
    /// Returns one failure line per compilation input that differs from the source
    /// commit: handwritten sources, analyzer configs, additional files and project
    /// files must all hash to the committed blob.
    /// </summary>
    public static async Task<List<string>> ValidateSourcesAsync(
        string repoRoot, string commit, CompleteScan scan, string solutionDir)
    {
        var failures = new List<string>();
        var checkedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var compilation in scan.Compilations)
        {
            var label = $"{compilation.Key.Project} ({compilation.Key.TargetFramework})";
            var inputs = compilation.Sources
                .Where(source => source != compilation.SentinelPath)
                .Where(source => !ScanCollector.IsSdkGenerated(source, compilation.SentinelPath) && !compilation.IsTrustedFile(source))
                .Concat(compilation.Configs)
                .Concat(compilation.AdditionalFiles)
                .Append(compilation.ProjectPath)
                .Concat(MsBuildInputs(repoRoot, compilation.ProjectPath))
                .Where(File.Exists)
                .Select(QualityGate.RealPath)
                .Distinct(StringComparer.OrdinalIgnoreCase);

            foreach (var fullPath in inputs)
            {
                if (!IsUnderRepo(repoRoot, fullPath))
                {
                    if (compilation.Sources.Contains(fullPath, StringComparer.OrdinalIgnoreCase))
                    {
                        failures.Add($"{label}: compile input {fullPath} is outside the approved source repository");
                    }
                    continue;
                }
                var relative = QualityGate.RelativePath(repoRoot, fullPath);
                if (!checkedPaths.Add(relative))
                {
                    continue;
                }
                var committedBytes = await Git.CommittedBytesAsync(repoRoot, commit, relative);
                if (committedBytes is null)
                {
                    failures.Add($"{label}: {relative} is not committed at {commit[..12]}");
                    continue;
                }
                var actualBytes = await File.ReadAllBytesAsync(fullPath);
                if (!SameIgnoringLineEndings(committedBytes, actualBytes))
                {
                    var committedText = System.Text.Encoding.UTF8.GetString(committedBytes);
                    if (!OnlyTemporaryQualityWiringDiffers(fullPath, committedText))
                    {
                        failures.Add(
                            $"{label}: {relative} differs from {commit[..12]}; " +
                            "bootstrap only permits temporary IIMMPACT.CodeQuality package wiring changes");
                    }
                }
            }
        }
        failures.AddRange(await ValidateSourceSetsAsync(repoRoot, commit, scan));
        return failures;
    }

    private static async Task<List<string>> ValidateSourceSetsAsync(
        string repoRoot, string commit, CompleteScan scan)
    {
        var failures = new List<string>();
        var snapshot = await Git.ExportTreeAsync(repoRoot, commit);
        try
        {
            foreach (var compilation in scan.Compilations)
            {
                var projectRelative = QualityGate.RelativePath(repoRoot, compilation.ProjectPath);
                var snapshotProject = Path.Combine(snapshot, projectRelative);
                var (exit, output) = await ScanCollector.RunProcessAsync(
                    "dotnet",
                    ["msbuild", snapshotProject, "--nologo", $"-p:TargetFramework={compilation.Key.TargetFramework}", "-getItem:Compile"],
                    Path.GetDirectoryName(snapshotProject)!);
                if (exit != 0)
                {
                    failures.Add($"{compilation.Key.Project} ({compilation.Key.TargetFramework}): cannot evaluate compile inputs at {commit[..12]}: {output.Trim()}");
                    continue;
                }
                try
                {
                    using var document = System.Text.Json.JsonDocument.Parse(output);
                    var expected = document.RootElement.GetProperty("Items").GetProperty("Compile").EnumerateArray()
                        .Select(item => item.GetProperty("FullPath").GetString()!)
                        .Where(path => IsUnderRepo(snapshot, path))
                        .Select(path => QualityGate.RelativePath(snapshot, path))
                        .ToHashSet(StringComparer.Ordinal);
                    var actual = compilation.Sources
                        .Where(source => source != compilation.SentinelPath)
                        .Where(source => !ScanCollector.IsSdkGenerated(source, compilation.SentinelPath) && !compilation.IsTrustedFile(source))
                        .Where(source => IsUnderRepo(repoRoot, source))
                        .Select(source => QualityGate.RelativePath(repoRoot, source))
                        .ToHashSet(StringComparer.Ordinal);
                    if (!expected.SetEquals(actual))
                    {
                        var missing = expected.Except(actual).OrderBy(path => path, StringComparer.Ordinal);
                        var added = actual.Except(expected).OrderBy(path => path, StringComparer.Ordinal);
                        failures.Add($"{compilation.Key.Project} ({compilation.Key.TargetFramework}): compile input set differs from {commit[..12]}; missing [{string.Join(", ", missing)}], added [{string.Join(", ", added)}]");
                    }
                }
                catch (Exception exception) when (exception is System.Text.Json.JsonException or KeyNotFoundException or InvalidOperationException)
                {
                    failures.Add($"{compilation.Key.Project} ({compilation.Key.TargetFramework}): invalid source-revision MSBuild evaluation: {exception.Message}");
                }
            }
        }
        finally
        {
            try { Directory.Delete(snapshot, recursive: true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
        return failures;
    }

    // A clean checkout may convert LF to CRLF (core.autocrlf, eol=crlf); nothing else may differ.
    private static bool SameIgnoringLineEndings(byte[] committed, byte[] actual) =>
        committed.AsSpan().SequenceEqual(actual) || WithoutCarriageReturns(committed).SequenceEqual(WithoutCarriageReturns(actual));

    private static byte[] WithoutCarriageReturns(byte[] bytes)
    {
        var result = new List<byte>(bytes.Length);
        for (var index = 0; index < bytes.Length; index++)
        {
            if (bytes[index] == (byte)'\r' && index + 1 < bytes.Length && bytes[index + 1] == (byte)'\n') continue;
            result.Add(bytes[index]);
        }
        return [.. result];
    }

    private static bool IsUnderRepo(string repoRoot, string fullPath)
    {
        var relative = Path.GetRelativePath(repoRoot, fullPath);
        return !relative.StartsWith("..", StringComparison.Ordinal);
    }

    private static IEnumerable<string> MsBuildInputs(string repoRoot, string projectPath)
    {
        for (var directory = Path.GetDirectoryName(projectPath); directory is not null; directory = Path.GetDirectoryName(directory))
        {
            foreach (var name in new[] { "Directory.Build.props", "Directory.Build.targets", "Directory.Packages.props", "global.json" })
            {
                var path = Path.Combine(directory, name);
                if (File.Exists(path)) yield return path;
            }
            if (QualityGate.RealPath(directory) == QualityGate.RealPath(repoRoot)) yield break;
        }
    }

    private static bool OnlyTemporaryQualityWiringDiffers(string actualPath, string committedText)
    {
        if (!actualPath.EndsWith(".props", StringComparison.OrdinalIgnoreCase)
            && !actualPath.EndsWith(".targets", StringComparison.OrdinalIgnoreCase)
            && !actualPath.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase)) return false;
        try
        {
            // Removes the quality package references, then any ItemGroup they leave empty; formatting
            // whitespace is ignored, everything else (elements, attributes, comments) must match.
            static string Normalize(string xml)
            {
                var document = System.Xml.Linq.XDocument.Parse(xml);
                var wiring = document.Descendants().Where(element =>
                    element.Name.LocalName == "PackageReference"
                    && ((string?)element.Attribute("Include")) is "IIMMPACT.CodeQuality" or "IIMMPACT.CodeQuality.Tool")
                    .ToList();
                foreach (var element in wiring)
                {
                    var group = element.Parent;
                    element.Remove();
                    if (group is { Name.LocalName: "ItemGroup" } && !group.Nodes().Any()) group.Remove();
                }
                return document.ToString(System.Xml.Linq.SaveOptions.DisableFormatting);
            }
            return Normalize(File.ReadAllText(actualPath)) == Normalize(committedText);
        }
        catch (System.Xml.XmlException)
        {
            return false;
        }
    }
}
