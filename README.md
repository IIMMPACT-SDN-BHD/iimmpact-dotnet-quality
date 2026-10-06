# iimmpact-dotnet-quality

IIMMPACT's shared C# code-quality rules: analyzer configuration shipped as a NuGet package, plus a `dotnet` tool that gates new violations on a per-declaration baseline committed to git.

## Packages

| Package | What it is |
| --- | --- |
| `IIMMPACT.CodeQuality` | Analyzer config package. References SonarAnalyzer.CSharp and Microsoft.CodeAnalysis.BannedApiAnalyzers and injects a `.globalconfig`, `SonarLint.xml`, `CodeMetricsConfig.txt`, and `BannedSymbols.IIMMPACT.txt` into the build. `DevelopmentDependency`, so it never flows to downstream consumers of your package. |
| `IIMMPACT.CodeQuality.Tool` | `dotnet` tool `iimmpact-quality` with `check`, `baseline` and `bootstrap` commands. Builds the solution with SARIF logging enabled, collects diagnostics for the managed rules, attributes each to its owning declaration, and compares them against the `code-quality-baseline.json` committed at a trusted git revision. |

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
| S3776 cognitive complexity | 15 | `SonarLint.xml` |
| CA2008, CA2012 task and `ValueTask` misuse | — | globalconfig |
| CA2017, CA2241 logging and format argument mismatches | — | globalconfig |
| CA2000 undisposed objects | — | globalconfig |
| CA2013, CA2019, CA2022, CA2200, CA2208, CA2214 runtime correctness | — | globalconfig |
| CS8600, CS8601, CS8602, CS8603, CS8604, CS8618, CS8619, CS8625, CS8629 nullable correctness | — | globalconfig |

## Requirements

- .NET SDK 9 or later. `CA2022` ships with the .NET 9 SDK analyzers, so the tool rejects older SDKs with exit `2` instead of silently enforcing fewer rules. Projects may still target older frameworks such as `net8.0`.
- `<Nullable>enable</Nullable>` in every checked project. The tool fails with exit `2` when nullable analysis does not run, or when handwritten source contains `#nullable disable`.

## Usage

Reference the package in every project you want checked:

```xml
<PackageReference Include="IIMMPACT.CodeQuality" Version="0.2.0" PrivateAssets="all" />
```

Install the tool once per repo:

```sh
dotnet tool install IIMMPACT.CodeQuality.Tool
```

Propose a baseline from a trusted source revision, review and commit it, then gate:

```sh
dotnet iimmpact-quality bootstrap MyApp.slnx --source main   # proposes code-quality-baseline.json
git add code-quality-baseline.json && git commit            # human review approves it
dotnet iimmpact-quality check MyApp.slnx --base main        # fails on anything over baseline
dotnet iimmpact-quality baseline MyApp.slnx --base main     # reductions only: shrinks debt
```

`check` compares the working-tree `code-quality-baseline.json` and a fresh scan against the baseline committed at `--base`. The working-tree file may only remove allowances, lower ceilings or drop exclusions, so a branch cannot approve its own debt.

Exit codes:

- `0`: every managed diagnostic is within the trusted baseline.
- `1`: gate failure. A violation has no allowance, a metric exceeds its ceiling, a frozen scope changed, the candidate baseline is not a reduction, or no baseline exists at `--base`.
- `2`: invalid input or analysis. The build failed, analysis was incomplete or altered, or the baseline JSON is malformed.

`check` fails with exit `2` when:

