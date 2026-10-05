using Xunit;

namespace IIMMPACT.CodeQuality.Tool.Tests;

/// <summary>
/// Schema-2 baseline comparison is the gate's core behavior: an allowance must cover
/// the candidate violation or the check fails. Each test builds literal scan inputs
/// and baseline JSON and asserts the observable failure strings.
/// </summary>
public class BaselineCompareTests
{
    private static EntityKey Entity(string declaration, string source = "src/Foo.cs") =>
        new(new CompilationKey("Consumer.csproj", "net8.0"), source, declaration);

    private static ScanViolation Metric(
        string rule, string declaration, long metric, string source = "src/Foo.cs", int line = 10) =>
        new(Entity(declaration, source), rule, $"message {rule}", metric,
            new DiagnosticSite(source, line, 5), [], string.Empty);

    private static ScanViolation Frozen(
        string rule, string declaration, string digest, string source = "src/Foo.cs", int line = 10) =>
        new(Entity(declaration, source), rule, $"message {rule}", null,
            new DiagnosticSite(source, line, 5), [new TokenSite(line, 0)], digest);

    private static IReadOnlyList<ScanViolation> Scan(params ScanViolation[] violations) => violations;

    private static Baseline ParseBaseline(string json) => Baseline.Parse(json);

    [Fact]
    public void Pass_WhenViolationIsCoveredByAllowance()
    {
        var baseline = ParseBaseline("""
            {
              "schema": "iimmpact-quality-baseline",
              "version": 2,
              "policy": "iimmpact-quality/v2",
              "allowances": [{
                "kind": "metric",
                "project": "Consumer.csproj", "tfm": "net8.0",
                "source": "src/Foo.cs",
                "declaration": "class Foo.method int Run(int)",
                "rule": "S138", "ceiling": 61
              }]
            }
            """);
        var scan = Scan(Metric("S138", "class Foo.method int Run(int)", 61));
        Assert.Empty(baseline.Compare(scan));
    }

    [Fact]
    public void Fails_WhenNoAllowanceExists()
    {
        var baseline = ParseBaseline("""
            {
              "schema": "iimmpact-quality-baseline", "version": 2,
              "policy": "iimmpact-quality/v2", "allowances": []
            }
            """);
        var scan = Scan(Metric("S138", "class Foo.method int Run(int)", 61));
        var failure = Assert.Single(baseline.Compare(scan));
        Assert.Contains("src/Foo.cs(10,5): error S138", failure);
        Assert.Contains("no baseline allowance", failure);
    }

    [Fact]
    public void Fails_WhenMetricExceedsCeiling()
    {
        var baseline = ParseBaseline("""
            {
              "schema": "iimmpact-quality-baseline", "version": 2,
              "policy": "iimmpact-quality/v2",
              "allowances": [{
                "kind": "metric",
                "project": "Consumer.csproj", "tfm": "net8.0",
                "source": "src/Foo.cs",
                "declaration": "class Foo.method int Run(int)",
                "rule": "S138", "ceiling": 61
              }]
            }
            """);
        var scan = Scan(Metric("S138", "class Foo.method int Run(int)", 71));
        var failure = Assert.Single(baseline.Compare(scan));
        Assert.Contains("metric 71 exceeds approved ceiling 61", failure);
    }

    [Fact]
    public void Fails_WhenFrozenScopeDigestDiffers()
    {
        var baseline = ParseBaseline("""
            {
              "schema": "iimmpact-quality-baseline", "version": 2,
              "policy": "iimmpact-quality/v2",
              "allowances": [{
                "kind": "frozen",
                "project": "Consumer.csproj", "tfm": "net8.0",
                "source": "src/Foo.cs",
                "declaration": "class Foo.method int Run(int)",
                "rule": "S134", "tokenDigest": "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
                "sites": [{ "token": 10, "offset": 0 }]
              }]
            }
            """);
        var scan = Scan(Frozen("S134", "class Foo.method int Run(int)", "different"));
        var failure = Assert.Single(baseline.Compare(scan));
        Assert.Contains("differ from the approved scope", failure);
    }

    [Fact]
    public void Fails_WhenViolationMovesFiles_EvenWithSameDeclarationName()
    {
        // A same-named declaration in a different file must not inherit the allowance.
        var baseline = ParseBaseline("""
            {
              "schema": "iimmpact-quality-baseline", "version": 2,
              "policy": "iimmpact-quality/v2",
              "allowances": [{
                "kind": "metric",
                "project": "Consumer.csproj", "tfm": "net8.0",
                "source": "src/Foo.cs",
                "declaration": "class Foo.method int Run(int)",
                "rule": "S138", "ceiling": 61
              }]
            }
            """);
        var scan = Scan(Metric("S138", "class Foo.method int Run(int)", 61, source: "src/Other.cs"));
        Assert.Single(baseline.Compare(scan));
    }

