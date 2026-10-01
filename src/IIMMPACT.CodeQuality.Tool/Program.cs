using System.Text.Json;

return await QualityGate.RunAsync(args);

/// <summary>
/// Entry points for `iimmpact-quality check` and `iimmpact-quality baseline`.
/// Both collect diagnostics from SARIF logs, then check compares them against
/// code-quality-baseline.json while baseline rewrites it.
/// </summary>
internal static class QualityGate
{
    private const string BaselineFileName = "code-quality-baseline.json";
    private const string SarifDirectoryName = "iimmpact-quality";

    public static async Task<int> RunAsync(string[] args)
    {
        if (args.Length != 2 || (args[0] is not ("check" or "baseline")))
        {
            Console.Error.WriteLine("usage: iimmpact-quality check|baseline <solution.sln|solution.slnx>");
            return 2;
        }

        var solutionPath = Path.GetFullPath(args[1]);
        if (!File.Exists(solutionPath))
        {
            Console.Error.WriteLine($"solution not found: {solutionPath}");
            return 2;
        }

        var solutionDir = Path.GetDirectoryName(solutionPath)!;

        var projects = await ListProjectsAsync(solutionPath);
        if (projects.Count == 0)
        {
            Console.Error.WriteLine("no projects found in solution");
            return 2;
        }

        var diagnostics = await CollectDiagnosticsAsync(solutionPath, solutionDir, projects);
        if (diagnostics is null)
        {
            return 2;
        }

        var weakened = FindWeakenedRules(solutionDir);
        var baselinePath = Path.Combine(solutionDir, BaselineFileName);

        if (args[0] == "baseline")
        {
            var state = BaselineState.FromDiagnostics(diagnostics);
            File.WriteAllText(baselinePath, state.ToJson());
            Console.WriteLine($"wrote {BaselineFileName}: {state.FileCount} file(s), {state.TotalCount} diagnostic(s)");
            return 0;
        }

        var failures = new List<string>();

        if (!File.Exists(baselinePath))
        {
            Console.Error.WriteLine(
                $"baseline not found: {baselinePath}\n" +
                "run `iimmpact-quality baseline <solution>` once to record existing debt");
            return 1;
        }

        var baseline = BaselineState.Parse(File.ReadAllText(baselinePath));
        failures.AddRange(baseline.Compare(BaselineState.FromDiagnostics(diagnostics), diagnostics));

        foreach (var w in weakened)
        {
            failures.Add(w);
        }

        if (failures.Count == 0)
        {
            Console.WriteLine($"PASS: {diagnostics.Count} diagnostic(s) within baseline");
            return 0;
        }

        foreach (var failure in failures)
        {
            Console.WriteLine(failure);
        }
        Console.WriteLine($"FAIL: {failures.Count} group(s) over baseline");
        return 1;
    }

