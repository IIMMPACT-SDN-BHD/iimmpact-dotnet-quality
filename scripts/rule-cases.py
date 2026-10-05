#!/usr/bin/env python3
"""Bad and corrected source for each correctness rule added in 0.2.0.

Usage: rule-cases.py list              -> prints the rule IDs
       rule-cases.py RULE bad|good FILE -> writes that source to FILE
"""
import sys

PREFIX = (
    "using System; using System.IO; using System.Collections.Generic; "
    "using System.Threading.Tasks; using Microsoft.Extensions.Logging;\n"
    "namespace RuleProbes;\n"
)
RESOURCE = "public sealed class Resource : IDisposable { public void Touch() {} public void Dispose() {} }"

CASES = {
    "CA2200": (
        "public class Probe { public void Run() { try { Work(); } catch (Exception ex) { throw ex; } } private static void Work() => throw new InvalidOperationException(); }",
        "public class Probe { public void Run() { try { Work(); } catch (Exception) { throw; } } private static void Work() => throw new InvalidOperationException(); }"),
    "CA2017": (
        'public class Probe { public void Run(ILogger log) => log.LogInformation("{Name}", "one", "two"); }',
        'public class Probe { public void Run(ILogger log) => log.LogInformation("{Name} {Other}", "one", "two"); }'),
    "CA2000": (
        "public class Probe { public void Run() { var item = new Resource(); item.Touch(); } } " + RESOURCE,
        # Ownership transfer to the caller or to a wrapper must stay clean.
        "public class Probe { public void Run() { using var item = new Resource(); item.Touch(); } "
        "public IDisposable Create() => new Resource(); public StreamReader Wrap(Stream stream) => new StreamReader(stream); } " + RESOURCE),
    "CA2008": (
        "public class Probe { public Task Run() => Task.Factory.StartNew(() => 42); }",
        "public class Probe { public Task Run() => Task.Run(() => 42); }"),
    "CA2012": (
        "public class Probe { public async Task<int> Run() { var value = NextAsync(); int a = await value; int b = await value; return a + b; } private static async ValueTask<int> NextAsync() { await Task.Delay(1); return 42; } }",
        "public class Probe { public async Task<int> Run() { int a = await NextAsync(); int b = await NextAsync(); return a + b; } private static async ValueTask<int> NextAsync() { await Task.Delay(1); return 42; } }"),
    "CA2013": (
        "public class Probe { public bool Run(int value) => object.ReferenceEquals(value, value); }",
        "public class Probe { public bool Run(int value) => object.Equals(value, value); }"),
    "CA2022": (
        "public class Probe { public void Run(Stream stream, byte[] buffer) { stream.Read(buffer, 0, buffer.Length); } }",
        "public class Probe { public void Run(Stream stream, byte[] buffer) { stream.ReadExactly(buffer); } }"),
    "CA2208": (
        'public class Probe { public void Run(string value) { throw new ArgumentNullException("other"); } }',
        "public class Probe { public void Run(string value) { throw new ArgumentNullException(nameof(value)); } }"),
    "CA2214": (
        "public class Probe { public Probe() { Initialize(); } public virtual void Initialize() {} }",
        "public class Probe { public virtual void Initialize() {} }"),
    "CA2241": (
        'public class Probe { public string Run(string value) => string.Format("{0} {1}", value); }',
        'public class Probe { public string Run(string value) => string.Format("{0}", value); }'),
    "CA2019": (
        'public class Probe { [ThreadStatic] private static string? _value = "value"; public string Value => _value ?? ""; }',
        'public class Probe { [ThreadStatic] private static string? _value; public string Value => _value ??= "value"; }'),
    "S3776": (
        "public class Probe { public int Run(int a, int b, int c, int d, int e, int f) { if(a > 0) { if(b > 0) { if(c > 0) { if(d > 0) { if(e > 0) { if(f > 0) { return 1; } } } } } } return 0; } }",
        "public class Probe { public int Run(int a, int b, int c, int d, int e, int f) { if(a <= 0) return 0; if(b <= 0) return 0; if(c <= 0) return 0; if(d <= 0) return 0; if(e <= 0) return 0; if(f <= 0) return 0; return 1; } }"),
    "CS8600": (
        "public class Probe { public int Run(string? input) { string value = input; return value?.Length ?? 0; } }",
        "public class Probe { public int Run(string? input) { string? value = input; return value?.Length ?? 0; } }"),
    "CS8601": (
        'public class Probe { public string Value { get; private set; } = ""; public void Run(string? value) { Value = value; } }',
        'public class Probe { public string Value { get; private set; } = ""; public void Run(string? value) { Value = value ?? ""; } }'),
    "CS8602": (
        "public class Probe { public int Run(string? value) => value.Length; }",
        "public class Probe { public int Run(string? value) => value?.Length ?? 0; }"),
    "CS8603": (
        "public class Probe { public string Run(string? value) => value; }",
        'public class Probe { public string Run(string? value) => value ?? ""; }'),
    "CS8604": (
        "public class Probe { public int Run(string? value) => Read(value); private static int Read(string value) => value.Length; }",
        'public class Probe { public int Run(string? value) => Read(value ?? ""); private static int Read(string value) => value.Length; }'),
    "CS8618": (
        "public class Probe { public string Value { get; set; } }",
        'public class Probe { public string Value { get; set; } = ""; }'),
    "CS8619": (
        "public class Probe { public IEnumerable<string> Run(string? value) => new string?[] { value }; }",
        'public class Probe { public IEnumerable<string> Run(string? value) => new string[] { value ?? "" }; }'),
    "CS8625": (
        "public class Probe { public string Value { get; set; } = null; }",
        'public class Probe { public string Value { get; set; } = ""; }'),
    "CS8629": (
        "public class Probe { public int Run(int? value) => value.Value; }",
        "public class Probe { public int Run(int? value) => value ?? 0; }"),
}

if sys.argv[1] == "list":
    print("\n".join(CASES))
else:
    rule, state, path = sys.argv[1:4]
    bad, good = CASES[rule]
    with open(path, "w") as f:
        f.write(PREFIX + (bad if state == "bad" else good) + "\n")
