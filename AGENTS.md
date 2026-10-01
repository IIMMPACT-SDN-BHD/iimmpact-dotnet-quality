# iimmpact-dotnet-quality — development guide

Shared C# code-quality rules for IIMMPACT. One NuGet package ships analyzer configuration; one dotnet tool gates violations on a per-file baseline.

## Layout

- `src/IIMMPACT.CodeQuality` — netstandard2.0 analyzer-config package. `build/*.props|targets` inject `IIMMPACT.CodeQuality.globalconfig`, `SonarLint.xml`, `CodeMetricsConfig.txt`, and `BannedSymbols.IIMMPACT.txt` into consumer builds.
- `src/IIMMPACT.CodeQuality.Tool` — net8.0 `PackAsTool` CLI `iimmpact-quality` (`check`/`baseline`). Entry point `Program.cs`, parsing/comparison in `Diagnostics.cs`. Only dependency: System.Text.Json.
- `tests/IIMMPACT.CodeQuality.Tool.Tests` — xunit suite covering SARIF parsing, baseline compare, and weakened-config detection.
- `scripts/verify-fixture.sh` — end-to-end proof. Packs both packages into a temp feed under `${TMPDIR:-/tmp}/iimmpact-dotnet-quality-fixture`, builds a consumer that violates every managed rule group, and asserts fail-before-baseline / pass-after / fail-on-growth / fail-on-weakened-config / boundary cases.
- `.github/workflows` — `ci.yml` (build, test, fixture) and `release.yml` (tag-gated pack + `dotnet nuget push` via `NuGet/login` OIDC).

## Rules and thresholds

Managed rules are defined in `Diagnostics.cs` (`ManagedRules.Ids`). The tool only counts these IDs; other analyzer warnings are ignored. Thresholds live in the package files: `CodeMetricsConfig.txt` (CA1502=25, CA1506 type=60, method=40), `SonarLint.xml` (S134=4, S138=60, S104=400). Banned symbols live in `BannedSymbols.IIMMPACT.txt` (DateTime.Now/DateTimeOffset.Now/System.Console/HttpClient constructors).

## Conventions

- `dotnet` CLI drives everything: `dotnet build`, `dotnet test`, `dotnet pack`, `dotnet tool`.
- The tool collects diagnostics by building the consumer solution with `-p:IimmpactQualitySarif=true`; `IimmpactQualitySarifDir` is `obj/iimmpact-quality` per project. Always delete that directory before building or analyzers may be skipped.
- `check` never writes; `baseline` rewrites `code-quality-baseline.json` next to the solution.
- Keep the tool free of extra dependencies — it is a `dotnet tool`, not a library.
- `dotnet build` on .NET 10 SDK produces `.slnx`; the tool and scripts accept both `.sln` and `.slnx`.

## Verification

- `dotnet build iimmpact-dotnet-quality.slnx -c Release`
- `dotnet test iimmpact-dotnet-quality.slnx -c Release --no-build`
- `bash scripts/verify-fixture.sh` — must print `ALL CHECKS PASSED`. This is the real proof; unit tests alone do not cover the NuGet packaging or analyzer wiring.

## Rules

- Do not commit or push without explicit approval.
- Do not publish packages or create the GitHub remote without explicit approval.
- Bump `<Version>` in both csproj files together; release tags must match.
