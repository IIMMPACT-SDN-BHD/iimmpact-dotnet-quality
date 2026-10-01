using System.Text.Json;
using System.Text.RegularExpressions;

/// <summary>The rule IDs IIMMPACT.CodeQuality manages. Diagnostics for any other rule are ignored.</summary>
internal static class ManagedRules
{
    public static readonly IReadOnlySet<string> Ids = new HashSet<string>(StringComparer.Ordinal)
    {
        "CA1502", "CA1506",
        "S104", "S134", "S138", "S4462",
        "CA1849", "CA2016", "CA2254",
        "RS0030",
        "IDE0051", "IDE0052", "IDE0060",
    };

    /// <summary>Rules whose message carries a measured value that the baseline tracks as "max".</summary>
    public static readonly IReadOnlySet<string> MetricIds = new HashSet<string>(StringComparer.Ordinal)
    {
        "CA1502", "CA1506", "S104", "S138",
    };

    /// <summary>Extracts the measured value for a metric rule from its diagnostic message.</summary>
    public static long? ParseMetric(string ruleId, string message)
    {
        var pattern = ruleId switch
        {
            // CA1502/CA1506 wrap the number in single quotes on newer SDKs
            // ("complexity of '28'"); older SDKs print it bare. Allow both.
            "CA1502" => @"cyclomatic complexity of '?(\d+)'?",
            "CA1506" => @"coupled with '?(\d+)'? different types",
            // S138: "This method 'x' has 61 lines, which is greater than the 60 lines authorized."
            // S104: "This file has 408 lines, which is greater than 400 authorized."
            "S138" or "S104" => @"has '?(\d+)'? lines",
            _ => null,
        };
        if (pattern is null)
        {
            return null;
        }
        var match = Regex.Match(message, pattern);
        return match.Success ? long.Parse(match.Groups[1].Value) : null;
    }
}

/// <summary>Reads managed-rule diagnostics out of a SARIF 2.1 error log.</summary>
internal static class SarifLogParser
{
    public static List<Diagnostic> Parse(string sarifJson, string solutionDir)
    {
        var diagnostics = new List<Diagnostic>();
        using var document = JsonDocument.Parse(sarifJson);
        if (!document.RootElement.TryGetProperty("runs", out var runs))
        {
            return diagnostics;
        }

        foreach (var run in runs.EnumerateArray())
        {
            if (!run.TryGetProperty("results", out var results))
            {
                continue;
            }
            foreach (var result in results.EnumerateArray())
            {
                if (result.TryGetProperty("ruleId", out var ruleIdElement)
                    && ruleIdElement.GetString() is { } ruleId
                    && ManagedRules.Ids.Contains(ruleId)
                    && TryReadLocation(result, out var uri, out var line, out var column))
                {
                    var message = result.TryGetProperty("message", out var messageElement)
                        && messageElement.TryGetProperty("text", out var textElement)
                            ? textElement.GetString() ?? string.Empty
                            : string.Empty;
                    var path = ToRelativePath(uri, solutionDir);
                    diagnostics.Add(new Diagnostic(
                        ruleId, path, line, column, message,
                        ManagedRules.ParseMetric(ruleId, message)));
                }
            }
        }
        return diagnostics;
    }

    private static bool TryReadLocation(
        JsonElement result, out string uri, out int line, out int column)
    {
        uri = string.Empty;
        line = 0;
        column = 0;
        if (!result.TryGetProperty("locations", out var locations)
            || locations.GetArrayLength() == 0
            || !locations[0].TryGetProperty("physicalLocation", out var physical)
            || !physical.TryGetProperty("artifactLocation", out var artifact)
            || !artifact.TryGetProperty("uri", out var uriElement)
            || uriElement.GetString() is not { } value)
        {
            return false;
        }
        uri = value;
        if (physical.TryGetProperty("region", out var region))
        {
            if (region.TryGetProperty("startLine", out var lineElement))
            {
                line = lineElement.GetInt32();
            }
            if (region.TryGetProperty("startColumn", out var columnElement))
            {
                column = columnElement.GetInt32();
            }
        }
        return true;
    }

    /// <summary>SARIF uris may be file:// uris, absolute paths, or relative paths.</summary>
    private static string ToRelativePath(string uri, string solutionDir)
    {
        string path;
        if (Uri.TryCreate(uri, UriKind.Absolute, out var parsed) && parsed.IsFile)
        {
            path = parsed.LocalPath;
        }
        else if (Path.IsPathRooted(uri))
        {
            path = uri;
        }
        else
        {
            path = Path.GetFullPath(Path.Combine(solutionDir, uri));
        }
        return QualityGate.RelativePath(solutionDir, path);
    }
}

/// <summary>
/// The baseline shape: file path -> rule id -> count and (for metric rules) max.
/// Sorted keys and fixed layout so the file is diff-stable.
/// </summary>
internal sealed class BaselineState
{
    private readonly SortedDictionary<string, SortedDictionary<string, Entry>> _files = new(StringComparer.Ordinal);

    public sealed record Entry(int Count, long? Max);

    public int FileCount => _files.Count;

