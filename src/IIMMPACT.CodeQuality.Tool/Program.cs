// Lets the compiler command-line parser accept legacy <CodePage> settings such as 1252.
System.Text.Encoding.RegisterProvider(System.Text.CodePagesEncodingProvider.Instance);
return await QualityGate.RunAsync(args);

/// <summary>
/// Entry points for `iimmpact-quality check`, `baseline` and `bootstrap`.
/// All three build a validated scan of every compilation, then compare against the
/// schema-2 baseline committed at a trusted Git revision.
/// </summary>
internal static class QualityGate
{
    private const string BaselineFileName = "code-quality-baseline.json";

    public static async Task<int> RunAsync(string[] args)
    {
        var parsed = ParseArgs(args);
        if (parsed is null)
        {
            Console.Error.WriteLine(
                "usage:\n" +
                "  iimmpact-quality check <solution.sln|slnx> --base <revision>\n" +
                "  iimmpact-quality baseline <solution.sln|slnx> --base <revision>\n" +
                "  iimmpact-quality bootstrap <solution.sln|slnx> --source <revision>");
            return 2;
        }
        var (command, solutionArg, _, revision) = parsed.Value;

        var solutionPath = Path.GetFullPath(solutionArg);
        if (!File.Exists(solutionPath))
        {
            Console.Error.WriteLine($"solution not found: {solutionPath}");
            return 2;
        }
        var solutionDir = RealPath(Path.GetDirectoryName(solutionPath)!);

        try
        {
            var repoRoot = RealPath(await Git.RepositoryRootAsync(solutionDir));
            var commit = await Git.ResolveCommitAsync(repoRoot, revision);
            var projects = await ListProjectsAsync(solutionPath);
            if (projects.Count == 0)
            {
                Console.Error.WriteLine("no projects found in solution");
                return 2;
            }

            return command switch
            {
                "bootstrap" => await RunBootstrapAsync(solutionPath, solutionDir, projects, repoRoot, commit),
                "baseline" => await RunBaselineAsync(solutionPath, solutionDir, projects, repoRoot, commit),
                _ => await RunCheckAsync(solutionPath, solutionDir, projects, repoRoot, commit),
            };
        }
        catch (ScanException exception)
        {
            Console.Error.WriteLine(exception.Message);
            return 2;
        }
        catch (BaselineFormatException exception)
        {
            Console.Error.WriteLine($"invalid baseline: {exception.Message}");
            return 2;
        }
        catch (GateFailureException exception)
        {
            Console.Error.WriteLine(exception.Message);
            return 1;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or System.Xml.XmlException or ArgumentException)
        {
            Console.Error.WriteLine($"invalid input or analysis evidence: {exception.Message}");
            return 2;
        }
    }

    private static (string Command, string Solution, string OptionName, string Revision)? ParseArgs(string[] args)
    {
        if (args.Length != 4 || args[0] is not ("check" or "baseline" or "bootstrap") || string.IsNullOrWhiteSpace(args[1]))
        {
            return null;
        }
        var option = args[0] == "bootstrap" ? "--source" : "--base";
        if (args[2] != option)
        {
            return null;
        }
        return (args[0], args[1], option, args[3]);
    }

    /// <summary>
    /// Resolves every symlink component so the repo root and evidence paths share
    /// one canonical prefix; without it Path.GetRelativePath produces "../" paths
    /// for files that are really inside the repository (e.g. /var vs /private/var).
    /// </summary>
    internal static string RealPath(string path)
    {
        var full = Path.GetFullPath(path);
        var root = Path.GetPathRoot(full)!;
        var current = root;
        foreach (var segment in full[root.Length..].Split(Path.DirectorySeparatorChar))
        {
            current = Path.Combine(current, segment);
            var info = new FileInfo(current);
            while (info.LinkTarget is { } target)
            {
                current = Path.GetFullPath(target, Path.GetDirectoryName(current)!);
                info = new FileInfo(current);
            }
        }
        return current;
    }

