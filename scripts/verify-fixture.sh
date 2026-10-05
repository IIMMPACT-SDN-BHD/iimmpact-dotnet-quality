#!/usr/bin/env bash
# End-to-end proof of the strict quality gate.
#
# The tool now verifies a per-declaration baseline against a complete scan. This
# script packs the package + tool, builds a violating consumer, then exercises:
#   1. check fails when no trusted baseline exists
#   2. bootstrap proposes a baseline from a verified source revision
#   3. check passes when every violation is inside its allowance
#   4. growth inside an allowance's declaration still fails (frozen scope)
#   5. a new declaration's violation fails even at the same metric
#   6. a forged/raised baseline is rejected
#   7. a missing-TFM compilation fails even though the build passes
#   8. a malformed SARIF log fails
#   9. uppercase NONE in .editorconfig fails
#  10. NoWarn in the project file fails
#  11. an analyzer-disabled build fails
#  12. a fake generated exclusion fails
#  13. an empty clean scan passes with real execution evidence
#
# The consumer lives in a disposable git repository under agent-tmp so `check`
# can read baselines from an approved commit, never from the working tree.
set -euo pipefail

REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
# Keep each run's feed, caches and build outputs separate.
WORK="$(mktemp -d "${TMPDIR:-/tmp}/iimmpact-dotnet-quality-fixture.XXXXXX")"
FEED="$WORK/feed"
CONSUMER="$WORK/consumer"
SLN="$CONSUMER/Fixture.slnx"
BASELINE="$CONSUMER/code-quality-baseline.json"

# Never clear or write the user's NuGet cache.
export NUGET_PACKAGES="$WORK/nuget-packages"
export NUGET_HTTP_CACHE_PATH="$WORK/nuget-http-cache"
export NUGET_PLUGINS_CACHE_PATH="$WORK/nuget-plugins-cache"
export DOTNET_CLI_HOME="$WORK/dotnet-home"
mkdir -p "$FEED" "$CONSUMER" "$NUGET_PACKAGES" "$NUGET_HTTP_CACHE_PATH" "$DOTNET_CLI_HOME"
printf 'work dir: %s\n' "$WORK"

log() { printf '\n== %s ==\n' "$*"; }
assert_contains() {
  # $1: file, $2: needle, $3: label
  if ! grep -qF "$2" "$1"; then
    echo "FAIL: $3 — expected output to contain '$2'" >&2
    cat "$1" >&2
    exit 1
  fi
}
assert_exit() {
  # $1: expected exit, $2: log file, $3: label
  local want="$1" file="$2" label="$3" got="$4"
  if [ "$got" -ne "$want" ]; then
    echo "FAIL: $label — expected exit $want, got $got" >&2
    cat "$file" >&2
    exit 1
  fi
}
expect_fail() {
  # Runs a command that must fail with a specific exit code. $1: expected code,
  # $2: label. Remaining args are the command.
  local want="$1" label="$2"; shift 2
  local out="$WORK/$(echo "$label" | tr ' /' '__').log"
  set +e
  "$@" > "$out" 2>&1
  local got=$?
  set -e
  assert_exit "$want" "$out" "$label" "$got"
  LAST_LOG="$out"
}

# --- environment -----------------------------------------------------------

