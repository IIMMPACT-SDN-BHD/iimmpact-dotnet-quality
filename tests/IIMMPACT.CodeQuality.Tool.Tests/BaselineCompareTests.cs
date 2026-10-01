using Xunit;

namespace IIMMPACT.CodeQuality.Tool.Tests;

/// <summary>
/// Baseline comparison is the gate's core behavior: growth fails, shrinkage is stale,
/// equality passes. Each test builds literal diagnostics and baseline JSON and asserts
/// the observable failure strings.
/// </summary>
public class BaselineCompareTests
{
    private static Diagnostic Diag(string path, string rule, int line, string message = "message", long? metric = null) =>
        new(rule, path, line, 5, message, metric);

    private static BaselineState Current(params Diagnostic[] diagnostics) =>
        BaselineState.FromDiagnostics(diagnostics);

    private static BaselineState Baseline(string json) => BaselineState.Parse(json);

    [Fact]
    public void Pass_WhenDiagnosticsMatchBaseline()
    {
        var baseline = Baseline("""
            {
              "src/Foo.cs": {
                "S134": { "count": 1 }
              }
            }
            """);
        var diagnostics = new List<Diagnostic> { Diag("src/Foo.cs", "S134", 10) };
        var failures = baseline.Compare(Current(diagnostics.ToArray()), diagnostics);
        Assert.Empty(failures);
    }

    [Fact]
    public void Fails_WhenRuleAppearsInNewFile()
    {
        var baseline = Baseline("""
            {
              "src/Foo.cs": {
                "S134": { "count": 1 }
              }
            }
            """);
        var diagnostics = new List<Diagnostic>
        {
            Diag("src/Foo.cs", "S134", 10),
            Diag("src/Bar.cs", "S134", 3),
        };
        var failures = baseline.Compare(Current(diagnostics.ToArray()), diagnostics);
        var failure = Assert.Single(failures);
        Assert.Contains("src/Bar.cs(3,5): error S134: message", failure);
        Assert.Contains("new diagnostics not in baseline", failure);
    }

    [Fact]
    public void Fails_WhenCountGrows()
    {
        var baseline = Baseline("""
            {
              "src/Foo.cs": {
                "S134": { "count": 1 }
              }
            }
            """);
        var diagnostics = new List<Diagnostic>
        {
            Diag("src/Foo.cs", "S134", 10),
            Diag("src/Foo.cs", "S134", 20),
        };
        var failures = baseline.Compare(Current(diagnostics.ToArray()), diagnostics);
        var failure = Assert.Single(failures);
        Assert.Contains("src/Foo.cs(10,5): error S134", failure);
        Assert.Contains("src/Foo.cs(20,5): error S134", failure);
        Assert.Contains("count 2 exceeds baseline 1", failure);
    }

    [Fact]
    public void Fails_WhenMetricGrows()
    {
        var baseline = Baseline("""
            {
              "src/Foo.cs": {
                "S138": { "count": 1, "max": 61 }
              }
            }
            """);
        var diagnostics = new List<Diagnostic>
        {
            Diag("src/Foo.cs", "S138", 4,
                "This method 'Run' has 65 lines, which is greater than the 60 lines authorized.",
                metric: 65),
        };
        var failures = baseline.Compare(Current(diagnostics.ToArray()), diagnostics);
        var failure = Assert.Single(failures);
        Assert.Contains("metric 65 exceeds baseline max 61", failure);
    }

    [Fact]
    public void Fails_WhenBaselineEntryDisappears()
    {
        var baseline = Baseline("""
            {
              "src/Foo.cs": {
                "S134": { "count": 1 }
              }
            }
            """);
        var failures = baseline.Compare(Current(), []);
        var failure = Assert.Single(failures);
        Assert.Contains("src/Foo.cs: error S134: stale baseline entry (was count 1, now absent)", failure);
        Assert.Contains("lower it", failure);
    }

    [Fact]
    public void Fails_WhenBaselineEntryIsLowerNow()
    {
        var baseline = Baseline("""
            {
              "src/Foo.cs": {
                "S134": { "count": 2 }
              }
            }
            """);
        var diagnostics = new List<Diagnostic> { Diag("src/Foo.cs", "S134", 10) };
        var failures = baseline.Compare(Current(diagnostics.ToArray()), diagnostics);
        var failure = Assert.Single(failures);
        Assert.Contains("stale baseline entry (was count 2, now count 1)", failure);
    }

    [Fact]
    public void Fails_WhenBaselineMetricIsLowerNow()
    {
        var baseline = Baseline("""
            {
              "src/Foo.cs": {
                "S138": { "count": 1, "max": 61 }
              }
            }
            """);
        var diagnostics = new List<Diagnostic>
        {
            Diag("src/Foo.cs", "S138", 4,
                "This method 'Run' has 61 lines, which is greater than the 60 lines authorized.",
                metric: 61),
        };
        // Rewrite max upward in baseline to simulate drift below the recorded max.
        var tighter = Baseline("""
            {
              "src/Foo.cs": {
                "S138": { "count": 1, "max": 62 }
              }
            }
            """);
        var failures = tighter.Compare(Current(diagnostics.ToArray()), diagnostics);
        var failure = Assert.Single(failures);
        Assert.Contains("stale baseline entry (was count 1, max 62, now count 1, max 61)", failure);
        Assert.Empty(baseline.Compare(Current(diagnostics.ToArray()), diagnostics));
    }
}
