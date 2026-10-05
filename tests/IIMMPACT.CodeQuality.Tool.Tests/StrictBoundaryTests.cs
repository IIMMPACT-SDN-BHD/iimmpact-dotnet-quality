using Xunit;

namespace IIMMPACT.CodeQuality.Tool.Tests;

public class StrictBoundaryTests
{
    public static TheoryData<string> InvalidBaselines => new()
    {
        """{"schema":"iimmpact-quality-baseline","version":2,"policy":"iimmpact-quality/v2","allowances":[],"extra":true}""",
        """{"version":2,"policy":"iimmpact-quality/v2","allowances":[]}""",
        """{"schema":"iimmpact-quality-baseline","version":2,"policy":"iimmpact-quality/v2","allowances":null}""",
        """{"schema":"iimmpact-quality-baseline","version":2,"policy":"iimmpact-quality/v2","generatedExclusions":[{"path":"../G.cs","sha256":"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"}],"allowances":[]}""",
        """{"schema":"iimmpact-quality-baseline","version":2,"policy":"iimmpact-quality/v2","generatedExclusions":[{"path":"obj/G.cs","sha256":"bad"}],"allowances":[]}""",
        """{"schema":"iimmpact-quality-baseline","version":2,"policy":"iimmpact-quality/v2","allowances":[{"kind":"metric","project":"P.csproj","tfm":"net8.0","source":"C.cs","declaration":"file","rule":"S138","ceiling":0}]}""",
        """{"schema":"iimmpact-quality-baseline","version":2,"policy":"iimmpact-quality/v2","allowances":[{"kind":"metric","project":"P.csproj","tfm":"net8.0","source":"C.cs","declaration":"file","rule":"RS0030","ceiling":1}]}""",
        """{"schema":"iimmpact-quality-baseline","version":2,"policy":"iimmpact-quality/v2","allowances":[{"kind":"frozen","project":"P.csproj","tfm":"net8.0","source":"C.cs","declaration":"file","rule":"S134","tokenDigest":"","sites":[]}]}""",
        """{"schema":"iimmpact-quality-baseline","version":2,"policy":"iimmpact-quality/v2","allowances":[{"kind":"frozen","project":"P.csproj","tfm":"net8.0","source":"C.cs","declaration":"file","rule":"UNKNOWN","tokenDigest":"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa","sites":[{"token":1,"offset":0}]}]}""",
    };

    [Theory]
    [MemberData(nameof(InvalidBaselines))]
    public void Parse_RejectsMalformedOrOpenSchema(string json) =>
        Assert.Throws<BaselineFormatException>(() => Baseline.Parse(json));

    [Fact]
    public void Candidate_MustBeARealReductionOfTrustedBaseline()
    {
        var trusted = Baseline.Parse(BaselineJson(61));
        var inflated = Baseline.Parse(BaselineJson(62));

        var failure = Assert.Single(inflated.ValidateReductionOf(trusted));

        Assert.Contains("not a reduction", failure);
    }

    private static string BaselineJson(long ceiling) => $$"""
        {
          "schema":"iimmpact-quality-baseline","version":2,"policy":"iimmpact-quality/v2",
          "allowances":[{
            "kind":"metric","project":"Consumer.csproj","tfm":"net8.0","source":"C.cs",
            "declaration":"class C/method Run(int)","rule":"S138","ceiling":{{ceiling}}
          }]
        }
        """;
}