log "pack packages"
dotnet pack "$REPO_ROOT/src/IIMMPACT.CodeQuality" --artifacts-path "$WORK/artifacts" -o "$FEED" -c Release -v quiet --nologo
dotnet pack "$REPO_ROOT/src/IIMMPACT.CodeQuality.Tool" --artifacts-path "$WORK/artifacts" -o "$FEED" -c Release -v quiet --nologo
ls "$FEED"/*.nupkg

cat > "$CONSUMER/NuGet.config" <<EOF
<?xml version="1.0" encoding="utf-8"?>
<configuration>
  <packageSources>
    <add key="local" value="$FEED" />
    <add key="nuget.org" value="https://api.nuget.org/v3/index.json" />
  </packageSources>
</configuration>
EOF

cd "$CONSUMER"
dotnet new sln -n Fixture --force >/dev/null
dotnet new classlib -n Consumer -f net8.0 -o Consumer --force >/dev/null
rm -f Consumer/Class1.cs
if [ -f Fixture.slnx ]; then SLN=Fixture.slnx; else SLN=Fixture.sln; fi
dotnet sln "$SLN" add Consumer/Consumer.csproj >/dev/null

cat > Consumer/Directory.Build.props <<'EOF'
<Project>
  <ItemGroup>
    <PackageReference Include="IIMMPACT.CodeQuality" Version="0.1.0" PrivateAssets="all" />
    <PackageReference Include="Microsoft.Extensions.Logging.Abstractions" Version="9.0.0" />
  </ItemGroup>
</Project>
EOF

dotnet new tool-manifest --force >/dev/null
dotnet tool install IIMMPACT.CodeQuality.Tool --version 0.1.0 --add-source "$FEED" >/dev/null

# --- consumer sources ------------------------------------------------------
# One violation per managed rule group plus a second S138 offender to prove
# per-declaration attribution.

cat > Consumer/Complexity26.cs <<'EOF'
// CA1502: cyclomatic complexity 26 (threshold 25).
public static class Complexity26
{
    public static int Pick(int x)
    {
        var r = 0;
        if (x == 0) r += 1;
        if (x == 1) r += 2;
        if (x == 2) r += 3;
        if (x == 3) r += 4;
        if (x == 4) r += 5;
        if (x == 5) r += 6;
        if (x == 6) r += 7;
        if (x == 7) r += 8;
        if (x == 8) r += 9;
        if (x == 9) r += 10;
        if (x == 10) r += 11;
        if (x == 11) r += 12;
        if (x == 12) r += 13;
        if (x == 13) r += 14;
        if (x == 14) r += 15;
        if (x == 15) r += 16;
        if (x == 16) r += 17;
        if (x == 17) r += 18;
        if (x == 18) r += 19;
        if (x == 19) r += 20;
        if (x == 20) r += 21;
        if (x == 21) r += 22;
        if (x == 22) r += 23;
        if (x == 23) r += 24;
        if (x > 24 && x < 100 || x < -5) r += 25;
        return r;
    }
}
EOF

cat > Consumer/NestingDepth5.cs <<'EOF'
// S134: nesting depth 5 (max 4).
public static class NestingDepth5
{
    public static int Run(int x)
    {
        var r = 0;
        var i = x;
        if (x > 0)
        {
            while (i > 0)
            {
                if (i % 2 == 0)
                {
                    for (var j = 0; j < i; j++)
                    {
                        if (j % 3 == 0)
                        {
                            r += j;
                        }
                    }
                    return r;
                }
                i--;
            }
        }
        return r;
    }
}
EOF

{
  echo "// S138: method spanning 61 counted lines (max 60)."
  echo "public static class Method61Lines"
  echo "{"
  echo "    public static int Run(int x) {"
  for i in $(seq 1 60); do
    echo "        x += $i;"
  done
  echo "        return x;"
  echo "    }"
  echo "}"
} > Consumer/Method61Lines.cs

{
  echo "// S138: second 61-line method so each declaration must be tracked alone."
  echo "public static class Method61LinesB"
  echo "{"
  echo "    public static int Run(int x) {"
  for i in $(seq 1 60); do
    echo "        x += $i;"
  done
  echo "        return x;"
  echo "    }"
  echo "}"
} > Consumer/Method61LinesB.cs

{
  echo "// S104: file with more than 400 counted lines."
  echo "public static class File401Lines"
  echo "{"
  for i in $(seq 1 405); do
    echo "    public static int M$i(int x) => x + $i;"
  done
  echo "}"
} > Consumer/File401Lines.cs

cat > Consumer/BannedApis.cs <<'EOF'
// RS0030: DateTime.Now, System.Console, and new HttpClient() are banned.
public static class BannedApis
{
    public static void Run()
    {
        var now = System.DateTime.Now;
        var offset = System.DateTimeOffset.Now;
        System.Console.WriteLine($"{now} {offset}");
        using var client = new System.Net.Http.HttpClient();
        client.Dispose();
    }
}
EOF

cat > Consumer/AsyncBlocking.cs <<'EOF'
// S4462: task.Result blocks in an async-capable member.
public static class AsyncBlocking
{
    public static int Run()
    {
        var task = System.Threading.Tasks.Task.FromResult(1);
        return task.Result;
    }
}
EOF

cat > Consumer/InterpolatedLogging.cs <<'EOF'
// CA2254: interpolated string passed to a logging template.
using Microsoft.Extensions.Logging;

public static class InterpolatedLogging
{
    public static void Run(ILogger logger, string name)
    {
        logger.LogInformation($"hello {name}");
    }
}
EOF

cat > Consumer/UnusedCode.cs <<'EOF'
// IDE0051: unused private member. IDE0052: unread private member. IDE0060: unused parameter.
public static class UnusedCode
{
    private static int _unused = 5;

    private static int _unread;

    private static int NeverCalled(int used, int unusedParameter)
    {
        return used + _unread;
    }
}
EOF

# Commit the full source so the source revision matches every compilation input.
git init -b main >/dev/null
git config user.email fixture@local
git config user.name fixture
git add -A
git commit -m "init" >/dev/null
SRC_REV=$(git rev-parse HEAD)

# --- fail-closed -----------------------------------------------------------

log "check fails without a trusted baseline"
expect_fail 1 "no baseline" dotnet iimmpact-quality check "$SLN" --base "$SRC_REV"
assert_contains "$LAST_LOG" "baseline not found" "check reports missing baseline"
assert_contains "$LAST_LOG" "schema-2 baseline" "check names the trusted baseline"

log "bootstrap proposes a baseline"
dotnet iimmpact-quality bootstrap "$SLN" --source "$SRC_REV" | tee "$WORK/bootstrap.log"
assert_contains "$WORK/bootstrap.log" "wrote code-quality-baseline.json" "bootstrap writes the file"
assert_contains "$WORK/bootstrap.log" "proposed" "bootstrap labels the output"
test -f "$BASELINE"

log "check passes with the committed baseline"
git add "$BASELINE" && git commit -m "baseline" >/dev/null
BASE_REV=$(git rev-parse HEAD)
dotnet iimmpact-quality check "$SLN" --base "$BASE_REV" | tee "$WORK/check-pass.log"
assert_contains "$WORK/check-pass.log" "PASS" "check passes"

log "a candidate-edited baseline cannot self-approve"
# Raise a ceiling only in the working tree. Candidate baselines must themselves be
# reductions of the trusted baseline, even when source is unchanged.
python3 - <<'PY'
import json
path = "code-quality-baseline.json"
with open(path) as f:
    data = json.load(f)
for a in data["allowances"]:
    if a["rule"] == "S138" and a.get("kind") == "metric":
        a["ceiling"] += 10
with open(path, "w") as f:
    json.dump(data, f, indent=2)
PY
expect_fail 1 "candidate baseline edit" dotnet iimmpact-quality check "$SLN" --base "$BASE_REV"
assert_contains "$LAST_LOG" "not a reduction" "candidate-only inflation is rejected"
cp "$BASELINE" "$WORK/forged-baseline.json"
expect_fail 1 "candidate baseline reduction" dotnet iimmpact-quality baseline "$SLN" --base "$BASE_REV"
assert_contains "$LAST_LOG" "not a reduction" "baseline refuses forged candidate"
cmp -s "$BASELINE" "$WORK/forged-baseline.json" || { echo "FAIL: refused baseline changed the file" >&2; exit 1; }
git checkout -- "$BASELINE"

log "bootstrap refuses a source-revision compile-set removal"
mv "$BASELINE" "$WORK/approved-baseline.json"
python3 - <<'PY'
path = "Consumer/Consumer.csproj"
text = open(path).read()
text = text.replace("</Project>", '  <ItemGroup><Compile Remove="BannedApis.cs" /></ItemGroup>\n</Project>')
open(path, "w").write(text)
PY
expect_fail 1 "bootstrap compile removal" dotnet iimmpact-quality bootstrap "$SLN" --source "$SRC_REV"
assert_contains "$LAST_LOG" "differs" "bootstrap rejects a changed compile set"
git checkout -- Consumer/Consumer.csproj
mv "$WORK/approved-baseline.json" "$BASELINE"

log "a malformed committed baseline is rejected"
git stash -q >/dev/null 2>&1 || true
python3 - <<'PY'
path = "code-quality-baseline.json"
with open(path) as f:
    data = f.read()
with open(path, "w") as f:
    f.write('{ "Consumer/Foo.cs": { "S134": { "count": 1 } } }')
PY
git add "$BASELINE" && git commit -m "aggregate schema" >/dev/null
AGG_REV=$(git rev-parse HEAD)
expect_fail 2 "aggregate baseline" dotnet iimmpact-quality check "$SLN" --base "$AGG_REV"
assert_contains "$LAST_LOG" "invalid baseline" "aggregate schema is rejected"
git reset --hard "$BASE_REV" >/dev/null

# --- per-declaration enforcement --------------------------------------------

log "metric growth inside the same declaration fails"
python3 - <<'PY'
path = "Consumer/Method61Lines.cs"
with open(path) as f:
    lines = f.readlines()
# Insert 10 more statements before the final x += 60 so the method measures 71.
idx = next(i for i, l in enumerate(lines) if "x += 60;" in l)
lines[idx:idx] = [f"        x += {60 + i};\n" for i in range(1, 11)]
with open(path, "w") as f:
    f.writelines(lines)
PY
expect_fail 1 "growth inside allowance" dotnet iimmpact-quality check "$SLN" --base "$BASE_REV"
assert_contains "$LAST_LOG" "Method61Lines" "growth is attributed to the method"
assert_contains "$LAST_LOG" "S138" "growth reports the rule"
assert_contains "$LAST_LOG" "exceeds approved ceiling" "ordinary source growth reports its metric ceiling"
git checkout -- Consumer/Method61Lines.cs

log "same metric in a sibling declaration fails"
{
  echo "// S138: a second 61-line method in a different declaration."
  echo "public static class Method61LinesNew"
  echo "{"
  echo "    public static int Run(int x) {"
  for i in $(seq 1 60); do
    echo "        x += $i;"
  done
  echo "        return x;"
  echo "    }"
  echo "}"
} > Consumer/Method61LinesNew.cs
expect_fail 1 "sibling allowance does not transfer" dotnet iimmpact-quality check "$SLN" --base "$BASE_REV"
assert_contains "$LAST_LOG" "Method61LinesNew" "new declaration fails"
rm Consumer/Method61LinesNew.cs

log "an analyzer-recognized generated filename cannot hide new debt"
sed 's/Method61Lines/HiddenDesigner/' Consumer/Method61Lines.cs > Consumer/Hidden.designer.cs
expect_fail 1 "designer generated bypass" dotnet iimmpact-quality check "$SLN" --base "$BASE_REV"
assert_contains "$LAST_LOG" "generated compile input" "unapproved designer source is rejected"
rm Consumer/Hidden.designer.cs

log "frozen scope digest change fails"
python3 - <<'PY'
path = "Consumer/NestingDepth5.cs"
with open(path) as f:
    text = f.read()
text = text.replace("r += j;", "r += j + 1;")
with open(path, "w") as f:
    f.write(text)
PY
expect_fail 1 "edited frozen scope" dotnet iimmpact-quality check "$SLN" --base "$BASE_REV"
assert_contains "$LAST_LOG" "NestingDepth5" "edited scope is attributed"
assert_contains "$LAST_LOG" "S134" "edited scope reports S134"
git checkout -- Consumer/NestingDepth5.cs

log "a comment inside a frozen owner does not move its token anchor"
python3 - <<'PY'
path = "Consumer/NestingDepth5.cs"
text = open(path).read().replace("var r = 0;", "// token-free comment inside owner\n        var r = 0;")
open(path, "w").write(text)
PY
dotnet iimmpact-quality check "$SLN" --base "$BASE_REV" | tee "$WORK/check-inner-comment.log"
assert_contains "$WORK/check-inner-comment.log" "PASS" "owner-internal trivia remains stable"
git checkout -- Consumer/NestingDepth5.cs

log "a comment added above a violation does not invalidate it"
# Frozen sites are token positions, so a preceding comment must still pass while
# the declaration's tokens are unchanged.
python3 - <<'PY'
path = "Consumer/NestingDepth5.cs"
with open(path) as f:
    text = f.read()
text = text.replace("// S134: nesting depth 5 (max 4).", "// S134: nesting depth 5 (max 4).\n// inserted comment\n// another comment", 1)
with open(path, "w") as f:
    f.write(text)
PY
dotnet iimmpact-quality check "$SLN" --base "$BASE_REV" | tee "$WORK/check-comment.log"
assert_contains "$WORK/check-comment.log" "PASS" "comment-only edit still passes"
git checkout -- Consumer/NestingDepth5.cs

# --- policy fail-closed -----------------------------------------------------

log "uppercase NONE in .editorconfig fails"
cat > Consumer/.editorconfig <<'EOF'
root = true

[*.cs]
dotnet_diagnostic.S134.severity = NONE
EOF
expect_fail 2 "editorconfig NONE" dotnet iimmpact-quality check "$SLN" --base "$BASE_REV"
assert_contains "$LAST_LOG" "S134" "weakened rule named"
assert_contains "$LAST_LOG" "effective severity" "effective weakening is named"
rm Consumer/.editorconfig

log "NoWarn on a managed rule fails"
cat > Consumer/Consumer.csproj <<'EOF'
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net8.0</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
    <NoWarn>S138;CS1591</NoWarn>
  </PropertyGroup>
</Project>
EOF
expect_fail 2 "NoWarn managed rule" dotnet iimmpact-quality check "$SLN" --base "$BASE_REV"
assert_contains "$LAST_LOG" "S138" "NoWarn rule named"
assert_contains "$LAST_LOG" "actual csc command line" "NoWarn reaches the compiler command line"
# Restore the project.
git checkout -- Consumer/Consumer.csproj

log "code-style analyzer execution is required"
cat > Consumer/Consumer.csproj <<'EOF'
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net8.0</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
    <EnforceCodeStyleInBuild>false</EnforceCodeStyleInBuild>
  </PropertyGroup>
</Project>
EOF
expect_fail 2 "code-style analyzers disabled" dotnet iimmpact-quality check "$SLN" --base "$BASE_REV"
assert_contains "$LAST_LOG" "IDE0051" "missing code-style sentinel is named"
git checkout -- Consumer/Consumer.csproj

log "a fake generated exclusion fails"
cat > Consumer/Consumer.csproj <<'EOF'
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net8.0</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
  </PropertyGroup>
  <ItemGroup>
    <Compile Include="obj/Fake.g.cs" />
  </ItemGroup>
</Project>
EOF
mkdir -p Consumer/obj
cat > Consumer/obj/Fake.g.cs <<'EOF'
// Not compiler-generated; a hand-planted file under obj.
public static class Fake { }
EOF
expect_fail 1 "fake generated marker" dotnet iimmpact-quality check "$SLN" --base "$BASE_REV"
assert_contains "$LAST_LOG" "generated compile input" "fake exclusion rejected"
rm -f Consumer/obj/Fake.g.cs
git checkout -- Consumer/Consumer.csproj

log "missing evidence for an expected existing TFM fails analysis"
cat > Consumer/Consumer.csproj <<'EOF'
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFrameworks>net8.0;net9.0</TargetFrameworks>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
  </PropertyGroup>
  <Target Name="DeleteExpectedEvidence" AfterTargets="IimmpactQualityWriteScanEvidence" Condition="'$(TargetFramework)' == 'net9.0'">
    <Delete Files="$(IimmpactQualitySarifDir)\scan.txt" />
  </Target>
</Project>
EOF
expect_fail 2 "missing existing TFM evidence" dotnet iimmpact-quality check "$SLN" --base "$BASE_REV"
assert_contains "$LAST_LOG" "net9.0" "missing evidence names the expected TFM"
git checkout -- Consumer/Consumer.csproj

# --- scan completeness ------------------------------------------------------

log "missing TFM compilation fails"
cat > Consumer/Consumer.csproj <<'EOF'
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFrameworks>net8.0;net9.0</TargetFrameworks>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
  </PropertyGroup>
</Project>
EOF
expect_fail 1 "missing TFM" dotnet iimmpact-quality check "$SLN" --base "$BASE_REV"
assert_contains "$LAST_LOG" "net9.0" "missing TFM is named"
assert_contains "$LAST_LOG" "no baseline allowance" "unbaselined TFM violations fail"
git checkout -- Consumer/Consumer.csproj

log "analyzers disabled fails"
# The tool rebuilds the solution itself, so the skip must come from the project:
# RunAnalyzersDuringBuild=false in the csproj makes the tool's own build skip
# analyzers and the evidence records it.
cat > Consumer/Consumer.csproj <<'EOF'
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net8.0</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
    <RunAnalyzersDuringBuild>false</RunAnalyzersDuringBuild>
  </PropertyGroup>
</Project>
EOF
expect_fail 2 "analyzers disabled" dotnet iimmpact-quality check "$SLN" --base "$BASE_REV"
assert_contains "$LAST_LOG" "skipped analyzers" "analyzer skip is named"
git checkout -- Consumer/Consumer.csproj

log "malformed SARIF fails"
# The tool rebuilds and rewrites the SARIF itself, so corruption must be injected
# during the tool's own build: a project target overwrites the log after evidence
# is written.
cat > Consumer/Consumer.csproj <<'EOF'
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net8.0</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
  </PropertyGroup>
  <Target Name="CorruptSarif" AfterTargets="IimmpactQualityWriteScanEvidence">
    <WriteLinesToFile File="$(IimmpactQualitySarifDir)\scan.sarif"
                      Lines="not json" Overwrite="true" />
  </Target>
</Project>
EOF
expect_fail 2 "malformed SARIF" dotnet iimmpact-quality check "$SLN" --base "$BASE_REV"
assert_contains "$LAST_LOG" "malformed SARIF" "malformed SARIF named"
git checkout -- Consumer/Consumer.csproj

log "analyzer failure fails"
# Same injection point: an AD0001 result appended after the real log is written
# proves analyzer crashes fail the gate.
cat > Consumer/Consumer.csproj <<'EOF'
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net8.0</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
  </PropertyGroup>
  <Target Name="InjectAnalyzerFailure" AfterTargets="IimmpactQualityWriteScanEvidence">
    <Exec Command="python3 -c &quot;import json; p=r'$(IimmpactQualitySarifDir)/scan.sarif'; d=json.load(open(p)); d['runs'][0]['results'].append({'ruleId':'AD0001','level':'warning','message':{'text':'Analyzer threw'},'locations':[]}); json.dump(d, open(p,'w'))&quot;" />
  </Target>
</Project>
EOF
expect_fail 2 "analyzer failure" dotnet iimmpact-quality check "$SLN" --base "$BASE_REV"
assert_contains "$LAST_LOG" "AD0001" "analyzer failure named"
git checkout -- Consumer/Consumer.csproj

# --- regenerated baseline ----------------------------------------------------

log "baseline reduction lowers debt"
rm Consumer/Method61LinesB.cs
dotnet iimmpact-quality baseline "$SLN" --base "$BASE_REV" | tee "$WORK/baseline-shrink.log"
assert_contains "$WORK/baseline-shrink.log" "reduced" "baseline reports the reduction"
dotnet iimmpact-quality check "$SLN" --base "$BASE_REV" | tee "$WORK/check-shrink.log"
assert_contains "$WORK/check-shrink.log" "PASS" "reduced baseline still passes"

# --- clean scan passes -------------------------------------------------------

log "an empty clean compilation passes"
rm -rf Consumer/bin Consumer/obj
# Keep only a trivially clean source.
for f in Consumer/*.cs; do
  case "$(basename "$f")" in
    BannedApis.cs|Complexity26.cs|NestingDepth5.cs|Method61Lines.cs|File401Lines.cs|AsyncBlocking.cs|InterpolatedLogging.cs|UnusedCode.cs) rm "$f" ;;
  esac
done
cat > Consumer/Clean.cs <<'EOF'
public static class Clean
{
    public static int Add(int a, int b) => a + b;
}
EOF
rm -f "$BASELINE"
git add -A && git commit -m "clean" >/dev/null
CLEAN_REV=$(git rev-parse HEAD)
dotnet iimmpact-quality bootstrap "$SLN" --source "$CLEAN_REV" | tee "$WORK/bootstrap-clean.log"
assert_contains "$WORK/bootstrap-clean.log" "wrote code-quality-baseline.json" "clean bootstrap writes the file"
git add "$BASELINE" && git commit -m "clean baseline" >/dev/null
dotnet iimmpact-quality check "$SLN" --base "$(git rev-parse HEAD)" | tee "$WORK/check-clean.log"
assert_contains "$WORK/check-clean.log" "PASS" "clean scan passes"

log "ALL CHECKS PASSED"
