# iimmpact-dotnet-quality — development guide

Shared C# code-quality rules for IIMMPACT. One NuGet package ships analyzer configuration; one dotnet tool gates violations on a per-declaration baseline committed to git.

## Layout

- `src/IIMMPACT.CodeQuality` — netstandard2.0 analyzer-config package. `build/*.props|targets` inject `IIMMPACT.CodeQuality.globalconfig`, `SonarLint.xml`, `CodeMetricsConfig.txt`, and `BannedSymbols.IIMMPACT.txt` into consumer builds.
- `src/IIMMPACT.CodeQuality.Tool` — net8.0 `PackAsTool` CLI `iimmpact-quality` (`check`/`baseline`/`bootstrap`, all revision-gated: `--base`/`--source`). Entry point `Program.cs`; `Scan.cs` collects build evidence + SARIF, `DeclarationIndex.cs` attributes diagnostics via Roslyn, `Baseline.cs` is the schema-2 compare/reduce, `Bootstrap.cs` validates provenance, `Git.cs` reads trusted revisions. Dependencies: System.Text.Json and Microsoft.CodeAnalysis.CSharp only.
- `tests/IIMMPACT.CodeQuality.Tool.Tests` — xunit suite covering SARIF parsing, baseline compare/reduce, declaration indexing, and policy detection.
- `scripts/verify-fixture.sh` — end-to-end proof. Packs both packages into a unique `mktemp` directory with its own NuGet caches and CLI home, builds a consumer inside a disposable git repo that violates every managed rule group, and asserts: fail-before-baseline, bootstrap-proposal, pass-after-commit, fail on growth/sibling/frozen-scope edit/candidate baseline inflation/compile-set removal/weakened config/generated filenames/missing code-style analysis/missing TFM evidence/skipped analyzers/malformed SARIF/analyzer crash, pass on comment-only edits, and reduction-only `baseline`.
- `.github/workflows` — `ci.yml` (build, test, fixture) and `release.yml` (tag-gated pack + `dotnet nuget push` via `NuGet/login` OIDC).

## Rules and thresholds

Managed rules are defined in `Diagnostics.cs` (`ManagedRules.Ids`). The tool only counts these IDs; other analyzer warnings are ignored. Thresholds live in the package files: `CodeMetricsConfig.txt` (CA1502=25, CA1506 type=60, method=40), `SonarLint.xml` (S134=4, S138=60, S104=400, S3776=15). Every correctness rule added in 0.2.0 has a bad and a corrected case in `scripts/rule-cases.py`, and the fixture runs both through the packaged CLI. Add a case there when adding a rule. The tool requires .NET SDK 9 or later and a nullable-enabled compilation (sentinel `CS8602`). Banned symbols live in `BannedSymbols.IIMMPACT.txt` (DateTime.Now/DateTimeOffset.Now/System.Console/HttpClient constructors).

## Conventions

- `dotnet` CLI drives everything: `dotnet build`, `dotnet test`, `dotnet pack`, `dotnet tool`.
- The tool builds each compilation with `-p:IimmpactQualitySarif=true`, a fresh temporary `--artifacts-path`, and `IimmpactQualitySarifDir` set to a new per-compilation evidence directory, so evidence from earlier builds is never read. Fixture probes that corrupt scan output must inject during the tool's own build (`AfterTargets="IimmpactQualityWriteScanEvidence"`, writing to `$(IimmpactQualitySarifDir)`).
- `check` never writes. `baseline` rewrites `code-quality-baseline.json` next to the solution with reductions only. `bootstrap` writes it once as a proposal and never overwrites it. `check` and `baseline` read the trusted baseline from the `--base` commit via `git show`, and require the working-tree file to be a reduction of it.
- Baseline is schema 2: `allowances[]` of `metric` (ceiling) or `frozen` (token digest + token sites) keyed by `project` (relative `.csproj` path)/`tfm`/`source`/`declaration`, plus `generatedExclusions` (path + content hash for analyzer-recognized generated sources). Strict parse: unknown or duplicate fields, wrong rule kind, bad digests, and invalid paths all fail with exit `2`.
- Path comparisons go through `QualityGate.RealPath` (macOS `/var` → `/private/var`); git blob reads hash raw stdout bytes (`RunProcessBytesAsync`) so BOMs survive.
- Keep the tool free of extra dependencies — it is a `dotnet tool`, not a library.
- `dotnet build` on .NET 10 SDK produces `.slnx`; the tool and scripts accept both `.sln` and `.slnx`.

## Verification

- `dotnet build iimmpact-dotnet-quality.slnx -c Release`
- `dotnet test iimmpact-dotnet-quality.slnx -c Release --no-build`
- `bash scripts/verify-fixture.sh` — must print `ALL CHECKS PASSED`. This is the real proof; unit tests alone do not cover the NuGet packaging or analyzer wiring.

## Rules

- Do not commit or push without explicit approval.
- Do not publish packages or create the GitHub remote without explicit approval.
- Bump `<Version>` in `Directory.Build.props`; it versions both packages and release tags must match it. Shared package metadata (copyright, license, README, release notes) also lives there.
