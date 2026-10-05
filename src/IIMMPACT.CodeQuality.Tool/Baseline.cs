using System.Text.Json;

internal abstract record Allowance(EntityKey Entity, string Rule);

internal sealed record MetricAllowance(EntityKey Entity, string Rule, long Ceiling)
    : Allowance(Entity, Rule);

internal sealed record FrozenScopeAllowance(
    EntityKey Entity, string Rule, string TokenDigest, TokenSite[] Sites)
    : Allowance(Entity, Rule);

internal sealed class Baseline
{
    public const string SchemaName = "iimmpact-quality-baseline";
    public const int SchemaVersion = 2;

    private static readonly HashSet<string> RootProperties =
        ["schema", "version", "policy", "generatedExclusions", "allowances"];
    private static readonly HashSet<string> CommonAllowanceProperties =
        ["kind", "project", "tfm", "source", "declaration", "rule"];
    private static readonly HashSet<string> MetricProperties =
        [.. CommonAllowanceProperties, "ceiling"];
    private static readonly HashSet<string> FrozenProperties =
        [.. CommonAllowanceProperties, "tokenDigest", "sites"];

    private readonly List<Allowance> _allowances;
    private readonly IReadOnlyDictionary<string, string> _generatedExclusions;

    private Baseline(List<Allowance> allowances, IReadOnlyDictionary<string, string> generatedExclusions)
    {
        _allowances = allowances;
        _generatedExclusions = generatedExclusions;
    }

    public IReadOnlyList<Allowance> Allowances => _allowances;
    public IReadOnlyDictionary<string, string> GeneratedExclusions => _generatedExclusions;

