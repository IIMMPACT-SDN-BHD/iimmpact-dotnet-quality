using Xunit;

namespace IIMMPACT.CodeQuality.Tool.Tests;

/// <summary>
/// The failure report is what an engineer or coding agent reads in CI: groups in a
/// fixed order, one hint per group, a reproduce command, and a stable last line.
/// </summary>
[Collection("StepSummaryEnvironment")]
public class CheckReportTests
{
    private const string Commit = "0123456789abcdef0123456789abcdef01234567";

    private static GateFailure Failure(FailureKind kind, string line = "line", string detail = "detail") =>
        new(kind, line, "src/Foo.cs(1,2)", "S138", "class Foo", detail);

    private static readonly GateFailure[] Mixed =
    [
        Failure(FailureKind.GeneratedExclusionChanged, "gen"),
        Failure(FailureKind.FrozenScopeChanged, "frozen"),
        Failure(FailureKind.NewDiagnostic, "new-1"),
        Failure(FailureKind.MetricAboveCeiling, "metric"),
        Failure(FailureKind.BaselineNotReduced, "baseline"),
        Failure(FailureKind.NewDiagnostic, "new-2"),
    ];

    [Fact]
    public void RenderFailure_OrdersGroupsAndCountsThem()
    {
        var lines = CheckReport.RenderFailure(Mixed, "My App.slnx", Commit);

        var headers = lines.Where(l => l.Contains(" ("))
            .Select(l => l[..l.IndexOf(':')]).ToArray();
        Assert.Equal(
            [
                "Baseline not reduced (1)", "New diagnostics (2)", "Metrics above approved ceiling (1)",
                "Frozen scopes changed (1)", "Generated sources changed (1)",
            ],
            headers);
        Assert.Equal(
            ["baseline", "new-1", "new-2", "metric", "frozen", "gen"],
            lines.Where(l => l is "baseline" or "new-1" or "new-2" or "metric" or "frozen" or "gen"));
    }

    [Fact]
    public void RenderFailure_PutsTheHintOnEveryGroupHeader()
    {
        var lines = CheckReport.RenderFailure(Mixed, "App.slnx", Commit);

        foreach (var kind in Enum.GetValues<FailureKind>().Where(k => Mixed.Any(f => f.Kind == k)))
        {
            Assert.Contains(lines, l => l.StartsWith($"{FailureGuidance.Title(kind)} (") && l.EndsWith(FailureGuidance.Hint(kind)));
        }
    }

    [Fact]
    public void EveryKind_HasATitleAndHint()
    {
        foreach (var kind in Enum.GetValues<FailureKind>())
        {
            Assert.False(string.IsNullOrWhiteSpace(FailureGuidance.Title(kind)));
            Assert.False(string.IsNullOrWhiteSpace(FailureGuidance.Hint(kind)));
        }
    }

    [Fact]
    public void RenderFailure_EndsWithReproduceThenTheStableFailLine()
    {
        var lines = CheckReport.RenderFailure(Mixed, "My App.slnx", Commit);

        Assert.Equal($"Reproduce locally: dotnet iimmpact-quality check My App.slnx --base {Commit}", lines[^2]);
        Assert.Equal("FAIL: 6 violation(s) over baseline 0123456789ab", lines[^1]);
    }

    [Fact]
    public void RenderFailure_KeepsEachFailureLineVerbatim()
    {
        var lines = CheckReport.RenderFailure([Failure(FailureKind.NewDiagnostic, "a(1,2): error S1: m [d: r]")], "A.slnx", Commit);

        Assert.Contains("a(1,2): error S1: m [d: r]", lines);
    }

    [Fact]
    public void FailureMarkdown_HasATablePerGroupAndTheReproduceCommand()
    {
        var markdown = CheckReport.FailureMarkdown(Mixed, "App.slnx", Commit);

        Assert.Contains("### New diagnostics (2)", markdown);
        Assert.Contains(FailureGuidance.Hint(FailureKind.NewDiagnostic), markdown);
        Assert.Contains("| Location | Rule | Declaration | Detail |", markdown);
        Assert.Contains("| src/Foo.cs(1,2) | S138 | class Foo | detail |", markdown);
        Assert.Contains($"`dotnet iimmpact-quality check App.slnx --base {Commit}`", markdown);
    }

    [Fact]
    public void FailureMarkdown_EscapesPipesNewlinesAndHtml()
    {
        var failure = Failure(FailureKind.NewDiagnostic, detail: "a|b\r\nc <T> & d");

        var markdown = CheckReport.FailureMarkdown([failure], "App.slnx", Commit);

        Assert.Contains("| a\\|b c &lt;T&gt; &amp; d |", markdown);
    }

    [Fact]
    public void FailureMarkdown_CapsRowsPerGroup()
    {
        var failures = Enumerable.Range(0, CheckReport.MaxSummaryRowsPerGroup + 7)
            .Select(i => Failure(FailureKind.NewDiagnostic, detail: $"row-{i}.")).ToArray();

        var markdown = CheckReport.FailureMarkdown(failures, "App.slnx", Commit);

        Assert.Contains($"row-{CheckReport.MaxSummaryRowsPerGroup - 1}.", markdown);
        Assert.DoesNotContain($"row-{CheckReport.MaxSummaryRowsPerGroup}.", markdown);
        Assert.Contains("and 7 more, see the job log.", markdown);
    }

    [Fact]
    public void AppendStepSummary_WritesOnlyWhenTheVariableIsSet()
    {
        var path = Path.Combine(Path.GetTempPath(), $"step-summary-{Guid.NewGuid():N}.md");
        try
        {
            using (EnvironmentVariable.Set(CheckReport.StepSummaryVariable, null))
            {
                CheckReport.AppendStepSummary("unset");
            }
            using (EnvironmentVariable.Set(CheckReport.StepSummaryVariable, string.Empty))
            {
                CheckReport.AppendStepSummary("empty");
            }
            Assert.False(File.Exists(path));

            using (EnvironmentVariable.Set(CheckReport.StepSummaryVariable, path))
            {
                CheckReport.AppendStepSummary("first");
                CheckReport.AppendStepSummary("second");
            }
            Assert.Equal("first\nsecond\n", File.ReadAllText(path));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void AppendStepSummary_WarnsInsteadOfThrowingWhenTheFileCannotBeWritten()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"step-summary-dir-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var error = new StringWriter();
        var originalError = Console.Error;
        Console.SetError(error);
        try
        {
            using (EnvironmentVariable.Set(CheckReport.StepSummaryVariable, directory))
            {
                CheckReport.AppendStepSummary("text");
            }
        }
        finally
        {
            Console.SetError(originalError);
            Directory.Delete(directory);
        }
        Assert.Contains("warning: could not write GITHUB_STEP_SUMMARY", error.ToString());
    }

    private sealed class EnvironmentVariable : IDisposable
    {
        private readonly string _name;
        private readonly string? _previous;

        private EnvironmentVariable(string name, string? value)
        {
            _name = name;
            _previous = Environment.GetEnvironmentVariable(name);
            Environment.SetEnvironmentVariable(name, value);
        }

        public static EnvironmentVariable Set(string name, string? value) => new(name, value);

        public void Dispose() => Environment.SetEnvironmentVariable(_name, _previous);
    }
}

[CollectionDefinition("StepSummaryEnvironment", DisableParallelization = true)]
public sealed class StepSummaryEnvironmentCollection;