    [Fact]
    public void Fails_WhenFrozenScopeSiteDiffers()
    {
        var baseline = ParseBaseline("""
            {
              "schema": "iimmpact-quality-baseline", "version": 2,
              "policy": "iimmpact-quality/v2",
              "allowances": [{
                "kind": "frozen",
                "project": "Consumer.csproj", "tfm": "net8.0",
                "source": "src/Foo.cs",
                "declaration": "class Foo.method int Run(int)",
                "rule": "S134", "tokenDigest": "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
                "sites": [{ "token": 1, "offset": 0 }]
              }]
            }
            """);
        var violation = new ScanViolation(
            Entity("class Foo.method int Run(int)"), "S134", "nesting", null,
            new DiagnosticSite("src/Foo.cs", 2, 9), [new TokenSite(2, 0)], new string('a', 64));
        var failure = Assert.Single(baseline.Compare(Scan(violation)));
        Assert.Contains("differ from the approved scope", failure);
    }

    [Fact]
    public void Pass_WhenFrozenScopeContainsTheCompleteApprovedSiteSet()
    {
        // A declaration that repeats a banned call produces one allowance listing
        // every call site; each candidate diagnostic must match one of them.
        var baseline = ParseBaseline("""
            {
              "schema": "iimmpact-quality-baseline", "version": 2,
              "policy": "iimmpact-quality/v2",
              "allowances": [{
                "kind": "frozen",
                "project": "Consumer.csproj", "tfm": "net8.0",
                "source": "src/Foo.cs",
                "declaration": "class Foo.method int Run(int)",
                "rule": "RS0030", "tokenDigest": "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
                "sites": [
                  { "token": 2, "offset": 0 },
                  { "token": 5, "offset": 0 }
                ]
              }]
            }
            """);
        var violation = new ScanViolation(
            Entity("class Foo.method int Run(int)"), "RS0030", "banned", null,
            new DiagnosticSite("src/Foo.cs", 5, 3),
            [new TokenSite(2, 0), new TokenSite(5, 0)],
            new string('a', 64));
        Assert.Empty(baseline.Compare(Scan(violation)));
    }

    [Fact]
    public void Fails_WhenFrozenScopeHasAnUnapprovedSite()
    {
        var baseline = ParseBaseline("""
            {
              "schema": "iimmpact-quality-baseline", "version": 2,
              "policy": "iimmpact-quality/v2",
              "allowances": [{
                "kind": "frozen",
                "project": "Consumer.csproj", "tfm": "net8.0",
                "source": "src/Foo.cs",
                "declaration": "class Foo.method int Run(int)",
                "rule": "RS0030", "tokenDigest": "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
                "sites": [{ "token": 2, "offset": 0 }]
              }]
            }
            """);
        var violation = new ScanViolation(
            Entity("class Foo.method int Run(int)"), "RS0030", "banned", null,
            new DiagnosticSite("src/Foo.cs", 9, 9), [new TokenSite(9, 0)], new string('a', 64));
        Assert.Single(baseline.Compare(Scan(violation)));
    }

    [Fact]
    public void Fails_WhenMetricViolationHasNoMeasuredValue()
    {
        var baseline = ParseBaseline("""
            {
              "schema": "iimmpact-quality-baseline", "version": 2,
              "policy": "iimmpact-quality/v2",
              "allowances": [{
                "kind": "metric",
                "project": "Consumer.csproj", "tfm": "net8.0",
                "source": "src/Foo.cs",
                "declaration": "class Foo.method int Run(int)",
                "rule": "S138", "ceiling": 61
              }]
            }
            """);
        var violation = new ScanViolation(
            Entity("class Foo.method int Run(int)"), "S138", "message", null,
            new DiagnosticSite("src/Foo.cs", 10, 5), [], string.Empty);
        var failure = Assert.Single(baseline.Compare(Scan(violation)));
        Assert.Contains("no measured value for a metric rule", failure);
    }

    [Fact]
    public void Parse_RejectsAggregateSchema()
    {
        var exception = Assert.Throws<BaselineFormatException>(() => Baseline.Parse("""
            { "src/Foo.cs": { "S134": { "count": 1 } } }
            """));
        Assert.Contains("unknown baseline property", exception.Message);
    }

    [Fact]
    public void Parse_RejectsWrongPolicy()
    {
        var exception = Assert.Throws<BaselineFormatException>(() => Baseline.Parse("""
            {
              "schema": "iimmpact-quality-baseline", "version": 2,
              "policy": "different", "allowances": []
            }
            """));
        Assert.Contains("policy", exception.Message);
    }