    /// <summary>dotnet sln list output: relative .csproj paths, one per line.</summary>
    private static async Task<List<string>> ListProjectsAsync(string solutionPath)
    {
        var (exit, output) = await RunProcessAsync(
            "dotnet", $"sln \"{solutionPath}\" list", Path.GetDirectoryName(solutionPath)!);
        if (exit != 0)
        {
            Console.Error.WriteLine(output);
            Console.Error.WriteLine($"dotnet sln list failed with exit code {exit}");
            return [];
        }

        var solutionDir = Path.GetDirectoryName(solutionPath)!;
        return output
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(l => l.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase))
            .Select(l => Path.GetFullPath(Path.Combine(solutionDir, l)))
            .ToList();
    }

    /// <summary>
    /// Deletes obj/iimmpact-quality under each project, rebuilds the solution with
    /// SARIF logging on, and parses the managed diagnostics from every log.
    /// Returns null when the build fails or a project produced no log.
    /// </summary>
    private static async Task<List<Diagnostic>?> CollectDiagnosticsAsync(
        string solutionPath, string solutionDir, List<string> projects)
    {
        foreach (var project in projects)
        {
            var dir = Path.Combine(Path.GetDirectoryName(project)!, "obj", SarifDirectoryName);
            if (Directory.Exists(dir))
            {
                Directory.Delete(dir, recursive: true);
            }
        }

        var (exit, output) = await RunProcessAsync(
            "dotnet",
            $"build \"{solutionPath}\" --no-incremental -nologo -p:IimmpactQualitySarif=true",
            solutionDir);
        Console.Write(output);
        if (exit != 0)
        {
            Console.Error.WriteLine($"dotnet build failed with exit code {exit}");
            return null;
        }

        var diagnostics = new List<Diagnostic>();
        foreach (var project in projects)
        {
            var dir = Path.Combine(Path.GetDirectoryName(project)!, "obj", SarifDirectoryName);
            var logs = Directory.Exists(dir)
                ? Directory.GetFiles(dir, "*.sarif")
                : [];
            if (logs.Length == 0)
            {
                Console.Error.WriteLine(
                    $"{Path.GetFileNameWithoutExtension(project)} produced no diagnostics log; " +
                    "reference IIMMPACT.CodeQuality");
                return null;
            }
            foreach (var log in logs)
            {
                diagnostics.AddRange(SarifLogParser.Parse(File.ReadAllText(log), solutionDir));
            }
        }

        // Multi-TFM projects emit the same diagnostic once per framework log.
        return diagnostics.Distinct().ToList();
    }

    /// <summary>
    /// Finds repo config that silences a managed rule: dotnet_diagnostic.&lt;id&gt;.severity
    /// set to none/silent/suggestion in *.editorconfig or *.globalconfig, or a managed
    /// rule listed in &lt;NoWarn&gt; inside *.props/*.targets/*.csproj. Skips bin/obj.
    /// </summary>
    public static List<string> FindWeakenedRules(string solutionDir)
    {
        var findings = new List<string>();
        var files = Directory
            .EnumerateFiles(solutionDir, "*", SearchOption.AllDirectories)
            .Where(f => !IsUnderBinOrObj(f))
            .Where(f =>
            {
                var name = Path.GetFileName(f);
                return name.EndsWith(".editorconfig", StringComparison.OrdinalIgnoreCase)
                    || name.EndsWith(".globalconfig", StringComparison.OrdinalIgnoreCase)
                    || name.EndsWith(".props", StringComparison.OrdinalIgnoreCase)
                    || name.EndsWith(".targets", StringComparison.OrdinalIgnoreCase)
                    || name.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase);
            });

        foreach (var file in files.OrderBy(f => f, StringComparer.Ordinal))
        {
            var relative = RelativePath(solutionDir, file);
            var lineNumber = 0;
            foreach (var rawLine in File.ReadLines(file))
            {
                lineNumber++;
                var line = rawLine.Trim();
                foreach (var rule in ManagedRules.Ids)
                {
                    if (IsConfigFile(file) && SeverityWeakens(line, rule))
                    {
                        findings.Add($"{relative}({lineNumber}): error: {rule} is silenced by repo config; remove the override");
                    }
                    else if (IsMsbuildFile(file) && NoWarnLists(line, rule))
                    {
                        findings.Add($"{relative}({lineNumber}): error: {rule} is suppressed in NoWarn; remove the suppression");
                    }
                }
            }
        }
        return findings;
    }

    private static bool IsConfigFile(string path) =>
        path.EndsWith(".editorconfig", StringComparison.OrdinalIgnoreCase)
        || path.EndsWith(".globalconfig", StringComparison.OrdinalIgnoreCase);

    private static bool IsMsbuildFile(string path) =>
        path.EndsWith(".props", StringComparison.OrdinalIgnoreCase)
        || path.EndsWith(".targets", StringComparison.OrdinalIgnoreCase)
        || path.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase);

    private static bool SeverityWeakens(string line, string rule)
    {
        if (!line.StartsWith($"dotnet_diagnostic.{rule}.severity", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }
        var value = line[(line.IndexOf('=') + 1)..].Trim();
        return value is "none" or "silent" or "suggestion";
    }

    private static bool NoWarnLists(string line, string rule)
    {
        var tagStart = line.IndexOf("<NoWarn", StringComparison.OrdinalIgnoreCase);
        if (tagStart < 0)
        {
            return false;
        }
        var closeStart = line.IndexOf('>', tagStart);
        var closeEnd = line.IndexOf("</NoWarn>", StringComparison.OrdinalIgnoreCase);
        if (closeStart < 0 || closeEnd < 0)
        {
            return false;
        }
        var value = line[(closeStart + 1)..closeEnd];
        return value.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Any(v => v.Equals(rule, StringComparison.OrdinalIgnoreCase));
    }

    private static bool IsUnderBinOrObj(string path)
    {
        foreach (var segment in path.Split(Path.DirectorySeparatorChar))
        {
            if (segment is "bin" or "obj")
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>Solution-relative path with forward slashes for stable baselines.</summary>
    public static string RelativePath(string solutionDir, string path)
    {
        var relative = Path.GetRelativePath(solutionDir, path);
        return relative.Replace(Path.DirectorySeparatorChar, '/');
    }

    private static async Task<(int Exit, string Output)> RunProcessAsync(
        string fileName, string arguments, string workingDirectory)
    {
        var process = new System.Diagnostics.Process
        {
            StartInfo = new System.Diagnostics.ProcessStartInfo
            {
                FileName = fileName,
                Arguments = arguments,
                WorkingDirectory = workingDirectory,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            },
        };
        process.Start();
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        var output = await stdout + await stderr;
        return (process.ExitCode, output);
    }
}

/// <summary>One compiler diagnostic that belongs to a managed rule.</summary>
internal sealed record Diagnostic(
    string RuleId,
    string Path,
    int Line,
    int Column,
    string Message,
    long? Metric);
