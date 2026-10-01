# iimmpact-dotnet-quality

IIMMPACT's shared C# code-quality rules: analyzer configuration shipped as a NuGet package, plus a `dotnet` tool that gates new violations on a per-file baseline.

## Packages

| Package | What it is |
| --- | --- |
| `IIMMPACT.CodeQuality` | Analyzer config package. References SonarAnalyzer.CSharp and Microsoft.CodeAnalysis.BannedApiAnalyzers and injects a `.globalconfig`, `SonarLint.xml`, `CodeMetricsConfig.txt`, and `BannedSymbols.IIMMPACT.txt` into the build. `DevelopmentDependency`, so it never flows to downstream consumers of your package. |
| `IIMMPACT.CodeQuality.Tool` | `dotnet` tool `iimmpact-quality` with `check` and `baseline` commands. Builds the solution with SARIF logging enabled, collects diagnostics for the managed rules, and compares them against `code-quality-baseline.json`. |

## Managed rules

The package turns these rules on as warnings:

| Rule | Threshold | Source of the limit |
| --- | --- | --- |
| CA1502 cyclomatic complexity | 25 | `CodeMetricsConfig.txt` |
| CA1506 class/method coupling | 60 (type), 40 (method) | `CodeMetricsConfig.txt` |
| S134 nesting depth | 4 | `SonarLint.xml` |
| S138 method lines | 60 | `SonarLint.xml` |
| S104 file lines | 400 | `SonarLint.xml` |
| S4462 blocking async calls | — | globalconfig |
| CA1849, CA2016 async correctness | — | globalconfig |
| CA2254 logging template | — | globalconfig |
| RS0030 banned APIs | `DateTime.Now`, `DateTimeOffset.Now`, `System.Console`, `new HttpClient()` | `BannedSymbols.IIMMPACT.txt` |
| IDE0051, IDE0052, IDE0060 unused code | non-public parameters | globalconfig |

## Usage

Reference the package in every project you want checked:

```xml
<PackageReference Include="IIMMPACT.CodeQuality" Version="0.1.0" PrivateAssets="all" />
```

Install the tool once per repo:

```sh
dotnet tool install IIMMPACT.CodeQuality.Tool
```

Record existing debt, then gate:

```sh
dotnet iimmpact-quality baseline MyApp.slnx   # writes code-quality-baseline.json
dotnet iimmpact-quality check MyApp.slnx    # fails on growth or stale entries
```

`check` exits non-zero when:

- a file gains diagnostics beyond what the baseline records (growth),
- the baseline records more diagnostics than the build reports (stale), or
- repo config silences a managed rule (`dotnet_diagnostic.<id>.severity = none|silent|suggestion` in `.editorconfig`/`.globalconfig`, or the rule in `<NoWarn>` in `.props`/`.targets`/`.csproj`).

## How the tool works

`iimmpact-quality` runs `dotnet sln list` to find projects, deletes each project's `obj/iimmpact-quality` directory, rebuilds with `-p:IimmpactQualitySarif=true`, then parses the SARIF logs that `IIMMPACT.CodeQuality.targets` wires into `ErrorLog`. Diagnostics for rules outside the managed list are ignored, so unrelated analyzer noise never fails the gate.

## Releasing

Tag `v<version>` where `<version>` equals the `<Version>` in `Directory.Build.props`. The release workflow verifies the tag, packs both packages, publishes to nuget.org via `NuGet/login` OIDC, and creates the GitHub release.

## License

MIT — IIMMPACT SDN BHD.