- a project or target framework in the `ProjectReference` closure produced no fresh compiler evidence or SARIF log,
- analyzers were skipped (`RunAnalyzersDuringBuild=false`) or an analyzer failed (`ADxxxx`),
- one of the analyzer families (NetAnalyzers including `CA2022`, BannedApi, Sonar, code style, compiler nullable analysis) did not report its execution sentinel. An older `Microsoft.CodeAnalysis.NetAnalyzers` package that lacks `CA2022` fails this check,
- the .NET SDK is older than 9, or handwritten source contains `#nullable disable` or a `#line` directive (which can move a diagnostic onto an approved declaration or hide it),
- an analyzer or source generator is loaded from anywhere other than the .NET SDK or a package that restore resolved for the project (`project.assets.json`, any package folder, symlinks resolved). A generator built in the repository, or a DLL copied into the NuGet folder, is rejected; add analyzers with `PackageReference`, not `<Analyzer Include>` paths,
- a `dotnet_code_quality` option for a managed rule, or a generic one that applies to every rule, differs from the package's value, since options such as `excluded_symbol_names` narrow what rules report. Options for unmanaged rules are left to the consumer,
- the SARIF log is malformed, or a managed result is missing its location, message or metric,
- the effective configuration silences a managed rule (`.editorconfig` or `.globalconfig` severity in any casing, `NoWarn`, or a source suppression),
- the shipped `.globalconfig`, `SonarLint.xml`, `CodeMetricsConfig.txt` or `BannedSymbols.IIMMPACT.txt` input differs from the package content.

A source that analyzers treat as generated (`.g.cs`, `.g.i.cs`, `.generated.cs`, `.designer.cs`, an `<auto-generated>` header, `[GeneratedCode]`/`[CompilerGenerated]` including through a `using` alias in that file or a `global using` alias, or `generated_code = true`) must match an approved path and content hash in the baseline. Only SDK-written `AssemblyInfo`, target-framework attribute and global-usings files in the tool's own build output are exempt.

`bootstrap` only writes a proposal, and refuses to overwrite an existing file. It also refuses when any handwritten compile input, project file or `Directory.Build.*` file differs from the `--source` commit, or when the compile set differs from that commit. Files are compared with the raw committed blob, ignoring only CRLF/LF differences (checkout filters configured in the repository are not trusted). The only other permitted difference is a `PackageReference` to `IIMMPACT.CodeQuality` or `IIMMPACT.CodeQuality.Tool`. `baseline` writes only reductions. It refuses, and leaves the file unchanged, when any violation is not covered.

## How the tool works

`iimmpact-quality` lists the solution's projects, evaluates each project's target frameworks and `ProjectReference` closure, then builds every compilation in dependency order into a fresh temporary artifacts directory with `-p:IimmpactQualitySarif=true`. Non-C# references such as F# projects are built but not analyzed. Scan builds turn off `TreatWarningsAsErrors`, because the gate reads the SARIF log and the injected sentinel warnings would otherwise fail the build. Each project's artifacts folder is named after a hash of its solution-relative path, so same-named projects in different folders do not overwrite each other. The package targets add an execution-sentinel source file to each compilation and write the actual `csc` command line, inputs and SARIF log to that invocation's evidence directory. The tool parses the command line with Roslyn's `CSharpCommandLineParser` and resolves effective analyzer options with `AnalyzerConfigSet`. Evidence from earlier builds is never read. Diagnostics for rules outside the managed list are ignored.

Allowances are per declaration (`EntityKey` = project path + TFM + source file + declaration identity), so a ceiling approved for one method cannot cover growth in another. `MetricAllowance` stores the measured ceiling for S138, S3776, CA1502 and CA1506. S104 is always attributed to the file. `FrozenScopeAllowance` stores a digest of the declaration's C# tokens and each diagnostic's token position for rules without a measurement (S134, RS0030, IDE*, and the rest). Comments and whitespace do not change either value, so they never invalidate an allowance. Any token change inside the declaration does. Declarations with the same name in one file (partial parts, or same-named local functions in sibling blocks) share one identity whose digest covers all of them: an allowance can never move between them, and editing or adding any one of them requires that scope to be clean.

## Releasing

Tag `v<version>` where `<version>` equals the `<Version>` in `Directory.Build.props`. The release workflow verifies the tag, packs both packages, publishes to nuget.org via `NuGet/login` OIDC, and creates the GitHub release.

To retry an existing release without moving its tag, run:

```bash
gh workflow run release.yml --ref main -f tag=v0.1.0
```

Both triggers build from the tag, not the current branch. Publishing runs for the same tag are serialized, and packages already published are skipped.

## License

MIT — IIMMPACT SDN BHD.
