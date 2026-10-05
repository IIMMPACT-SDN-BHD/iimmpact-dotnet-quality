using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

internal sealed record CompilationKey(string Project, string TargetFramework);

internal sealed record ScanViolation(
    EntityKey Entity,
    string Rule,
    string Message,
    long? Metric,
    DiagnosticSite PhysicalSite,
    TokenSite[] TokenSites,
    string TokenDigest);

internal sealed class CompilationScan
{
    public required CompilationKey Key { get; init; }
    public required string ProjectPath { get; init; }
    public required List<string> Sources { get; init; }
    public required List<string> Configs { get; init; }
    public required List<string> AdditionalFiles { get; init; }
    public required List<string> Analyzers { get; init; }
    public required CSharpCommandLineArguments CommandLine { get; init; }
    public required List<Diagnostic> Diagnostics { get; init; }
    public required string SentinelPath { get; init; }
    /// <summary>Sources analyzers treat as generated, including SDK-written files. Filled by the scan.</summary>
    public HashSet<string> GeneratedSources { get; } = new(StringComparer.Ordinal);
}

internal sealed class CompleteScan : IDisposable
{
    private CompleteScan(
        List<CompilationScan> compilations,
        List<ScanViolation> violations,
        IReadOnlyDictionary<string, string> generatedFiles,
        string? temporaryDirectory)
    {
        Compilations = compilations;
        Violations = violations;
        ObservedGeneratedFiles = generatedFiles;
        _temporaryDirectory = temporaryDirectory;
    }

    public IReadOnlyList<CompilationScan> Compilations { get; }
    public IReadOnlyList<ScanViolation> Violations { get; }
    public IReadOnlyDictionary<string, string> ObservedGeneratedFiles { get; }
    private readonly string? _temporaryDirectory;

    internal static CompleteScan CreateValidated(
        List<CompilationScan> compilations,
        List<ScanViolation> violations,
        IReadOnlyDictionary<string, string> generatedFiles,
        string temporaryDirectory) =>
        new(compilations, violations, generatedFiles, temporaryDirectory);

