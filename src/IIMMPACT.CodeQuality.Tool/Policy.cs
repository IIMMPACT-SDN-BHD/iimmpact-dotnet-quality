using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;

internal static class Policy
{
    public const string Name = "iimmpact-quality/v2";

    private static readonly IReadOnlyDictionary<string, byte[]> OwnedFiles = LoadOwnedFiles();

    public static List<string> Validate(CompilationScan compilation, string solutionDir)
    {
        var label = $"{compilation.Key.Project} ({compilation.Key.TargetFramework})";
        var failures = new List<string>();
        RequireAnalyzer(compilation, label, "Microsoft.CodeAnalysis.NetAnalyzers.dll", failures);
        RequireAnalyzer(compilation, label, "SonarAnalyzer.CSharp.dll", failures);
        RequireAnalyzer(compilation, label, "Microsoft.CodeAnalysis.CSharp.BannedApiAnalyzers.dll", failures);
        RequireOwnedFile(compilation.Configs, "IIMMPACT.CodeQuality.globalconfig", label, failures);
        RequireOwnedFile(compilation.AdditionalFiles, "SonarLint.xml", label, failures);
        RequireOwnedFile(compilation.AdditionalFiles, "CodeMetricsConfig.txt", label, failures);
        RequireOwnedFile(compilation.AdditionalFiles, "BannedSymbols.IIMMPACT.txt", label, failures);

        foreach (var source in compilation.Sources.Where(s => s != compilation.SentinelPath))
        {
            (AnalyzerConfigOptionsResult Global, AnalyzerConfigOptionsResult Source) options;
            try { options = EffectiveResults(compilation, source); }
            catch (Exception exception) when (exception is ArgumentException or InvalidOperationException)
            {
                failures.Add($"{label}: cannot resolve analyzer configuration for {QualityGate.RelativePath(solutionDir, source)}: {exception.Message}");
                continue;
            }
            foreach (var diagnostic in options.Global.Diagnostics.Concat(options.Source.Diagnostics).Where(d => d.Severity == DiagnosticSeverity.Error))
                failures.Add($"{label}: analyzer configuration error for {QualityGate.RelativePath(solutionDir, source)}: {diagnostic.GetMessage()}");
            foreach (var rule in ManagedRules.Ids)
            {
                var normalizedRule = rule.ToLowerInvariant();
                var found = options.Source.TreeOptions.TryGetValue(normalizedRule, out var severity)
                    || options.Global.TreeOptions.TryGetValue(normalizedRule, out severity);
                if (!found || severity != ReportDiagnostic.Warn)
                    failures.Add($"{label}: effective severity for {rule} on {QualityGate.RelativePath(solutionDir, source)} is {severity}; expected warning");
                if (compilation.CommandLine.CompilationOptions.SpecificDiagnosticOptions
                    .Any(option => option.Key.Equals(rule, StringComparison.OrdinalIgnoreCase)
                        && option.Value == ReportDiagnostic.Suppress))
                    failures.Add($"{label}: {rule} is suppressed by the actual csc command line");
            }
            if (!ScanCollector.IsSdkGenerated(source, compilation.ProjectPath, compilation.SentinelPath)
                && !ScanCollector.IsAnalyzerRecognizedGenerated(source, compilation.CommandLine.ParseOptions))
                failures.AddRange(NullableDisables(source, compilation.CommandLine.ParseOptions)
                    .Select(line => $"{QualityGate.RelativePath(solutionDir, source)}({line},1): error: #nullable disable suppresses managed nullable rules"));
        }

        failures.AddRange(compilation.Diagnostics
            .Where(d => d.SuppressedInSource && ManagedRules.Ids.Contains(d.RuleId))
            .Select(d => $"{d.Path}({d.Line},{d.Column}): error: {d.RuleId} is suppressed in source"));
        return failures;
    }

    private static IEnumerable<int> NullableDisables(string source, Microsoft.CodeAnalysis.CSharp.CSharpParseOptions options) =>
        Microsoft.CodeAnalysis.CSharp.CSharpSyntaxTree.ParseText(File.ReadAllText(source), options)
            .GetRoot()
            .DescendantTrivia(descendIntoTrivia: true)
            .Select(trivia => trivia.GetStructure())
            .OfType<Microsoft.CodeAnalysis.CSharp.Syntax.NullableDirectiveTriviaSyntax>()
            .Where(directive => directive.SettingToken.IsKind(Microsoft.CodeAnalysis.CSharp.SyntaxKind.DisableKeyword))
            .Select(directive => directive.GetLocation().GetLineSpan().StartLinePosition.Line + 1);

    internal static ImmutableDictionary<string, string> EffectiveOptions(CompilationScan compilation, string source)
    {
        var options = EffectiveResults(compilation, source);
        return options.Global.AnalyzerOptions.SetItems(options.Source.AnalyzerOptions);
    }

    private static (AnalyzerConfigOptionsResult Global, AnalyzerConfigOptionsResult Source) EffectiveResults(
        CompilationScan compilation, string source)
    {
        var configs = compilation.Configs.Select(path => AnalyzerConfig.Parse(
            SourceText.From(File.ReadAllText(path)), path)).ToImmutableArray();
        var set = AnalyzerConfigSet.Create(configs, out var diagnostics);
        if (diagnostics.Any(d => d.Severity == DiagnosticSeverity.Error))
            throw new InvalidOperationException(string.Join("; ", diagnostics.Select(d => d.GetMessage())));
        return (set.GlobalConfigOptions, set.GetOptionsForSourcePath(source));
    }

    private static void RequireAnalyzer(CompilationScan compilation, string label, string fileName, List<string> failures)
    {
        if (!compilation.Analyzers.Any(path => Path.GetFileName(path).Equals(fileName, StringComparison.OrdinalIgnoreCase)))
            failures.Add($"{label}: required analyzer {fileName} is absent from actual csc inputs");
    }

    private static void RequireOwnedFile(List<string> paths, string name, string label, List<string> failures)
    {
        var matches = paths.Where(path => Path.GetFileName(path).Equals(name, StringComparison.OrdinalIgnoreCase)).ToList();
        if (matches.Count != 1)
        {
            failures.Add($"{label}: expected exactly one {name} compiler input, found {matches.Count}");
            return;
        }
        byte[] actual;
        try { actual = File.ReadAllBytes(matches[0]); }
        catch (IOException exception)
        {
            failures.Add($"{label}: cannot read {name}: {exception.Message}");
            return;
        }
        if (!actual.AsSpan().SequenceEqual(OwnedFiles[name]))
            failures.Add($"{label}: {name} content differs from the package-owned policy");
    }

    private static IReadOnlyDictionary<string, byte[]> LoadOwnedFiles()
    {
        var assembly = typeof(Policy).Assembly;
        var result = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
        foreach (var name in new[] { "IIMMPACT.CodeQuality.globalconfig", "SonarLint.xml", "CodeMetricsConfig.txt", "BannedSymbols.IIMMPACT.txt" })
        {
            using var stream = assembly.GetManifestResourceStream($"Policy.{name}")
                ?? throw new InvalidOperationException($"embedded policy resource missing: {name}");
            using var buffer = new MemoryStream();
            stream.CopyTo(buffer);
            result[name] = buffer.ToArray();
        }
        return result;
    }
}
