/// <summary>
/// Why `check` failed. Declaration order is the order groups are reported in:
/// baseline problems first, then the code findings.
/// </summary>
internal enum FailureKind
{
    BaselineNotReduced,
    NewDiagnostic,
    MetricAboveCeiling,
    MetricUnmeasured,
    FrozenScopeChanged,
    GeneratedExclusionChanged,
}

/// <summary>
/// One failed gate decision. <see cref="Line"/> is the console and problem-matcher
/// text; the other fields feed the step-summary table.
/// </summary>
internal sealed record GateFailure(
    FailureKind Kind, string Line, string Location, string Rule, string Declaration, string Detail)
{
    public static GateFailure ForViolation(FailureKind kind, ScanViolation violation, string reason)
    {
        var site = violation.PhysicalSite;
        var location = $"{site.Path}({site.Line},{site.Column})";
        return new GateFailure(
            kind,
            $"{location}: error {violation.Rule}: {violation.Message} [{violation.Entity.Declaration}: {reason}]",
            location, violation.Rule, violation.Entity.Declaration, $"{violation.Message} ({reason})");
    }

    public static GateFailure ForPath(FailureKind kind, string path, string detail) =>
        new(kind, $"{path}: {detail}", path, string.Empty, string.Empty, detail);
}

/// <summary>Group titles and fix hints, written for an engineer or coding agent reading CI output.</summary>
internal static class FailureGuidance
{
    public static string Title(FailureKind kind) => kind switch
    {
        FailureKind.BaselineNotReduced => "Baseline not reduced",
        FailureKind.NewDiagnostic => "New diagnostics",
        FailureKind.MetricAboveCeiling => "Metrics above approved ceiling",
        FailureKind.MetricUnmeasured => "Metric rules without a measured value",
        FailureKind.FrozenScopeChanged => "Frozen scopes changed",
        FailureKind.GeneratedExclusionChanged => "Generated sources changed",
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };

    public static string Hint(FailureKind kind) => kind switch
    {
        FailureKind.BaselineNotReduced =>
            "the working-tree code-quality-baseline.json adds or raises an allowance compared with the trusted base; revert those entries.",
        FailureKind.NewDiagnostic =>
            "fix the code at each listed site; the baseline only shrinks, so never add allowances or suppressions.",
        FailureKind.MetricAboveCeiling =>
            "bring the file, type or method back to its approved ceiling by splitting along a coherent responsibility; do not raise the ceiling.",
        FailureKind.MetricUnmeasured =>
            "the scan produced no measurement for a metric rule; this is a tool problem, so report it to IIMMPACT-SDN-BHD/iimmpact-dotnet-quality.",
        FailureKind.FrozenScopeChanged =>
            "this declaration has recorded debt and its code changed, so it must now be clean; remove every managed diagnostic in each listed declaration. "
            + "Then `dotnet iimmpact-quality baseline <solution> --base <commit>` drops the unused allowances (optional, check passes without it).",
        FailureKind.GeneratedExclusionChanged =>
            "a pull request cannot add or change generated sources; ask the gate owner to approve the change (known limitation).",
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };

    public const string ToolProblem =
        "This is a build, configuration or tool problem, not a code finding. Fix the error shown above; " +
        "if the build is clean, report this log to IIMMPACT-SDN-BHD/iimmpact-dotnet-quality.";
}