    [Fact]
    public void Parse_RejectsDuplicateAllowance()
    {
        var exception = Assert.Throws<BaselineFormatException>(() => Baseline.Parse("""
            {
              "schema": "iimmpact-quality-baseline", "version": 2,
              "policy": "iimmpact-quality/v2",
              "allowances": [
                {
                  "kind": "metric", "project": "Consumer.csproj", "tfm": "net8.0",
                  "source": "src/Foo.cs", "declaration": "class Foo.method int Run(int)",
                  "rule": "S138", "ceiling": 61
                },
                {
                  "kind": "metric", "project": "Consumer.csproj", "tfm": "net8.0",
                  "source": "src/Foo.cs", "declaration": "class Foo.method int Run(int)",
                  "rule": "S138", "ceiling": 61
                }
              ]
            }
            """));
        Assert.Contains("duplicate", exception.Message);
    }

    [Fact]
    public void RoundTrip_PreservesAllowances()
    {
        var json = """
            {
              "schema": "iimmpact-quality-baseline",
              "version": 2,
              "policy": "iimmpact-quality/v2",
              "generatedExclusions": [
                { "path": "Consumer/obj/Debug/net8.0/Gen.g.cs", "sha256": "dddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddd" }
              ],
              "allowances": [
                {
                  "kind": "metric", "project": "Consumer.csproj", "tfm": "net8.0",
                  "source": "src/Foo.cs", "declaration": "class Foo.method int Run(int)",
                  "rule": "S138", "ceiling": 61
                },
                {
                  "kind": "frozen", "project": "Consumer.csproj", "tfm": "net8.0",
                  "source": "src/Bar.cs", "declaration": "class Bar.method int Go(int)",
                  "rule": "S134", "tokenDigest": "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
                  "sites": [{ "token": 3, "offset": 0 }]
                }
              ]
            }
            """;
        var baseline = Baseline.Parse(json);
        var roundTripped = Baseline.Parse(baseline.ToJson());
        Assert.Equal(2, roundTripped.Allowances.Count);
        Assert.Equal(new string('d', 64), roundTripped.GeneratedExclusions["Consumer/obj/Debug/net8.0/Gen.g.cs"]);
    }

    [Fact]
    public void Reduce_LowersCeilingToMeasuredValue()
    {
        var baseline = ParseBaseline("""
            {
              "schema": "iimmpact-quality-baseline", "version": 2,
              "policy": "iimmpact-quality/v2",
              "allowances": [{
                "kind": "metric", "project": "Consumer.csproj", "tfm": "net8.0",
                "source": "src/Foo.cs", "declaration": "class Foo.method int Run(int)",
                "rule": "S138", "ceiling": 61
              }]
            }
            """);
        var scan = Scan(Metric("S138", "class Foo.method int Run(int)", 61));
        var reduced = baseline.Reduce(scan, out var stillOver);
        Assert.Empty(stillOver);
        var allowance = Assert.IsType<MetricAllowance>(Assert.Single(reduced.Allowances));
        Assert.Equal(61, allowance.Ceiling);
    }

    [Fact]
    public void Reduce_DropsAllowanceWithNoMatchingViolation()
    {
        var baseline = ParseBaseline("""
            {
              "schema": "iimmpact-quality-baseline", "version": 2,
              "policy": "iimmpact-quality/v2",
              "allowances": [{
                "kind": "metric", "project": "Consumer.csproj", "tfm": "net8.0",
                "source": "src/Foo.cs", "declaration": "class Foo.method int Run(int)",
                "rule": "S138", "ceiling": 61
              }]
            }
            """);
        var reduced = baseline.Reduce(Scan(), out var stillOver);
        Assert.Empty(stillOver);
        Assert.Empty(reduced.Allowances);
    }

    [Fact]
    public void Reduce_RefusesViolationAboveCeiling()
    {
        var baseline = ParseBaseline("""
            {
              "schema": "iimmpact-quality-baseline", "version": 2,
              "policy": "iimmpact-quality/v2",
              "allowances": [{
                "kind": "metric", "project": "Consumer.csproj", "tfm": "net8.0",
                "source": "src/Foo.cs", "declaration": "class Foo.method int Run(int)",
                "rule": "S138", "ceiling": 61
              }]
            }
            """);
        var scan = Scan(Metric("S138", "class Foo.method int Run(int)", 71));
        baseline.Reduce(scan, out var stillOver);
        var over = Assert.Single(stillOver);
        Assert.Equal("S138", over.Rule);
    }
}