    /// <summary>dotnet sln list output: relative .csproj paths, one per line.</summary>
    private static async Task<List<string>> ListProjectsAsync(string solutionPath)
    {
        var (exit, output) = await ScanCollector.RunProcessAsync(
            "dotnet", ["sln", solutionPath, "list"], Path.GetDirectoryName(solutionPath)!);
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
    /// check: compare the candidate scan against the baseline committed at --base.
    /// Exit 1 on gate failure, 2 on invalid input/build/analysis.
    /// </summary>
    private static async Task<int> RunCheckAsync(
        string solutionPath, string solutionDir, List<string> projects, string repoRoot, string commit)
    {
        var baseline = await TrustedBaselineAsync(repoRoot, commit, solutionDir);
        var candidate = CandidateBaseline(solutionDir);
        var failures = candidate.ValidateReductionOf(baseline);
        using var scan = await ScanCollector.CollectAsync(solutionPath, solutionDir, projects);

        failures.AddRange(ScanCollector.CompareGeneratedExclusions(scan, candidate.GeneratedExclusions));
        failures.AddRange(candidate.Compare(scan.Violations));

        if (failures.Count == 0)
        {
            Console.WriteLine(
                $"PASS: {scan.Violations.Count} managed diagnostic(s) within trusted baseline {commit[..12]}");
            return 0;
        }
        foreach (var failure in failures)
        {
            Console.WriteLine(failure);
        }
        Console.WriteLine($"FAIL: {failures.Count} violation(s) over baseline {commit[..12]}");
        return 1;
    }

    /// <summary>
    /// baseline: rewrite the checked-in baseline with reductions only. Never raises a
    /// ceiling, never adds an allowance, never invents exclusions.
    /// </summary>
    private static async Task<int> RunBaselineAsync(
        string solutionPath, string solutionDir, List<string> projects, string repoRoot, string commit)
    {
        var baseline = await TrustedBaselineAsync(repoRoot, commit, solutionDir);
        var candidate = CandidateBaseline(solutionDir);
        var candidateFailures = candidate.ValidateReductionOf(baseline);
        if (candidateFailures.Count > 0)
        {
            foreach (var failure in candidateFailures) Console.Error.WriteLine(failure);
            Console.Error.WriteLine("baseline refused: candidate baseline is not a reduction of the trusted baseline");
            return 1;
        }
        using var scan = await ScanCollector.CollectAsync(solutionPath, solutionDir, projects);

        var failures = new List<string>();
        failures.AddRange(ScanCollector.CompareGeneratedExclusions(scan, candidate.GeneratedExclusions));
        if (failures.Count > 0)
        {
            foreach (var failure in failures)
            {
                Console.Error.WriteLine(failure);
            }
            Console.Error.WriteLine("baseline refused: the scan itself violates the policy");
            return 2;
        }

        var reduced = candidate.Reduce(scan.Violations, out var stillOver);
        if (stillOver.Count > 0)
        {
            foreach (var violation in stillOver)
            {
                var site = violation.PhysicalSite;
                var location = $"{site.Path}({site.Line},{site.Column})";
                Console.Error.WriteLine(
                    $"{location}: error {violation.Rule}: {violation.Message} " +
                    $"[{violation.Entity.Declaration}: still over approved ceiling]");
            }
            Console.Error.WriteLine("baseline refused: reductions only; violations above the baseline remain");
            return 1;
        }
        if (reduced.ToJson() == candidate.ToJson())
        {
            Console.WriteLine($"baseline already matches scan at {commit[..12]}; nothing to reduce");
            return 0;
        }

        var baselinePath = Path.Combine(solutionDir, BaselineFileName);
        File.WriteAllText(baselinePath, reduced.ToJson());
        Console.WriteLine(
            $"wrote {BaselineFileName}: reduced from {candidate.Allowances.Count} to " +
            $"{reduced.Allowances.Count} allowance(s)");
        return 0;
    }

    /// <summary>
    /// bootstrap: propose a baseline for the source revision. Only produces a file —
    /// nothing approves it. Handwritten compilation inputs must match the source
    /// revision exactly; generated files are recorded separately.
    /// </summary>
    private static async Task<int> RunBootstrapAsync(
        string solutionPath, string solutionDir, List<string> projects, string repoRoot, string commit)
    {
        var baselinePath = Path.Combine(solutionDir, BaselineFileName);
        if (File.Exists(baselinePath))
        {
            Console.Error.WriteLine($"bootstrap refused: {BaselineFileName} already exists; bootstrap is proposal-only and never overwrites it");
            return 1;
        }
        using var scan = await ScanCollector.CollectAsync(solutionPath, solutionDir, projects);

        // Every handwritten compile input must match the approved source commit.
        var mismatches = await Bootstrap.ValidateSourcesAsync(repoRoot, commit, scan, solutionDir);
        if (mismatches.Count > 0)
        {
            foreach (var mismatch in mismatches)
            {
                Console.Error.WriteLine(mismatch);
            }
            Console.Error.WriteLine(
                "bootstrap refused: compilation inputs differ from the approved source revision");
            return 1;
        }

        // One allowance per (entity, rule): a declaration that repeats a banned call
        // produces several diagnostics, which merge into a single frozen allowance
        // whose sites list every call. Metric rules collapse to the worst measured value.
        var allowances = scan.Violations
            .GroupBy(v => (v.Entity, v.Rule))
            .Select(group =>
            {
                var ceiling = group.Max(v => v.Metric);
                if (ceiling is { } metric)
                {
                    return (Allowance)new MetricAllowance(group.Key.Entity, group.Key.Rule, metric);
                }
                var sites = group
                    .SelectMany(v => v.TokenSites)
                    .OrderBy(s => s.Token)
                    .ThenBy(s => s.Offset)
                    .ToArray();
                return new FrozenScopeAllowance(
                    group.Key.Entity, group.Key.Rule,
                    group.Select(v => v.TokenDigest).Single(),
                    sites);
            });
        var baseline = Baseline.FromAllowances(allowances, scan.ObservedGeneratedFiles);
        File.WriteAllText(baselinePath, baseline.ToJson());
        Console.WriteLine(
            $"wrote {BaselineFileName}: {baseline.Allowances.Count} allowance(s) proposed " +
            $"from {commit[..12]}; review and merge before trusting");
        return 0;
    }

    /// <summary>Reads the baseline committed at the trusted revision, or fails.</summary>
    private static async Task<Baseline> TrustedBaselineAsync(
        string repoRoot, string commit, string solutionDir)
    {
        var repoRelative = Path.GetRelativePath(repoRoot, Path.Combine(solutionDir, BaselineFileName));
        var json = await Git.ReadFileAsync(repoRoot, commit, repoRelative);
        if (json is null)
        {
            throw new GateFailureException(
                $"baseline not found at {commit[..12]}:{repoRelative}\n" +
                "commit a schema-2 baseline produced by `iimmpact-quality bootstrap` first");
        }
        return Baseline.Parse(json);
    }

    private static Baseline CandidateBaseline(string solutionDir)
    {
        var path = Path.Combine(solutionDir, BaselineFileName);
        if (!File.Exists(path))
            throw new BaselineFormatException($"candidate baseline not found: {path}");
        return Baseline.Parse(File.ReadAllText(path));
    }

    /// <summary>Solution-relative path with forward slashes for stable baselines.</summary>
    public static string RelativePath(string solutionDir, string path)
    {
        var relative = Path.GetRelativePath(solutionDir, path);
        return relative.Replace(Path.DirectorySeparatorChar, '/');
    }
}

internal sealed class GateFailureException : Exception
{
    public GateFailureException(string message) : base(message) { }
}