    public void Dispose()
    {
        if (_temporaryDirectory is null) return;
        try { Directory.Delete(_temporaryDirectory, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}

internal static class ScanCollector
{
    private const string FileEntity = "file";
    private static readonly string[] SentinelRules = ["CA1849", "CA2022", "RS0030", "S134", "IDE0051", "CS8602"];
    private const int MinimumSdkMajor = 9;

    public static async Task<CompleteScan> CollectAsync(
        string solutionPath, string solutionDir, List<string> solutionProjects)
    {
        await RequireSupportedSdkAsync(solutionDir);
        var plans = await EvaluateClosureAsync(solutionProjects, solutionDir);
        if (!plans.Any(plan => plan.IsCSharp)) throw new ScanException("no C# compilations were declared");

        var invocationDir = Path.Combine(Path.GetTempPath(), $"iimmpact-quality-{Guid.NewGuid():N}");
        Directory.CreateDirectory(invocationDir);
        var artifactNames = WriteArtifactNamesProps(invocationDir, solutionDir);
        try
        {
            var compilations = new List<CompilationScan>();
            for (var index = 0; index < plans.Count; index++)
            {
                var plan = plans[index];
                var evidenceDir = Path.Combine(invocationDir, "evidence", index.ToString(System.Globalization.CultureInfo.InvariantCulture));
                var artifactsDir = Path.Combine(invocationDir, "artifacts");
                Directory.CreateDirectory(evidenceDir);
                var (exit, output) = await RunProcessAsync("dotnet",
                [
                    "build", plan.ProjectPath, "-f", plan.TargetFramework, "--no-dependencies",
                    "--no-incremental", "--nologo", "--artifacts-path", artifactsDir,
                    "-p:IimmpactQualitySarif=true", $"-p:IimmpactQualitySarifDir={evidenceDir}",
                    $"-p:CustomBeforeDirectoryBuildProps={artifactNames}",
                    "-p:PreferredUILang=en-US",
                    // The gate decides failure from the SARIF log; promoting the injected
                    // sentinel warnings to errors would only break the scan build.
                    "-p:TreatWarningsAsErrors=false", "-p:WarningsAsErrors=", "-p:CodeAnalysisTreatWarningsAsErrors=false",
                ], solutionDir);
                Console.Write(output);
                if (exit != 0) throw new ScanException($"dotnet build failed for {plan.Key.Project} ({plan.TargetFramework}) with exit code {exit}");
                // Non-C# references (for example F#) are built so dependants can compile,
                // but they carry no C# analysis.
                if (plan.IsCSharp) compilations.Add(ReadCompilation(plan, evidenceDir, solutionDir));
            }

            var generated = ClassifyGeneratedSources(solutionDir, compilations);
            var violations = Attribute(compilations, solutionDir);
            var policyFailures = compilations.SelectMany(compilation => Policy.Validate(compilation, solutionDir)).ToList();
            if (policyFailures.Count > 0) throw new ScanException(string.Join('\n', policyFailures));
            return CompleteScan.CreateValidated(compilations, violations, generated, invocationDir);
        }
        catch
        {
            try { Directory.Delete(invocationDir, recursive: true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            throw;
        }
    }

    // CA2022 ships in the .NET 9 SDK analyzers; an older SDK would silently enforce fewer rules.
    private static async Task RequireSupportedSdkAsync(string solutionDir)
    {
        var (exit, output) = await RunProcessAsync("dotnet", ["--version"], solutionDir);
        var version = output.Trim();
        if (exit != 0 || !int.TryParse(version.Split('.')[0], out var major))
            throw new ScanException($"cannot determine the .NET SDK version: {version}");
        if (major < MinimumSdkMajor)
            throw new ScanException($".NET SDK {version} is not supported; iimmpact-quality requires .NET SDK {MinimumSdkMajor} or later (target frameworks may stay older)");
    }

    // All scan builds share one artifacts directory, where the SDK names each project's folder
    // after the project file. Same-named projects in different folders would overwrite each
    // other, so name the folder after a hash of the solution-relative path. The SDK fixes this
    // name before package props load. CustomBeforeDirectoryBuildProps is imported earlier, is not
    // used by the SDK (unlike CustomAfterDirectoryBuildProps), and as a global property it also
    // applies when referenced projects are evaluated.
    private static string WriteArtifactNamesProps(string invocationDir, string solutionDir)
    {
        var path = Path.Combine(invocationDir, "artifact-names.props");
        var root = System.Security.SecurityElement.Escape(solutionDir);
        File.WriteAllText(path, $$"""
            <Project>
              <PropertyGroup>
                <_IimmpactRelativeProject>$([System.IO.Path]::GetRelativePath('{{root}}', '$(MSBuildProjectFullPath)'))</_IimmpactRelativeProject>
                <ArtifactsProjectName>$(MSBuildProjectName)-$([MSBuild]::StableStringHash('$(_IimmpactRelativeProject)'))</ArtifactsProjectName>
              </PropertyGroup>
            </Project>
            """);
        return path;
    }

    private sealed record CompilationPlan(string ProjectPath, string TargetFramework, CompilationKey Key)
    {
        public bool IsCSharp => ProjectPath.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase);
    }

    private static async Task<List<CompilationPlan>> EvaluateClosureAsync(
        List<string> solutionProjects, string solutionDir)
    {
        var plans = new Dictionary<(string Path, string Tfm), CompilationPlan>();
        var dependencies = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        var projects = new Queue<string>(solutionProjects.Select(Path.GetFullPath));
        var discovered = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        while (projects.Count > 0)
        {
            var project = QualityGate.RealPath(projects.Dequeue());
            if (!discovered.Add(project)) continue;
            var frameworks = await DeclaredFrameworksAsync(project);
            dependencies.TryAdd(project, new HashSet<string>(StringComparer.OrdinalIgnoreCase));
            if (frameworks.Count == 0) throw new ScanException($"project declares no target framework: {project}");
            foreach (var tfm in frameworks)
            {
                var relative = QualityGate.RelativePath(solutionDir, project);
                if (relative.StartsWith("../", StringComparison.Ordinal))
                    throw new ScanException($"project reference is outside the solution repository: {project}");
                var plan = new CompilationPlan(project, tfm, new CompilationKey(relative, tfm));
                plans[(project, tfm)] = plan;
                foreach (var reference in await ProjectReferencesAsync(project, tfm))
                {
                    dependencies[project].Add(reference);
                    projects.Enqueue(reference);
                }
            }
        }
        var projectOrder = new List<string>();
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var visiting = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        void Visit(string project)
        {
            if (visited.Contains(project)) return;
            if (!visiting.Add(project)) throw new ScanException($"cyclic project reference involving {project}");
            if (dependencies.TryGetValue(project, out var references))
                foreach (var reference in references.OrderBy(p => p, StringComparer.OrdinalIgnoreCase)) Visit(reference);
            visiting.Remove(project);
            visited.Add(project);
            projectOrder.Add(project);
        }
        foreach (var project in solutionProjects.Select(Path.GetFullPath)) Visit(QualityGate.RealPath(project));
        return projectOrder.SelectMany(project => plans.Values
                .Where(plan => plan.ProjectPath.Equals(project, StringComparison.OrdinalIgnoreCase))
                .OrderBy(plan => plan.TargetFramework, StringComparer.Ordinal))
            .ToList();
    }

    private static async Task<List<string>> DeclaredFrameworksAsync(string project)
    {
        using var root = await EvaluateAsync(project,
            ["-getProperty:TargetFramework", "-getProperty:TargetFrameworks"]);
        var properties = root.RootElement.GetProperty("Properties");
        var multi = properties.GetProperty("TargetFrameworks").GetString();
        var single = properties.GetProperty("TargetFramework").GetString();
        var value = string.IsNullOrWhiteSpace(multi) ? single : multi;
        return value?.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Distinct(StringComparer.OrdinalIgnoreCase).ToList() ?? [];
    }

    private static async Task<List<string>> ProjectReferencesAsync(string project, string tfm)
    {
        using var root = await EvaluateAsync(project, [$"-p:TargetFramework={tfm}", "-getItem:ProjectReference"]);
        if (!root.RootElement.TryGetProperty("Items", out var items)
            || !items.TryGetProperty("ProjectReference", out var references)) return [];
        return references.EnumerateArray()
            .Select(item => item.GetProperty("FullPath").GetString())
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Select(path => QualityGate.RealPath(path!)).ToList();
    }

    private static async Task<JsonDocument> EvaluateAsync(string project, IReadOnlyList<string> arguments)
    {
        var all = new List<string> { "msbuild", project, "--nologo" };
        all.AddRange(arguments);
        var (exit, output) = await RunProcessAsync("dotnet", all, Path.GetDirectoryName(project)!);
        if (exit != 0) throw new ScanException($"dotnet msbuild evaluation failed for {project}\n{output}");
        try { return JsonDocument.Parse(output); }
        catch (JsonException exception) { throw new ScanException($"invalid MSBuild evaluation output for {project}: {exception.Message}"); }
    }

    private static CompilationScan ReadCompilation(CompilationPlan plan, string evidenceDir, string solutionDir)
    {
        var evidencePath = Path.Combine(evidenceDir, "scan.txt");
        var sarifPath = Path.Combine(evidenceDir, "scan.sarif");
        if (!File.Exists(evidencePath)) throw new ScanException($"{plan.Key.Project} ({plan.TargetFramework}) produced no fresh compiler evidence");
        if (!File.Exists(sarifPath)) throw new ScanException($"{plan.Key.Project} ({plan.TargetFramework}) produced no fresh diagnostics log");

        var evidence = ParseEvidence(File.ReadAllLines(evidencePath));
        if (QualityGate.RealPath(evidence.Project) != plan.ProjectPath || evidence.TargetFramework != plan.TargetFramework)
            throw new ScanException($"compiler evidence identity does not match {plan.Key.Project} ({plan.TargetFramework})");

        CSharpCommandLineArguments commandLine;
        try
        {
            commandLine = CSharpCommandLineParser.Default.Parse(
                evidence.CscArgs, Path.GetDirectoryName(plan.ProjectPath)!, null!, null!);
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException)
        {
            throw new ScanException($"cannot parse csc command line for {plan.Key.Project} ({plan.TargetFramework}): {exception.Message}");
        }
        var label = $"{plan.Key.Project} ({plan.TargetFramework})";
        if (commandLine.Errors.Any(d => d.Severity == DiagnosticSeverity.Error))
            throw new ScanException($"{label}: csc command line contains errors: {string.Join("; ", commandLine.Errors.Select(e => e.GetMessage()))}");
        if (commandLine.SkipAnalyzers || evidence.SkippedAnalyzers)
            throw new ScanException($"{label}: compiler skipped analyzers");
        if (commandLine.PreferredUILang?.TwoLetterISOLanguageName != "en")
            throw new ScanException($"{label}: compiler diagnostics must use an English PreferredUILang");

        ValidateCommandLinePaths(label, evidence, commandLine, plan.ProjectPath);
        ValidateAnalyzerOrigins(label, evidence);
        var diagnostics = SarifLogParser.Parse(File.ReadAllText(sarifPath), solutionDir);
        var sentinel = QualityGate.RealPath(Path.Combine(evidenceDir, "execution-sentinel.cs"));
        foreach (var rule in SentinelRules)
        {
            if (!diagnostics.Any(d => d.RuleId == rule && QualityGate.RealPath(Path.Combine(solutionDir, d.Path)) == sentinel))
                throw new ScanException($"{label}: missing fresh {rule} analyzer execution sentinel result");
        }
        var analyzerFailures = diagnostics.Where(d => d.RuleId.StartsWith("AD", StringComparison.Ordinal)).ToList();
        if (analyzerFailures.Count > 0)
            throw new ScanException($"{label}: analyzer failed: {string.Join("; ", analyzerFailures.Select(d => $"{d.RuleId}: {d.Message}"))}");

        return new CompilationScan
        {
            Key = plan.Key,
            ProjectPath = plan.ProjectPath,
            Sources = evidence.Sources.Select(QualityGate.RealPath).ToList(),
            Configs = evidence.Configs.Select(QualityGate.RealPath).ToList(),
            AdditionalFiles = evidence.AdditionalFiles.Select(QualityGate.RealPath).ToList(),
            Analyzers = evidence.Analyzers.Select(QualityGate.RealPath).ToList(),
            CommandLine = commandLine,
            Diagnostics = diagnostics,
            SentinelPath = sentinel,
        };
    }

    private static void ValidateCommandLinePaths(string label, Evidence evidence, CSharpCommandLineArguments args, string project)
    {
        static HashSet<string> Real(IEnumerable<string> paths, string baseDir) => paths
            .Select(path => Path.IsPathRooted(path) ? path : Path.Combine(baseDir, path))
            .Select(QualityGate.RealPath).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var baseDir = Path.GetDirectoryName(project)!;
        var expectedSources = Real(evidence.Sources, baseDir);
        var actualSources = Real(args.SourceFiles.Select(s => s.Path), baseDir);
        if (!expectedSources.SetEquals(actualSources)) throw new ScanException($"{label}: compiler source evidence does not match parsed csc inputs");
        if (!Real(evidence.Configs, baseDir).SetEquals(Real(args.AnalyzerConfigPaths, baseDir))) throw new ScanException($"{label}: analyzer config evidence does not match parsed csc inputs");
        if (!Real(evidence.AdditionalFiles, baseDir).SetEquals(Real(args.AdditionalFiles.Select(f => f.Path), baseDir))) throw new ScanException($"{label}: additional-file evidence does not match parsed csc inputs");
        if (!Real(evidence.Analyzers, baseDir).SetEquals(Real(args.AnalyzerReferences.Select(a => a.FilePath), baseDir))) throw new ScanException($"{label}: analyzer evidence does not match parsed csc inputs");
    }

    // Analyzers and source generators must come from the SDK or a NuGet package, whose identity
    // and version are reviewed in project files. A locally built or copied analyzer could emit
    // or suppress code that the gate never sees.
    private static void ValidateAnalyzerOrigins(string label, Evidence evidence)
    {
        var roots = new[] { evidence.SdkRoot, evidence.NuGetRoot }
            .Select(root => QualityGate.RealPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar)
            .ToArray();
        var foreign = evidence.Analyzers.Select(QualityGate.RealPath)
            .Where(path => !roots.Any(root => path.StartsWith(root, StringComparison.Ordinal)))
            .ToList();
        if (foreign.Count > 0)
            throw new ScanException($"{label}: analyzers must come from the .NET SDK or a NuGet package; not supported: {string.Join(", ", foreign)}");
    }

    private sealed record Evidence(
        string Project, string TargetFramework, bool SkippedAnalyzers, string SdkRoot, string NuGetRoot,
        List<string> Analyzers, List<string> Configs, List<string> AdditionalFiles,
        List<string> Sources, List<string> CscArgs);

    private static Evidence ParseEvidence(string[] lines)
    {
        string? project = null;
        string? tfm = null;
        string? sdkRoot = null;
        string? nugetRoot = null;
        var skipped = false;
        var sections = new Dictionary<string, List<string>>(StringComparer.Ordinal)
        {
            ["analyzers"] = [], ["configs"] = [], ["additional-files"] = [], ["sources"] = [], ["csc-args"] = [],
        };
        string? section = null;
        foreach (var line in lines)
        {
            if (line.StartsWith("project=", StringComparison.Ordinal)) { project = line[8..]; continue; }
            if (line.StartsWith("tfm=", StringComparison.Ordinal)) { tfm = line[4..]; continue; }
            if (line.StartsWith("sdk-root=", StringComparison.Ordinal)) { sdkRoot = line[9..]; continue; }
            if (line.StartsWith("nuget-root=", StringComparison.Ordinal)) { nugetRoot = line[11..]; continue; }
            if (line.StartsWith("skipped-analyzers=", StringComparison.Ordinal))
            {
                if (!bool.TryParse(line[18..], out skipped) && line[18..].Length > 0) throw new ScanException("invalid skipped-analyzers evidence");
                continue;
            }
            var header = line.EndsWith(':') ? line[..^1] : null;
            if (header is not null && sections.ContainsKey(header)) { section = header; continue; }
            if (section is null) throw new ScanException("malformed compiler evidence");
            if (line.Length > 0) sections[section].Add(line);
        }
        if (string.IsNullOrWhiteSpace(project) || string.IsNullOrWhiteSpace(tfm) || sections["csc-args"].Count == 0
            || string.IsNullOrWhiteSpace(sdkRoot) || string.IsNullOrWhiteSpace(nugetRoot))
            throw new ScanException("incomplete compiler evidence");
        return new(project, tfm, skipped, sdkRoot, nugetRoot, sections["analyzers"], sections["configs"], sections["additional-files"], sections["sources"], sections["csc-args"]);
    }

    private static List<ScanViolation> Attribute(List<CompilationScan> compilations, string solutionDir)
    {
        var violations = new List<ScanViolation>();
        foreach (var compilation in compilations)
        {
            var sources = compilation.Sources.Where(s => s != compilation.SentinelPath)
                .ToDictionary(s => QualityGate.RelativePath(solutionDir, s), StringComparer.Ordinal);
            var indexes = new Dictionary<string, DeclarationIndex>(StringComparer.OrdinalIgnoreCase);
            foreach (var diagnostic in compilation.Diagnostics.Where(d => ManagedRules.Ids.Contains(d.RuleId)))
            {
                var diagnosticFullPath = QualityGate.RealPath(Path.Combine(solutionDir, diagnostic.Path));
                if (diagnosticFullPath == compilation.SentinelPath) continue;
                if (!sources.TryGetValue(diagnostic.Path, out var source))
                    throw new ScanException($"{compilation.Key.Project} ({compilation.Key.TargetFramework}): managed diagnostic {diagnostic.RuleId} points outside compiler inputs: {diagnostic.Path}");
                if (ManagedRules.MetricIds.Contains(diagnostic.RuleId) && diagnostic.Metric is null)
                    throw new ScanException($"{diagnostic.Path}({diagnostic.Line},{diagnostic.Column}): {diagnostic.RuleId} did not report a parseable metric");

                if (!indexes.TryGetValue(source, out var index))
                    indexes[source] = index = DeclarationIndex.Build(source, compilation.Key, diagnostic.Path, compilation.CommandLine.ParseOptions, compilation.CommandLine.Encoding);
                var owner = index.OwnerOf(diagnostic.Line, diagnostic.Column, out _);
                var declaration = diagnostic.RuleId == "S104" ? FileEntity : owner?.Identity.Split('|', 3)[2] ?? FileEntity;
                var physical = new DiagnosticSite(diagnostic.Path, diagnostic.Line, diagnostic.Column);
                var digest = ManagedRules.MetricIds.Contains(diagnostic.RuleId) ? string.Empty : index.TokenDigestOf(declaration);
                var tokenSites = ManagedRules.MetricIds.Contains(diagnostic.RuleId)
                    ? []
                    : new[] { index.TokenSiteOf(declaration, diagnostic.Line, diagnostic.Column) };
                violations.Add(new ScanViolation(
                    new EntityKey(compilation.Key, diagnostic.Path, declaration), diagnostic.RuleId,
                    diagnostic.Message, diagnostic.Metric, physical, tokenSites, digest));
            }
        }
        return violations.GroupBy(v => (v.Entity, v.Rule)).Select(group =>
        {
            var first = group.First();
            return first with
            {
                Metric = group.Max(v => v.Metric),
                TokenSites = group.SelectMany(v => v.TokenSites).Distinct().OrderBy(s => s.Token).ThenBy(s => s.Offset).ToArray(),
            };
        }).ToList();
    }

    private static IReadOnlyDictionary<string, string> ClassifyGeneratedSources(
        string solutionDir, List<CompilationScan> compilations)
    {
        var candidates = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var compilation in compilations)
        {
            var sources = compilation.Sources.Where(s => s != compilation.SentinelPath)
                .ToDictionary(s => s, s => CSharpSyntaxTree.ParseText(File.ReadAllText(s), compilation.CommandLine.ParseOptions).GetCompilationUnitRoot());
            var attributeNames = GeneratedAttributeNames(sources.Values);
            foreach (var (source, root) in sources)
            {
                if (IsSdkGenerated(source, compilation.ProjectPath, compilation.SentinelPath))
                {
                    compilation.GeneratedSources.Add(source);
                    continue;
                }
                var options = Policy.EffectiveOptions(compilation, source);
                var marked = IsAnalyzerRecognizedGenerated(source, root, attributeNames)
                    || (options.TryGetValue("generated_code", out var generated) && generated.Equals("true", StringComparison.OrdinalIgnoreCase));
                if (!marked) continue;
                compilation.GeneratedSources.Add(source);
                var relative = QualityGate.RelativePath(solutionDir, source);
                if (relative.StartsWith("../", StringComparison.Ordinal)) throw new ScanException($"generated compile input is outside the solution repository: {source}");
                candidates[relative] = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(source))).ToLowerInvariant();
            }
        }
        return candidates;
    }

    internal static bool IsSdkGenerated(string source, string projectPath, string sentinelPath)
    {
        var invocationRoot = Directory.GetParent(Directory.GetParent(Path.GetDirectoryName(sentinelPath)!)!.FullName)!.FullName;
        var obj = Path.Combine(invocationRoot, "artifacts", "obj") + Path.DirectorySeparatorChar;
        if (!source.StartsWith(obj, StringComparison.OrdinalIgnoreCase)) return false;
        var name = Path.GetFileName(source);
        var projectName = Path.GetFileNameWithoutExtension(projectPath);
        var recognizedName = name == $"{projectName}.AssemblyInfo.cs"
            || name == $"{projectName}.GlobalUsings.g.cs"
            || (name.StartsWith(".NETCoreApp,Version=v", StringComparison.Ordinal) && name.EndsWith(".AssemblyAttributes.cs", StringComparison.Ordinal));
        if (!recognizedName) return false;
        var root = CSharpSyntaxTree.ParseText(File.ReadAllText(source)).GetCompilationUnitRoot();
        if (!HasAutoGeneratedHeader(root)) return false;
        return name.EndsWith("GlobalUsings.g.cs", StringComparison.Ordinal)
            ? root.Members.Count == 0 && root.AttributeLists.Count == 0 && root.Usings.Count > 0
            : root.Members.Count == 0 && root.AttributeLists.Count > 0
                && root.AttributeLists.All(list => list.Target?.Identifier.IsKind(SyntaxKind.AssemblyKeyword) == true);
    }

    private static readonly string[] GeneratedAttributes =
        ["CompilerGenerated", "CompilerGeneratedAttribute", "GeneratedCode", "GeneratedCodeAttribute"];

    // The generated-code attribute names in this compilation, including `using X = ...` aliases
    // (local or global) that point at them. ValueText strips `@` escapes.
    private static HashSet<string> GeneratedAttributeNames(IEnumerable<CompilationUnitSyntax> roots)
    {
        var names = new HashSet<string>(GeneratedAttributes, StringComparer.Ordinal);
        foreach (var alias in roots.SelectMany(root => root.DescendantNodes().OfType<UsingDirectiveSyntax>()))
        {
            if (alias.Alias is not null && alias.NamespaceOrType is { } target && names.Contains(LastIdentifier(target)))
            {
                names.Add(alias.Alias.Name.Identifier.ValueText);
                names.Add(alias.Alias.Name.Identifier.ValueText + "Attribute");
            }
        }
        return names;
    }

    private static string LastIdentifier(SyntaxNode name) => name.DescendantTokens()
        .LastOrDefault(token => token.IsKind(SyntaxKind.IdentifierToken)).ValueText;

    private static bool IsAnalyzerRecognizedGenerated(string source, CompilationUnitSyntax root, HashSet<string> attributeNames)
    {
        var name = Path.GetFileName(source);
        if (name.EndsWith(".designer.cs", StringComparison.OrdinalIgnoreCase)
            || name.EndsWith(".generated.cs", StringComparison.OrdinalIgnoreCase)
            || name.EndsWith(".g.cs", StringComparison.OrdinalIgnoreCase)
            || name.EndsWith(".g.i.cs", StringComparison.OrdinalIgnoreCase)) return true;
        if (HasAutoGeneratedHeader(root)) return true;
        return root.DescendantNodes().OfType<AttributeSyntax>()
            .Any(attribute => attributeNames.Contains(LastIdentifier(attribute.Name)));
    }

    private static bool HasAutoGeneratedHeader(CompilationUnitSyntax root) => root.GetLeadingTrivia()
        .Where(trivia => trivia.IsKind(SyntaxKind.SingleLineCommentTrivia) || trivia.IsKind(SyntaxKind.MultiLineCommentTrivia))
        .Any(trivia => trivia.ToString().Contains("<auto-generated", StringComparison.OrdinalIgnoreCase)
            || trivia.ToString().Contains("<autogenerated", StringComparison.OrdinalIgnoreCase));

    public static List<string> CompareGeneratedExclusions(
        CompleteScan scan, IReadOnlyDictionary<string, string> approved)
    {
        var failures = new List<string>();
        foreach (var (path, hash) in scan.ObservedGeneratedFiles)
        {
            if (!approved.TryGetValue(path, out var expected) || expected != hash)
                failures.Add($"{path}: generated compile input is not approved at this exact content digest");
        }
        foreach (var path in approved.Keys.Where(path => !scan.ObservedGeneratedFiles.ContainsKey(path)))
            failures.Add($"{path}: approved generated exclusion is not present in the current compilation inputs");
        return failures;
    }

    public static async Task<(int Exit, byte[] Output)> RunProcessBytesAsync(
        string fileName, IReadOnlyList<string> arguments, string workingDirectory)
    {
        var info = StartInfo(fileName, arguments, workingDirectory);
        var process = System.Diagnostics.Process.Start(info)!;
        var stdout = new MemoryStream();
        var copy = process.StandardOutput.BaseStream.CopyToAsync(stdout);
        var stderr = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        await copy;
        await stderr;
        return (process.ExitCode, stdout.ToArray());
    }

    public static async Task<(int Exit, string Output)> RunProcessAsync(
        string fileName, IReadOnlyList<string> arguments, string workingDirectory)
    {
        var process = System.Diagnostics.Process.Start(StartInfo(fileName, arguments, workingDirectory))!;
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        return (process.ExitCode, await stdout + await stderr);
    }

    private static System.Diagnostics.ProcessStartInfo StartInfo(
        string fileName, IReadOnlyList<string> arguments, string workingDirectory)
    {
        var info = new System.Diagnostics.ProcessStartInfo
        {
            FileName = fileName, WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false,
        };
        foreach (var argument in arguments) info.ArgumentList.Add(argument);
        return info;
    }
}

internal sealed class ScanException : Exception
{
    public ScanException(string message) : base(message) { }
}
