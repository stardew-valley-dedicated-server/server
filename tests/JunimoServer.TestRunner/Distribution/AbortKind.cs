namespace JunimoServer.TestRunner.Distribution;

/// <summary>
/// Why a distributed run was aborted (in contrast to early-return failures from
/// the strict-policy preflight / image-transfer gates, which return exit code 2
/// without entering the abort funnel). Two terminal causes today:
/// <list type="bullet">
///   <item><see cref="CtrlC"/> — operator-initiated SIGINT.</item>
///   <item><see cref="DispatchFault"/> — unexpected exception thrown during
///     dispatch, wait, or merge that the abort funnel must intercept so the
///     dispatcher's cancel ladder, coordinator drain, and renderer stop run
///     instead of leaking the local in-process worker as an orphan.</item>
/// </list>
/// Adding a new cause means adding both an enum value and its
/// <see cref="AbortKindExtensions.ToReasonString"/> projection.
/// </summary>
internal enum AbortKind
{
    CtrlC,
    DispatchFault,
}

internal static class AbortKindExtensions
{
    /// <summary>
    /// Stable wire-form projection used in <c>summary.json</c>'s
    /// <c>abortReason</c> field and in <c>distributed_aborted</c> event payloads.
    /// Existing consumers see <c>"ctrl_c"</c> today; the projection preserves
    /// that string verbatim. <c>"dispatch_fault"</c> is additive — no
    /// breaking change.
    /// </summary>
    public static string ToReasonString(this AbortKind kind) =>
        kind switch
        {
            AbortKind.CtrlC => "ctrl_c",
            AbortKind.DispatchFault => "dispatch_fault",
            _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unhandled AbortKind"),
        };
}
