using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

internal static class ManagedRules
{
    public static readonly IReadOnlySet<string> OccurrenceIds = new HashSet<string>(StringComparer.Ordinal)
    {
        "S4462", "CA1849", "CA2016", "CA2254", "RS0030", "IDE0051", "IDE0052", "IDE0060",
    };

    public static readonly IReadOnlySet<string> MetricIds = new HashSet<string>(StringComparer.Ordinal)
    {
        "CA1502", "CA1506", "S104", "S138",
    };

    public static readonly IReadOnlySet<string> FrozenScopeIds = new HashSet<string>(StringComparer.Ordinal) { "S134" };
    public static readonly IReadOnlySet<string> Ids = OccurrenceIds.Union(MetricIds).Union(FrozenScopeIds).ToHashSet(StringComparer.Ordinal);

    public static long? ParseMetric(string ruleId, string message)
    {
        var pattern = ruleId switch
        {
            "CA1502" => @"cyclomatic complexity of '?([0-9]+)'?",
            "CA1506" => @"coupled with '?([0-9]+)'? different types",
            "S138" or "S104" => @"has '?([0-9]+)'? lines",
            _ => null,
        };
        if (pattern is null) return null;
        var match = Regex.Match(message, pattern, RegexOptions.CultureInvariant);
        return match.Success && long.TryParse(match.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var value)
            ? value : null;
    }
}

internal sealed record Diagnostic(
    string RuleId, string Path, int Line, int Column, string Message, long? Metric, bool SuppressedInSource);

internal sealed record DiagnosticSite(string Path, int Line, int Column);
internal sealed record TokenSite(int Token, int Offset);

internal static class SarifLogParser
{
    public static List<Diagnostic> Parse(string sarifJson, string solutionDir)
    {
        try
        {
            using var document = JsonDocument.Parse(sarifJson);
            if (!document.RootElement.TryGetProperty("version", out var version)
                || version.ValueKind != JsonValueKind.String || version.GetString() != "2.1.0")
                throw new ScanException("SARIF version must be 2.1.0");
            if (!document.RootElement.TryGetProperty("runs", out var runs) || runs.ValueKind != JsonValueKind.Array || runs.GetArrayLength() == 0)
                throw new ScanException("SARIF contains no runs");
            var diagnostics = new List<Diagnostic>();
            foreach (var run in runs.EnumerateArray())
            {
                if (!run.TryGetProperty("results", out var results) || results.ValueKind != JsonValueKind.Array)
                    throw new ScanException("SARIF run contains no results array");
                foreach (var result in results.EnumerateArray())
                {
                    if (!result.TryGetProperty("ruleId", out var ruleElement) || ruleElement.ValueKind != JsonValueKind.String
                        || string.IsNullOrWhiteSpace(ruleElement.GetString()))
                        throw new ScanException("SARIF result has no ruleId");
                    var rule = ruleElement.GetString()!;
                    if (!ManagedRules.Ids.Contains(rule) && !rule.StartsWith("AD", StringComparison.Ordinal)) continue;
                    var message = ReadMessage(result, rule);
                    if (rule.StartsWith("AD", StringComparison.Ordinal))
                    {
                        diagnostics.Add(new Diagnostic(rule, string.Empty, 0, 0, message, null, false));
                        continue;
                    }
                    var (uri, line, column) = ReadRequiredLocation(result, rule);
                    var path = ToRelativePath(uri, solutionDir);
                    if (line <= 0 || column <= 0) throw new ScanException($"managed SARIF result {rule} has an invalid location");
                    var suppressed = result.TryGetProperty("suppressions", out var suppressions)
                        && suppressions.ValueKind == JsonValueKind.Array
                        && suppressions.EnumerateArray().Any(s => s.TryGetProperty("kind", out var kind) && kind.GetString() == "inSource");
                    diagnostics.Add(new Diagnostic(rule, path, line, column, message, ManagedRules.ParseMetric(rule, message), suppressed));
                }
            }
            return diagnostics;
        }
        catch (ScanException) { throw; }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException or UriFormatException)
        {
            throw new ScanException($"malformed SARIF: {exception.Message}");
        }
    }

    private static string ReadMessage(JsonElement result, string rule)
    {
        if (!result.TryGetProperty("message", out var message)
            || !message.TryGetProperty("text", out var text)
            || text.ValueKind != JsonValueKind.String
            || string.IsNullOrWhiteSpace(text.GetString()))
            throw new ScanException($"managed SARIF result {rule} has no message");
        return text.GetString()!;
    }

    private static (string Uri, int Line, int Column) ReadRequiredLocation(JsonElement result, string rule)
    {
        if (!result.TryGetProperty("locations", out var locations)
            || locations.ValueKind != JsonValueKind.Array || locations.GetArrayLength() != 1
            || !locations[0].TryGetProperty("physicalLocation", out var physical)
            || !physical.TryGetProperty("artifactLocation", out var artifact)
            || !artifact.TryGetProperty("uri", out var uri)
            || uri.ValueKind != JsonValueKind.String
            || !physical.TryGetProperty("region", out var region)
            || !region.TryGetProperty("startLine", out var line) || !line.TryGetInt32(out var lineNumber)
            || !region.TryGetProperty("startColumn", out var column) || !column.TryGetInt32(out var columnNumber))
            throw new ScanException($"managed SARIF result {rule} must have exactly one physical location");
        return (uri.GetString()!, lineNumber, columnNumber);
    }

    private static string ToRelativePath(string uri, string solutionDir)
    {
        var path = Uri.TryCreate(uri, UriKind.Absolute, out var parsed) && parsed.IsFile
            ? parsed.LocalPath
            : Path.IsPathRooted(uri) ? uri : Path.GetFullPath(Path.Combine(solutionDir, uri));
        return QualityGate.RelativePath(solutionDir, QualityGate.RealPath(path));
    }
}
