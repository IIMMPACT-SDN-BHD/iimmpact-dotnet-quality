using Xunit;

namespace IIMMPACT.CodeQuality.Tool.Tests;

/// <summary>
/// A project that changes its single target framework keeps its trusted allowances;
/// every other TFM difference stays strict and shrink-only.
/// </summary>
public class RetargetTests
{
    private static string Json(params (string Project, string Tfm, int Ceiling)[] allowances) => $$"""
        {
          "schema": "iimmpact-quality-baseline", "version": 2,
          "policy": "iimmpact-quality/v2",
          "allowances": [{{string.Join(",", allowances.Select((a, i) => $$"""
            {
              "kind": "metric",
              "project": "{{a.Project}}", "tfm": "{{a.Tfm}}",
              "source": "src/Foo{{i}}.cs",
              "declaration": "class Foo{{i}}.method int Run(int)",
              "rule": "S138", "ceiling": {{a.Ceiling}}
            }
            """))}}]
        }
        """;

    private static CompilationKey Key(string project, string tfm) => new(project, tfm);

    private static ScanViolation Violation(string project, string tfm, int index, long metric) =>
        new(new EntityKey(Key(project, tfm), $"src/Foo{index}.cs", $"class Foo{index}.method int Run(int)"),
            "S138", "message", metric, new DiagnosticSite($"src/Foo{index}.cs", 1, 1), [], string.Empty);

    [Fact]
    public void SingleTargetRetarget_PassesWhenTheCandidateIsReKeyed()
    {
        var trusted = Baseline.Parse(Json(("A.csproj", "net9.0", 61), ("A.csproj", "net9.0", 70)));
        var candidate = Baseline.Parse(Json(("A.csproj", "net10.0", 61), ("A.csproj", "net10.0", 70)));
        var scanned = new[] { Key("A.csproj", "net10.0") };

        var mapped = trusted.RetargetedTo(scanned, out var retargets);

        Assert.Equal(new Retarget("A.csproj", "net9.0", "net10.0"), Assert.Single(retargets));
        Assert.Empty(candidate.ValidateReductionOf(mapped));
        Assert.Empty(candidate.Compare([Violation("A.csproj", "net10.0", 0, 61), Violation("A.csproj", "net10.0", 1, 70)]));
        Assert.All(mapped.Allowances, a => Assert.Equal("net10.0", a.Entity.Compilation.TargetFramework));
    }

    [Fact]
    public void SingleTargetRetarget_FailsWhenTheCandidateRaisesACeiling()
    {
        var trusted = Baseline.Parse(Json(("A.csproj", "net9.0", 61)));
        var candidate = Baseline.Parse(Json(("A.csproj", "net10.0", 62)));

        var mapped = trusted.RetargetedTo([Key("A.csproj", "net10.0")], out _);

        var failure = Assert.Single(candidate.ValidateReductionOf(mapped));
        Assert.Equal(FailureKind.BaselineNotReduced, failure.Kind);
    }

    [Fact]
    public void SingleTargetRetarget_FailsWhenTheCandidateKeepsTheOldFramework()
    {
        var trusted = Baseline.Parse(Json(("A.csproj", "net9.0", 61)));
        var candidate = Baseline.Parse(Json(("A.csproj", "net9.0", 61)));

        var mapped = trusted.RetargetedTo([Key("A.csproj", "net10.0")], out _);

        Assert.Equal(FailureKind.BaselineNotReduced, Assert.Single(candidate.ValidateReductionOf(mapped)).Kind);
    }

    [Fact]
    public void SingleTargetRetarget_StillFailsGrowthAfterTheMapping()
    {
        var trusted = Baseline.Parse(Json(("A.csproj", "net9.0", 61)));
        var mapped = trusted.RetargetedTo([Key("A.csproj", "net10.0")], out _);

        var failure = Assert.Single(mapped.Compare([Violation("A.csproj", "net10.0", 0, 62)]));
        Assert.Equal(FailureKind.MetricAboveCeiling, failure.Kind);
    }

    [Fact]
    public void OnlyTheRetargetedProjectIsMapped()
    {
        var trusted = Baseline.Parse(Json(("A.csproj", "net9.0", 61), ("B.csproj", "net9.0", 61)));

        var mapped = trusted.RetargetedTo(
            [Key("A.csproj", "net10.0"), Key("B.csproj", "net9.0")], out var retargets);

        Assert.Equal("A.csproj", Assert.Single(retargets).Project);
        Assert.Equal(
            ["A.csproj:net10.0", "B.csproj:net9.0"],
            mapped.Allowances.Select(a => $"{a.Entity.Compilation.Project}:{a.Entity.Compilation.TargetFramework}"));
    }

    [Fact]
    public void UnchangedFramework_IsNotMapped()
    {
        var trusted = Baseline.Parse(Json(("A.csproj", "net9.0", 61)));

        var mapped = trusted.RetargetedTo([Key("A.csproj", "net9.0")], out var retargets);

        Assert.Empty(retargets);
        Assert.Same(trusted, mapped);
    }

    [Fact]
    public void MultiTargetedProject_IsNotMapped()
    {
        var trusted = Baseline.Parse(Json(("A.csproj", "net9.0", 61)));

        var mapped = trusted.RetargetedTo(
            [Key("A.csproj", "net9.0"), Key("A.csproj", "net10.0")], out var retargets);
        Assert.Empty(retargets);
        Assert.Equal("net9.0", Assert.Single(mapped.Allowances).Entity.Compilation.TargetFramework);

        mapped = trusted.RetargetedTo(
            [Key("A.csproj", "net8.0"), Key("A.csproj", "net10.0")], out retargets);
        Assert.Empty(retargets);
        Assert.Equal("net9.0", Assert.Single(mapped.Allowances).Entity.Compilation.TargetFramework);
    }

    [Fact]
    public void AllowancesUnderSeveralFrameworks_AreNotMapped()
    {
        var trusted = Baseline.Parse(Json(("A.csproj", "net8.0", 61), ("A.csproj", "net9.0", 61)));

        var mapped = trusted.RetargetedTo([Key("A.csproj", "net10.0")], out var retargets);

        Assert.Empty(retargets);
        Assert.Equal(
            ["net8.0", "net9.0"],
            mapped.Allowances.Select(a => a.Entity.Compilation.TargetFramework));
    }

    [Fact]
    public void ProjectsMissingFromTheScan_AreNotMapped()
    {
        var trusted = Baseline.Parse(Json(("A.csproj", "net9.0", 61)));

        var mapped = trusted.RetargetedTo([Key("B.csproj", "net10.0")], out var retargets);

        Assert.Empty(retargets);
        Assert.Same(trusted, mapped);
    }

    [Fact]
    public void RetargetNotes_NameTheProjectAndBothFrameworks()
    {
        var note = Assert.Single(CheckReport.RetargetNotes([new Retarget("src/API/API.csproj", "net9.0", "net10.0")]));
        Assert.Equal("src/API/API.csproj: trusted baseline net9.0 applied to net10.0", note);
    }
}