    public static Baseline Parse(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = RequireObject(document.RootElement, "baseline");
            RequireOnlyProperties(root, RootProperties, "baseline");
            if (RequireString(root, "schema") != SchemaName)
            {
                throw new BaselineFormatException($"baseline schema must be '{SchemaName}'");
            }
            if (RequireInt32(root, "version") != SchemaVersion)
            {
                throw new BaselineFormatException(
                    $"baseline version must be {SchemaVersion}; aggregate per-file baselines are no longer supported");
            }
            if (RequireString(root, "policy") != Policy.Name)
            {
                throw new BaselineFormatException($"baseline policy must be '{Policy.Name}'");
            }

            var exclusions = new Dictionary<string, string>(StringComparer.Ordinal);
            if (root.TryGetProperty("generatedExclusions", out var exclusionsElement))
            {
                RequireArray(exclusionsElement, "generatedExclusions");
                foreach (var entry in exclusionsElement.EnumerateArray())
                {
                    RequireOnlyProperties(RequireObject(entry, "generated exclusion"), new HashSet<string>(["path", "sha256"]), "generated exclusion");
                    var path = RequirePath(entry, "path");
                    var digest = RequireDigest(entry, "sha256");
                    if (!exclusions.TryAdd(path, digest))
                    {
                        throw new BaselineFormatException($"duplicate generated exclusion '{path}'");
                    }
                }
            }

            if (!root.TryGetProperty("allowances", out var allowancesElement))
            {
                throw new BaselineFormatException("baseline must contain an 'allowances' array");
            }
            RequireArray(allowancesElement, "allowances");
            var allowances = allowancesElement.EnumerateArray().Select(ParseAllowance).ToList();
            var duplicate = allowances.GroupBy(a => (a.Entity, a.Rule)).FirstOrDefault(g => g.Count() > 1);
            if (duplicate is not null)
            {
                throw new BaselineFormatException(
                    $"duplicate baseline allowance '{duplicate.Key.Entity.Declaration}' {duplicate.Key.Rule}");
            }
            return FromAllowances(allowances, exclusions);
        }
        catch (BaselineFormatException)
        {
            throw;
        }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException or OverflowException)
        {
            throw new BaselineFormatException(exception.Message);
        }
    }

    private static Allowance ParseAllowance(JsonElement element)
    {
        RequireObject(element, "allowance");
        var kind = RequireString(element, "kind");
        RequireOnlyProperties(element, kind switch
        {
            "metric" => MetricProperties,
            "frozen" => FrozenProperties,
            _ => throw new BaselineFormatException($"unknown allowance kind '{kind}'"),
        }, "allowance");

        var project = RequirePath(element, "project");
        if (!project.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase))
        {
            throw new BaselineFormatException("allowance project must be a repository-relative .csproj path");
        }
        var entity = new EntityKey(
            new CompilationKey(project, RequireNonEmptyString(element, "tfm")),
            RequirePath(element, "source"),
            RequireNonEmptyString(element, "declaration"));
        var rule = RequireNonEmptyString(element, "rule").ToUpperInvariant();
        if (!ManagedRules.Ids.Contains(rule))
        {
            throw new BaselineFormatException($"unknown managed rule '{rule}'");
        }

        if (kind == "metric")
        {
            if (!ManagedRules.MetricIds.Contains(rule))
            {
                throw new BaselineFormatException($"rule {rule} requires a frozen allowance");
            }
            var ceiling = RequireInt64(element, "ceiling");
            if (ceiling <= 0)
            {
                throw new BaselineFormatException("metric ceiling must be positive");
            }
            return new MetricAllowance(entity, rule, ceiling);
        }

        if (ManagedRules.MetricIds.Contains(rule))
        {
            throw new BaselineFormatException($"rule {rule} requires a metric allowance");
        }
        var digest = RequireDigest(element, "tokenDigest");
        if (!element.TryGetProperty("sites", out var sitesElement))
        {
            throw new BaselineFormatException("frozen allowance must contain sites");
        }
        RequireArray(sitesElement, "sites");
        var sites = sitesElement.EnumerateArray().Select(site =>
        {
            RequireOnlyProperties(RequireObject(site, "token site"), new HashSet<string>(["token", "offset"]), "token site");
            var token = RequireInt32(site, "token");
            var offset = RequireInt32(site, "offset");
            if (token < 0 || offset < 0)
            {
                throw new BaselineFormatException("token site values must be non-negative");
            }
            return new TokenSite(token, offset);
        }).ToArray();
        if (sites.Length == 0 || sites.Distinct().Count() != sites.Length)
        {
            throw new BaselineFormatException("frozen allowance sites must be non-empty and unique");
        }
        return new FrozenScopeAllowance(entity, rule, digest, sites);
    }

    public static Baseline FromAllowances(
        IEnumerable<Allowance> allowances,
        IReadOnlyDictionary<string, string> generatedExclusions) =>
        new(
            allowances.OrderBy(a => a.Entity.Compilation.Project, StringComparer.Ordinal)
                .ThenBy(a => a.Entity.Compilation.TargetFramework, StringComparer.Ordinal)
                .ThenBy(a => a.Entity.Source, StringComparer.Ordinal)
                .ThenBy(a => a.Entity.Declaration, StringComparer.Ordinal)
                .ThenBy(a => a.Rule, StringComparer.Ordinal)
                .ToList(),
            new SortedDictionary<string, string>(generatedExclusions.ToDictionary(kv => kv.Key, kv => kv.Value), StringComparer.Ordinal));

    public List<string> ValidateReductionOf(Baseline trusted)
    {
        var failures = new List<string>();
        var trustedAllowances = trusted.Allowances.ToDictionary(a => (a.Entity, a.Rule));
        foreach (var candidate in Allowances)
        {
            if (!trustedAllowances.TryGetValue((candidate.Entity, candidate.Rule), out var approved)
                || !IsReduction(candidate, approved))
            {
                failures.Add($"candidate allowance {candidate.Entity.Source} {candidate.Entity.Declaration} {candidate.Rule} is not a reduction of the trusted baseline");
            }
        }
        foreach (var (path, digest) in GeneratedExclusions)
        {
            if (!trusted.GeneratedExclusions.TryGetValue(path, out var approved) || approved != digest)
            {
                failures.Add($"candidate generated exclusion '{path}' is not a reduction of the trusted baseline");
            }
        }
        return failures;
    }

    private static bool IsReduction(Allowance candidate, Allowance trusted) => (candidate, trusted) switch
    {
        (MetricAllowance c, MetricAllowance t) => c.Ceiling <= t.Ceiling,
        (FrozenScopeAllowance c, FrozenScopeAllowance t) =>
            c.TokenDigest == t.TokenDigest && c.Sites.OrderBy(SiteKey).SequenceEqual(t.Sites.OrderBy(SiteKey)),
        _ => false,
    };

    public List<string> Compare(IReadOnlyList<ScanViolation> violations)
    {
        var failures = new List<string>();
        var lookup = _allowances.ToDictionary(a => (a.Entity, a.Rule));
        foreach (var violation in violations)
        {
            if (!lookup.TryGetValue((violation.Entity, violation.Rule), out var allowance))
            {
                failures.Add(FormatViolation(violation, "no baseline allowance"));
            }
            else if (!Covers(allowance, violation))
            {
                failures.Add(FormatViolation(violation, allowance switch
                {
                    MetricAllowance metric when violation.Metric is { } actual =>
                        $"metric {actual} exceeds approved ceiling {metric.Ceiling}",
                    MetricAllowance => "no measured value for a metric rule",
                    _ => "declaration body or diagnostic sites differ from the approved scope",
                }));
            }
        }
        return failures;
    }

    private static bool Covers(Allowance allowance, ScanViolation violation) => (allowance, violation) switch
    {
        (MetricAllowance metric, { Metric: { } actual }) => actual <= metric.Ceiling,
        (FrozenScopeAllowance frozen, _) =>
            frozen.TokenDigest == violation.TokenDigest
            && frozen.Sites.OrderBy(SiteKey).SequenceEqual(violation.TokenSites.OrderBy(SiteKey)),
        _ => false,
    };

    private static string SiteKey(TokenSite site) => $"{site.Token:D10}\0{site.Offset:D10}";

    private static string FormatViolation(ScanViolation violation, string reason) =>
        $"{violation.PhysicalSite.Path}({violation.PhysicalSite.Line},{violation.PhysicalSite.Column}): error {violation.Rule}: {violation.Message} [{violation.Entity.Declaration}: {reason}]";

    public Baseline Reduce(IReadOnlyList<ScanViolation> currentViolations, out List<ScanViolation> uncovered)
    {
        uncovered = Compare(currentViolations).Count == 0 ? [] : currentViolations.Where(v =>
            !_allowances.Any(a => a.Entity == v.Entity && a.Rule == v.Rule && Covers(a, v))).ToList();
        if (uncovered.Count > 0)
        {
            return this;
        }

        var violations = currentViolations.GroupBy(v => (v.Entity, v.Rule)).ToDictionary(g => g.Key, g => g.ToList());
        var reduced = new List<Allowance>();
        foreach (var allowance in _allowances)
        {
            if (!violations.TryGetValue((allowance.Entity, allowance.Rule), out var current))
            {
                continue;
            }
            reduced.Add(allowance is MetricAllowance metric
                ? metric with { Ceiling = current.Max(v => v.Metric!.Value) }
                : allowance);
        }
        return FromAllowances(reduced, _generatedExclusions);
    }

    public string ToJson()
    {
        var shape = new Dictionary<string, object?>
        {
            ["schema"] = SchemaName,
            ["version"] = SchemaVersion,
            ["policy"] = Policy.Name,
            ["generatedExclusions"] = _generatedExclusions.Select(kv => new { path = kv.Key, sha256 = kv.Value }).ToArray(),
            ["allowances"] = _allowances.Select(ToShape).ToArray(),
        };
        return JsonSerializer.Serialize(shape, new JsonSerializerOptions { WriteIndented = true }) + "\n";
    }

    private static object ToShape(Allowance allowance)
    {
        var common = new Dictionary<string, object>
        {
            ["kind"] = allowance is MetricAllowance ? "metric" : "frozen",
            ["project"] = allowance.Entity.Compilation.Project,
            ["tfm"] = allowance.Entity.Compilation.TargetFramework,
            ["source"] = allowance.Entity.Source,
            ["declaration"] = allowance.Entity.Declaration,
            ["rule"] = allowance.Rule,
        };
        if (allowance is MetricAllowance metric)
        {
            common["ceiling"] = metric.Ceiling;
        }
        else if (allowance is FrozenScopeAllowance frozen)
        {
            common["tokenDigest"] = frozen.TokenDigest;
            common["sites"] = frozen.Sites.Select(s => new { token = s.Token, offset = s.Offset }).ToArray();
        }
        return common;
    }

    private static JsonElement RequireObject(JsonElement element, string name)
    {
        if (element.ValueKind != JsonValueKind.Object) throw new BaselineFormatException($"{name} must be an object");
        return element;
    }

    private static void RequireArray(JsonElement element, string name)
    {
        if (element.ValueKind != JsonValueKind.Array) throw new BaselineFormatException($"{name} must be an array");
    }

    private static void RequireOnlyProperties(JsonElement element, IReadOnlySet<string> allowed, string name)
    {
        var duplicate = element.EnumerateObject().GroupBy(p => p.Name, StringComparer.Ordinal).FirstOrDefault(g => g.Count() > 1);
        if (duplicate is not null) throw new BaselineFormatException($"duplicate {name} property '{duplicate.Key}'");
        foreach (var property in element.EnumerateObject())
        {
            if (!allowed.Contains(property.Name))
                throw new BaselineFormatException($"unknown {name} property '{property.Name}'");
        }
    }

    private static string RequireString(JsonElement element, string property)
    {
        if (!element.TryGetProperty(property, out var value) || value.ValueKind != JsonValueKind.String)
            throw new BaselineFormatException($"'{property}' must be a string");
        return value.GetString()!;
    }

    private static string RequireNonEmptyString(JsonElement element, string property)
    {
        var value = RequireString(element, property);
        if (string.IsNullOrWhiteSpace(value)) throw new BaselineFormatException($"'{property}' must not be empty");
        return value;
    }

    private static string RequirePath(JsonElement element, string property)
    {
        var value = RequireNonEmptyString(element, property);
        if (Path.IsPathRooted(value) || value.Contains('\\') || value.Split('/').Any(p => p is "" or "." or ".."))
            throw new BaselineFormatException($"'{property}' must be a normalized relative path");
        return value;
    }

    private static string RequireDigest(JsonElement element, string property)
    {
        var value = RequireNonEmptyString(element, property);
        if (value.Length != 64 || value.Any(c => !Uri.IsHexDigit(c)))
            throw new BaselineFormatException($"'{property}' must be a 64-character SHA-256 digest");
        return value.ToLowerInvariant();
    }

    private static int RequireInt32(JsonElement element, string property)
    {
        if (!element.TryGetProperty(property, out var value) || !value.TryGetInt32(out var number))
            throw new BaselineFormatException($"'{property}' must be an integer");
        return number;
    }

    private static long RequireInt64(JsonElement element, string property)
    {
        if (!element.TryGetProperty(property, out var value) || !value.TryGetInt64(out var number))
            throw new BaselineFormatException($"'{property}' must be an integer");
        return number;
    }
}

internal sealed class BaselineFormatException : Exception
{
    public BaselineFormatException(string message) : base(message) { }
}
