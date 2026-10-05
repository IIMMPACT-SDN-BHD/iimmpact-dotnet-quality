using Xunit;

namespace IIMMPACT.CodeQuality.Tool.Tests;

/// <summary>
/// Metric and SARIF parsing against literal messages in the shapes the real analyzers emit.
/// </summary>
public class ParsingTests
{
    [Theory]
    // Newer SDKs quote numbers; older ones print them bare. Both shapes occur.
    [InlineData("CA1502", "'Foo.Bar' has a cyclomatic complexity of '26'. Rewrite or refactor the code to decrease its complexity below '25'.", 26)]
    [InlineData("CA1502", "'Foo.Bar' has a cyclomatic complexity of 26. Rewrite or refactor the code to decrease its complexity below the threshold 25.", 26)]
    [InlineData("CA1506", "'Foo.Bar' is coupled with '41' different types from '3' different namespaces. Rewrite or refactor the code to decrease its class coupling below '40'.", 41)]
    [InlineData("CA1506", "'Foo.Bar' is coupled with 41 different types from 3 different namespaces. Rewrite or refactor the code to decrease its class coupling below the threshold 40.", 41)]
    [InlineData("S138", "This method 'Run' has 61 lines, which is greater than the 60 lines authorized. Split it into smaller methods.", 61)]
    [InlineData("S104", "This file has 401 lines, which is greater than 400 authorized. Split it into smaller files.", 401)]
    public void ParseMetric_ReadsValueFromRealMessageShapes(string rule, string message, long expected) =>
        Assert.Equal(expected, ManagedRules.ParseMetric(rule, message));

    [Theory]
    [InlineData("RS0030", "The symbol 'DateTime.Now' is banned in this project: Use DateTime.UtcNow")]
    [InlineData("IDE0051", "Private member 'Foo.Unused' is unused")]
    [InlineData("S134", "Refactor this code to not nest more than 4 control flow statements.")]
    public void ParseMetric_IgnoresNonMetricRules(string rule, string message) =>
        Assert.Null(ManagedRules.ParseMetric(rule, message));

    [Fact]
    public void SarifParser_ReadsManagedDiagnosticsAndSkipsOthers()
    {
        var sarif = """
            {
              "version": "2.1.0",
              "runs": [{
                "results": [
                  {
                    "ruleId": "S134",
                    "level": "warning",
                    "message": { "text": "Refactor this code to not nest more than 4 control flow statements." },
                    "locations": [{
                      "physicalLocation": {
                        "artifactLocation": { "uri": "file:///repo/Consumer/Bad.cs" },
                        "region": { "startLine": 12, "startColumn": 9 }
                      }
                    }]
                  },
                  {
                    "ruleId": "CS0219",
                    "level": "warning",
                    "message": { "text": "The variable 'x' is assigned but never used" },
                    "locations": [{
                      "physicalLocation": {
                        "artifactLocation": { "uri": "file:///repo/Consumer/Bad.cs" },
                        "region": { "startLine": 5, "startColumn": 5 }
                      }
                    }]
                  },
                  {
                    "ruleId": "S138",
                    "level": "warning",
                    "message": { "text": "This method 'Run' has 61 lines, which is greater than the 60 lines authorized. Split it into smaller methods." },
                    "locations": [{
                      "physicalLocation": {
                        "artifactLocation": { "uri": "Consumer/Big.cs" },
                        "region": { "startLine": 4, "startColumn": 17 }
                      }
                    }]
                  }
                ]
              }]
            }
            """;

        var diagnostics = SarifLogParser.Parse(sarif, "/repo");

        Assert.Equal(2, diagnostics.Count);

        var s134 = Assert.Single(diagnostics, d => d.RuleId == "S134");
        Assert.Equal("Consumer/Bad.cs", s134.Path);
        Assert.Equal(12, s134.Line);
        Assert.Equal(9, s134.Column);
        Assert.Null(s134.Metric);
        Assert.False(s134.SuppressedInSource);

        var s138 = Assert.Single(diagnostics, d => d.RuleId == "S138");
        Assert.Equal("Consumer/Big.cs", s138.Path);
        Assert.Equal(61, s138.Metric);
    }

    [Fact]
    public void SarifParser_MarksInSourceSuppressions()
    {
        var sarif = """
            {
              "version": "2.1.0",
              "runs": [{
                "results": [{
                  "ruleId": "S134",
                  "level": "warning",
                  "message": { "text": "Refactor this code." },
                  "suppressions": [{ "kind": "inSource" }],
                  "locations": [{
                    "physicalLocation": {
                      "artifactLocation": { "uri": "file:///repo/Foo.cs" },
                      "region": { "startLine": 1, "startColumn": 1 }
                    }
                  }]
                }]
              }]
            }
            """;
        var diagnostic = Assert.Single(SarifLogParser.Parse(sarif, "/repo"));
        Assert.True(diagnostic.SuppressedInSource);
    }

    [Fact]
    public void SarifParser_ReadsAnalyzerFailuresButDoesNotTreatDescriptorsAsExecution()
    {
        var sarif = """
            {
              "version": "2.1.0",
              "runs": [{
                "tool": {
                  "driver": {
                    "name": "csc",
                    "rules": [
                      { "id": "CA1502" },
                      { "id": "S134" },
                      { "id": "RS0030" }
                    ]
                  }
                },
                "results": [{
                  "ruleId": "AD0001",
                  "level": "warning",
                  "message": { "text": "Analyzer 'Sonar' threw an exception" },
                  "locations": []
                }]
              }]
            }
            """;
        var diagnostic = Assert.Single(SarifLogParser.Parse(sarif, "/repo"));
        Assert.Equal("AD0001", diagnostic.RuleId);
        Assert.Contains("threw an exception", diagnostic.Message);
    }

    [Fact]
    public void SarifParser_RejectsManagedResultWithoutPhysicalLocation()
    {
        var sarif = """
            {"version":"2.1.0","runs":[{"results":[{
              "ruleId":"RS0030","message":{"text":"banned"},"locations":[]
            }]}]}
            """;
        Assert.Throws<ScanException>(() => SarifLogParser.Parse(sarif, "/repo"));
    }
}
