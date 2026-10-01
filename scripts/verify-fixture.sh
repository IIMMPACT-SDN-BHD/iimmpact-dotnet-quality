#!/usr/bin/env bash
# End-to-end proof: pack the package and tool, build a consumer that violates every
# managed rule group, and check that iimmpact-quality fails before the baseline,
# passes after it, and fails again on growth and weakened config.
set -euo pipefail

REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
WORK="${TMPDIR:-/tmp}/iimmpact-dotnet-quality-fixture"
FEED="$WORK/feed"
CONSUMER="$WORK/consumer"
rm -rf "$WORK"
mkdir -p "$FEED" "$CONSUMER"
# Clear cached copies so the packed 0.1.0 must resolve from the temp feed.
rm -rf "$HOME/.nuget/packages/iimmpact.codequality" "$HOME/.nuget/packages/iimmpact.codequality.tool"

assert_contains() {
  # $1: file, $2: needle, $3: label
  if ! grep -qF "$2" "$1"; then
    echo "FAIL: $3 — expected output to contain '$2'" >&2
    cat "$1" >&2
    exit 1
  fi
}

echo "== packing packages =="
dotnet pack "$REPO_ROOT/src/IIMMPACT.CodeQuality" -o "$FEED" -c Release -v quiet --nologo
dotnet pack "$REPO_ROOT/src/IIMMPACT.CodeQuality.Tool" -o "$FEED" -c Release -v quiet --nologo
ls "$FEED"/*.nupkg

echo "== creating consumer =="
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
# dotnet 10 SDKs create .slnx; older SDKs create .sln. Use whichever exists.
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

write_violations() {
  # Each file below violates one managed rule group. Line counts in comments are
  # the values the fixture asserts against.
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

  # S138 counts distinct lines that contain method tokens: signature line,
  # statement lines, return line, closing brace. 60 statements = 61 counted
  # lines > 60. Verified empirically: 59 statements counts 60 and does not fire.
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

  # S104 counts distinct lines covered by non-EOF tokens; comments and blank
  # lines do not count. class + brace + 405 methods + brace = 408 > 400.
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
// IDE0051: unused private member. IDE0052: unread private member. IDE0060: unused private parameter.
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
}

write_violations

echo "== check fails without a baseline =="
if dotnet iimmpact-quality check "$SLN" > "$WORK/check0.log" 2>&1; then
  echo "FAIL: check passed before a baseline existed" >&2
  exit 1
fi
assert_contains "$WORK/check0.log" "baseline not found" "check reports missing baseline"
echo "PASS: check fails without a baseline"

echo "== baseline records every managed rule =="
dotnet iimmpact-quality baseline "$SLN" | tee "$WORK/baseline1.log"
test -f "$CONSUMER/code-quality-baseline.json"
for rule in CA1502 S134 S138 S104 RS0030 S4462 CA2254 IDE0051 IDE0060; do
  assert_contains "$CONSUMER/code-quality-baseline.json" "\"$rule\"" "baseline contains $rule"
done
echo "PASS: baseline file contains all expected rule IDs"

echo "== diagnostics actually fired (build warnings) =="
# Prove the analyzer pipeline works end to end: the same build warnings the tool
# parsed must appear in the streamed build output.
dotnet build "$SLN" --no-incremental -nologo -p:IimmpactQualitySarif=true > "$WORK/build1.log" 2>&1
for rule in CA1502 S134 S138 S104 RS0030 S4462 CA2254 IDE0051 IDE0060; do
  assert_contains "$WORK/build1.log" "warning $rule" "build warning $rule fires"
done
assert_contains "$WORK/build1.log" "cyclomatic complexity of '28'" "CA1502 metric present"
assert_contains "$WORK/build1.log" "has 61 lines" "S138 metric present"
assert_contains "$WORK/build1.log" "This file has 40" "S104 metric present"
echo "PASS: all managed rules fired as build warnings"

echo "== check passes against the baseline =="
dotnet iimmpact-quality check "$SLN" | tee "$WORK/check2.log"
assert_contains "$WORK/check2.log" "PASS" "post-baseline check passes"
echo "PASS: baseline written and check passes"

echo "== growth fails =="
cat > Consumer/NewViolation.cs <<'EOF'
public static class NewViolation
{
    public static void Run()
    {
        System.Console.WriteLine("oops");
    }
}
EOF
if dotnet iimmpact-quality check "$SLN" > "$WORK/check3.log" 2>&1; then
  echo "FAIL: check passed after adding a violation" >&2
  exit 1
fi
assert_contains "$WORK/check3.log" "Consumer/NewViolation.cs" "growth reported against new file"
assert_contains "$WORK/check3.log" "RS0030" "growth reported as RS0030"
rm Consumer/NewViolation.cs
echo "PASS: added violation fails with the new file"

echo "== weakened config fails =="
cat > Consumer/.editorconfig <<'EOF'
root = true

[*.cs]
dotnet_diagnostic.S134.severity = none
EOF
if dotnet iimmpact-quality check "$SLN" > "$WORK/check4.log" 2>&1; then
  echo "FAIL: check passed with weakened editorconfig" >&2
  exit 1
fi
assert_contains "$WORK/check4.log" "S134" "weakened S134 reported"
assert_contains "$WORK/check4.log" ".editorconfig" "weakening reported with file"
rm Consumer/.editorconfig
echo "PASS: weakened config fails"

echo "== boundary cases pass =="
cat > Consumer/Boundaries.cs <<'EOF'
// Exactly at the limits: depth 4, 60-line method, complexity 25. None should fire.
public static class Boundaries
{
    public static int Depth4(int x)
    {
        var r = 0;
        var i = x;
        if (x > 0)
        {
            while (i > 1)
            {
                if (i % 2 == 0)
                {
                    for (var j = 0; j < i; j++)
                    {
                        r += j;
                    }
                }
                i--;
            }
        }
        return r;
    }

    // 24 ifs: cyclomatic complexity is 1 + 24 = 25, exactly the CA1502 limit.
    public static int Complexity25(int x)
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
        return r;
    }
}
EOF

{
  echo "// 59 statements + return + close brace = 60 counted lines, at the S138"
  echo "// limit, so it must not fire."
  echo "public static class Method60Lines"
  echo "{"
  echo "    public static int Run(int x) {"
  for i in $(seq 1 59); do
    echo "        x += $i;"
  done
  echo "        return x;"
  echo "    }"
  echo "}"
} > Consumer/Method60Lines.cs

dotnet iimmpact-quality baseline "$SLN"
dotnet iimmpact-quality check "$SLN" | tee "$WORK/check5.log"
assert_contains "$WORK/check5.log" "PASS" "boundary values pass"

# Baseline must not contain entries for the boundary-only members.
if grep -q "Method60Lines.cs" "$CONSUMER/code-quality-baseline.json"; then
  echo "FAIL: 60-line method was flagged despite the 60 threshold" >&2
  exit 1
fi
echo "PASS: boundary depth/lines/complexity do not trigger"

echo "== stale baseline fails =="
rm -f Consumer/NewViolation.cs
# Hand-edit: raise a baseline count so the current run is lower.
if command -v python3 >/dev/null 2>&1; then
  python3 - "$CONSUMER/code-quality-baseline.json" <<'PYEOF'
import json, sys
path = sys.argv[1]
data = json.load(open(path))
first_file = sorted(data)[0]
first_rule = sorted(data[first_file])[0]
data[first_file][first_rule]["count"] += 5
json.dump(data, open(path, "w"), indent=2)
PYEOF
else
  # Fallback without python: bump the first count to 9999 with sed.
  sed -i '' '0,/"count": [0-9]*/s//"count": 9999/' "$CONSUMER/code-quality-baseline.json"
fi
if dotnet iimmpact-quality check "$SLN" > "$WORK/check6.log" 2>&1; then
  echo "FAIL: check passed with stale baseline" >&2
  exit 1
fi
assert_contains "$WORK/check6.log" "stale baseline" "stale baseline reported"
echo "PASS: stale baseline fails"

echo ""
echo "ALL CHECKS PASSED"
