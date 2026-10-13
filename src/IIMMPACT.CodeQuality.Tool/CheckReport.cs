using System.Text;

/// <summary>
/// Renders `check` results: grouped console text and the optional GitHub step summary.
/// </summary>
internal static class CheckReport
{
    public const string StepSummaryVariable = "GITHUB_STEP_SUMMARY";
    public const int MaxSummaryRowsPerGroup = 50;

    public static string ReproduceCommand(string solutionArg, string commit) =>
        $"dotnet iimmpact-quality check {solutionArg} --base {commit}";

    /// <summary>
    /// Failure groups in report order, each group's lines in discovery order. The
    /// final line is the stable `FAIL:` summary.
    /// </summary>
    public static List<string> RenderFailure(
        IReadOnlyList<GateFailure> failures, string solutionArg, string commit)
    {
        var lines = new List<string>();
        foreach (var group in Groups(failures))
        {
            lines.Add($"{FailureGuidance.Title(group.Key)} ({group.Count()}): {FailureGuidance.Hint(group.Key)}");
            lines.AddRange(group.Select(f => f.Line));
            lines.Add(string.Empty);
        }
        lines.Add($"Reproduce locally: {ReproduceCommand(solutionArg, commit)}");
        lines.Add($"FAIL: {failures.Count} violation(s) over baseline {commit[..12]}");
        return lines;
    }

    public static string PassMessage(int diagnosticCount, string commit) =>
        $"PASS: {diagnosticCount} managed diagnostic(s) within trusted baseline {commit[..12]}";

    public static string FailureMarkdown(IReadOnlyList<GateFailure> failures, string solutionArg, string commit)
    {
        var markdown = new StringBuilder();
        markdown.AppendLine($"## Code quality gate failed: {failures.Count} violation(s) over baseline {commit[..12]}");
        markdown.AppendLine();
        foreach (var group in Groups(failures))
        {
            markdown.AppendLine($"### {FailureGuidance.Title(group.Key)} ({group.Count()})");
            markdown.AppendLine();
            markdown.AppendLine(FailureGuidance.Hint(group.Key));
            markdown.AppendLine();
            markdown.AppendLine("| Location | Rule | Declaration | Detail |");
            markdown.AppendLine("| --- | --- | --- | --- |");
            foreach (var failure in group.Take(MaxSummaryRowsPerGroup))
            {
                markdown.AppendLine(
                    $"| {Cell(failure.Location)} | {Cell(failure.Rule)} | {Cell(failure.Declaration)} | {Cell(failure.Detail)} |");
            }
            if (group.Count() > MaxSummaryRowsPerGroup)
            {
                markdown.AppendLine();
                markdown.AppendLine($"and {group.Count() - MaxSummaryRowsPerGroup} more, see the job log.");
            }
            markdown.AppendLine();
        }
        markdown.AppendLine($"Reproduce locally: `{ReproduceCommand(solutionArg, commit)}`");
        return markdown.ToString();
    }

    public static string PassMarkdown(int diagnosticCount, string commit) =>
        $"## Code quality gate passed\n\n{PassMessage(diagnosticCount, commit)}\n";

    /// <summary>
    /// Appends to the file named by GITHUB_STEP_SUMMARY. Does nothing when the
    /// variable is unset or empty; a write failure only warns, so it never changes
    /// the gate result.
    /// </summary>
    public static void AppendStepSummary(string markdown)
    {
        var path = Environment.GetEnvironmentVariable(StepSummaryVariable);
        if (string.IsNullOrEmpty(path))
        {
            return;
        }
        try
        {
            File.AppendAllText(path, markdown + "\n");
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            Console.Error.WriteLine($"warning: could not write {StepSummaryVariable}: {exception.Message}");
        }
    }

    private static IEnumerable<IGrouping<FailureKind, GateFailure>> Groups(IReadOnlyList<GateFailure> failures) =>
        failures.GroupBy(f => f.Kind).OrderBy(g => g.Key);

    private static string Cell(string text) => text
        .Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;")
        .Replace("|", "\\|")
        .Replace("\r\n", " ").Replace('\n', ' ').Replace('\r', ' ');
}