    public int TotalCount => _files.Values.Sum(rules => rules.Values.Sum(e => e.Count));

    public static BaselineState FromDiagnostics(IEnumerable<Diagnostic> diagnostics)
    {
        var state = new BaselineState();
        foreach (var group in diagnostics.GroupBy(d => (d.Path, d.RuleId)))
        {
            var rules = state.RulesFor(group.Key.Path);
            var max = ManagedRules.MetricIds.Contains(group.Key.RuleId)
                ? group.Max(d => d.Metric ?? 0)
                : (long?)null;
            rules[group.Key.RuleId] = new Entry(group.Count(), max);
        }
        return state;
    }

    public static BaselineState Parse(string json)
    {
        var state = new BaselineState();
        using var document = JsonDocument.Parse(json);
        foreach (var file in document.RootElement.EnumerateObject())
        {
            var rules = state.RulesFor(file.Name);
            foreach (var rule in file.Value.EnumerateObject())
            {
                var count = rule.Value.GetProperty("count").GetInt32();
                var max = rule.Value.TryGetProperty("max", out var maxElement)
                    ? maxElement.GetInt64()
                    : (long?)null;
                rules[rule.Name] = new Entry(count, max);
            }
        }
        return state;
    }

    public string ToJson()
    {
        var options = new JsonSerializerOptions { WriteIndented = true };
        var shape = _files.ToDictionary(
            file => file.Key,
            file => file.Value.ToDictionary(
                rule => rule.Key,
                rule => rule.Value.Max is { } max
                    ? new Dictionary<string, object> { ["count"] = rule.Value.Count, ["max"] = max }
                    : new Dictionary<string, object> { ["count"] = rule.Value.Count }));
        return JsonSerializer.Serialize(shape, options) + "\n";
    }

    /// <summary>
    /// Compares this baseline against the current run. Diagnostics are the current run's,
    /// used to print every offending line for groups that grew. A group fails when it is
    /// new, has more occurrences, or has a larger metric. A baseline entry that is missing
    /// or lower now is stale and also fails, so the baseline only shrinks.
    /// </summary>
    public List<string> Compare(BaselineState current, IReadOnlyList<Diagnostic> diagnostics)
    {
        var failures = new List<string>();

        foreach (var (path, rules) in current._files)
        {
            foreach (var (ruleId, currentEntry) in rules)
            {
                string? reason = null;
                if (!_files.TryGetValue(path, out var baselineRules)
                    || !baselineRules.TryGetValue(ruleId, out var baselineEntry))
                {
                    reason = "new diagnostics not in baseline";
                }
                else if (currentEntry.Count > baselineEntry.Count)
                {
                    reason = $"count {currentEntry.Count} exceeds baseline {baselineEntry.Count}";
                }
                else if (currentEntry.Max is { } currentMax
                    && baselineEntry.Max is { } baselineMax
                    && currentMax > baselineMax)
                {
                    reason = $"metric {currentMax} exceeds baseline max {baselineMax}";
                }

                if (reason is not null)
                {
                    failures.Add(FormatGroup(diagnostics, path, ruleId, reason));
                }
            }
        }

        foreach (var (path, baselineRules) in _files)
        {
            foreach (var (ruleId, baselineEntry) in baselineRules)
            {
                if (!current._files.TryGetValue(path, out var rules)
                    || !rules.TryGetValue(ruleId, out var currentEntry))
                {
                    failures.Add(
                        $"{path}: error {ruleId}: stale baseline entry " +
                        $"(was {FormatEntry(baselineEntry)}, now absent); " +
                        "run `iimmpact-quality baseline` to lower it");
                    continue;
                }
                if (currentEntry.Count < baselineEntry.Count
                    || (currentEntry.Max is { } currentMax
                        && baselineEntry.Max is { } baselineMax
                        && currentMax < baselineMax))
                {
                    failures.Add(
                        $"{path}: error {ruleId}: stale baseline entry " +
                        $"(was {FormatEntry(baselineEntry)}, now {FormatEntry(currentEntry)}); " +
                        "run `iimmpact-quality baseline` to lower it");
                }
            }
        }

        return failures;
    }

    /// <summary>Every diagnostic of a failed group formatted like compiler errors.</summary>
    private static string FormatGroup(
        IReadOnlyList<Diagnostic> diagnostics, string path, string ruleId, string reason)
    {
        var lines = diagnostics
            .Where(d => d.Path == path && d.RuleId == ruleId)
            .Select(d => $"{d.Path}({d.Line},{d.Column}): error {d.RuleId}: {d.Message}")
            .ToList();
        lines.Add($"  -> {ruleId}: {reason}");
        return string.Join('\n', lines);
    }

    private SortedDictionary<string, Entry> RulesFor(string path)
    {
        if (!_files.TryGetValue(path, out var rules))
        {
            rules = new SortedDictionary<string, Entry>(StringComparer.Ordinal);
            _files[path] = rules;
        }
        return rules;
    }

    private static string FormatEntry(Entry entry) =>
        entry.Max is { } max
            ? $"count {entry.Count}, max {max}"
            : $"count {entry.Count}";
}
