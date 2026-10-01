using Xunit;

namespace IIMMPACT.CodeQuality.Tool.Tests;

/// <summary>
/// Repo config must not silence managed rules. These tests point FindWeakenedRules at
/// literal files under a temp directory and assert the reported file and line.
/// </summary>
public class WeakenedConfigTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"iimmpact-quality-tests-{Guid.NewGuid():N}");

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private string Write(string relativePath, string content)
    {
        var path = Path.Combine(_dir, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        return path;
    }

    [Fact]
    public void Fails_WhenEditorConfigSilencesManagedRule()
    {
        Write(".editorconfig", """
            root = true

            [*.cs]
            dotnet_diagnostic.S134.severity = none
            dotnet_diagnostic.CA1502.severity = warning
            """);

        var findings = QualityGate.FindWeakenedRules(_dir);

        var finding = Assert.Single(findings);
        Assert.Contains(".editorconfig(4)", finding);
        Assert.Contains("S134", finding);
    }

    [Fact]
    public void Fails_WhenGlobalConfigUsesSilentOrSuggestion()
    {
        Write("custom.globalconfig", """
            is_global = true
            dotnet_diagnostic.S138.severity = silent
            dotnet_diagnostic.RS0030.severity = suggestion
            """);

        var findings = QualityGate.FindWeakenedRules(_dir);

        Assert.Equal(2, findings.Count);
        Assert.Contains("custom.globalconfig(2)", findings[0]);
        Assert.Contains("custom.globalconfig(3)", findings[1]);
    }

    [Fact]
    public void Fails_WhenNoWarnListsManagedRule()
    {
        Write("Directory.Build.props", """
            <Project>
              <PropertyGroup>
                <NoWarn>$(NoWarn);CS1591;S104</NoWarn>
              </PropertyGroup>
            </Project>
            """);

        var findings = QualityGate.FindWeakenedRules(_dir);

        var finding = Assert.Single(findings);
        Assert.Contains("Directory.Build.props(3)", finding);
        Assert.Contains("S104", finding);
    }

    [Fact]
    public void Passes_WhenConfigIsUnrelatedOrUnderObj()
    {
        Write(".editorconfig", """
            root = true
            dotnet_diagnostic.CS1591.severity = none
            dotnet_diagnostic.S134.severity = error
            """);
        Write(Path.Combine("obj", "generated.editorconfig"), "dotnet_diagnostic.S134.severity = none");
        Write("Project.csproj", """
            <Project>
              <PropertyGroup>
                <NoWarn>$(NoWarn);CS0219</NoWarn>
              </PropertyGroup>
            </Project>
            """);

        Assert.Empty(QualityGate.FindWeakenedRules(_dir));
    }
}
