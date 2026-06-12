using JunimoServer.Tests.Clients;

namespace JunimoServer.Tests.Helpers;

/// <summary>
/// Collects ground-truth server state on the failure path so a polling
/// timeout becomes a diagnostic signal rather than a mystery. Emits a
/// structured <c>failure_context</c> event into <c>infrastructure.jsonl</c>
/// and returns a dictionary that callers can attach to result objects
/// (e.g. <see cref="ConnectionHelper"/>'s join result).
///
/// <para>
/// Hard-deadlined: every network call is capped so a broken collector
/// cannot stretch the test's already-bad failure path.
/// </para>
/// </summary>
public static class FailureContext
{
    /// <summary>
    /// Timeout for the <c>/diagnostics/state</c> fetch. Kept short — if the
    /// server can't answer within 2 s during failure analysis, it's stuck and
    /// we log the stuck state rather than blocking longer.
    /// </summary>
    public static readonly TimeSpan DefaultFetchTimeout = TimeSpan.FromSeconds(2);

    /// <summary>
    /// Per-test latest failure context, captured at the call site of DumpAsync
    /// and read by <c>TestFailureReporter</c> when building the
    /// <c>test_enrichment</c> IPC event so the UI can surface server state
    /// next to the failure.
    ///
    /// <para>
    /// <b>Reference-in-AsyncLocal pattern.</b> A naïve
    /// <c>AsyncLocal&lt;IReadOnlyDictionary&gt;</c> fails here: writes to an
    /// <see cref="AsyncLocal{T}"/> made inside a deeper async frame do NOT
    /// propagate up to the calling frame, because each <c>await</c> captures
    /// the caller's ExecutionContext and restores it when the awaited task
    /// completes. <c>DumpAsync</c>'s write happens inside its own async
    /// frame; the outer test code's read after <c>await DumpAsync(...)</c>
    /// would see <c>null</c>. Storing a mutable <see cref="Stash"/>
    /// reference in the AsyncLocal sidesteps the issue — the reference flows
    /// downward via standard EC propagation, and writes to the boxed value
    /// are plain memory writes visible to any frame holding the same reference.
    /// </para>
    /// </summary>
    private static readonly AsyncLocal<Stash?> _stash = new();

    /// <summary>
    /// Latest failure-context dump for the currently-running test, or null if
    /// none was captured. Cleared by <see cref="ClearForTest"/> at test boundaries.
    /// </summary>
    public static IReadOnlyDictionary<string, object?>? LatestForCurrentTest => _stash.Value?.Value;

    /// <summary>
    /// Install a fresh per-test stash. Called at the start of each test by
    /// <c>TestLifecycle</c> so a previous test's dump can't bleed into the
    /// next and so <c>DumpAsync</c>'s inner-frame write has a stable
    /// reference to write through.
    /// </summary>
    public static void ClearForTest() => _stash.Value = new Stash();

    private sealed class Stash
    {
        public IReadOnlyDictionary<string, object?>? Value;
    }

    /// <summary>
    /// Fetches live server state and emits a <c>failure_context</c> event.
    /// Returns an <see cref="IReadOnlyDictionary{TKey,TValue}"/> of diagnostic
    /// fields safe to attach to user-facing error messages.
    ///
    /// All exceptions are caught and turned into <c>diagnosticsError</c>
    /// fields on the emitted event — this must never throw on the failure path.
    /// </summary>
    /// <param name="apiClient">Client to query. May be null; the dump still
    /// emits an event with just the reason and extras.</param>
    /// <param name="reason">Short identifier for the triggering failure
    /// (e.g. <c>"player_visibility_timeout"</c>). Appears on the event and in
    /// the returned dict.</param>
    /// <param name="extras">Optional caller context (uid, farmer name, attempt,
    /// etc.). Merged into the event and the returned dict.</param>
    /// <param name="fetchTimeout">Override <see cref="DefaultFetchTimeout"/>.</param>
    public static async Task<IReadOnlyDictionary<string, object?>> DumpAsync(
        ServerApiClient? apiClient,
        string reason,
        IReadOnlyDictionary<string, object?>? extras = null,
        TimeSpan? fetchTimeout = null
    )
    {
        var result = new Dictionary<string, object?> { ["reason"] = reason };
        if (extras != null)
        {
            foreach (var kv in extras)
                result[kv.Key] = kv.Value;
        }

        DiagnosticsStateResponse? serverState = null;
        string? diagnosticsError = null;

        if (apiClient != null)
        {
            try
            {
                using var cts = new CancellationTokenSource(fetchTimeout ?? DefaultFetchTimeout);
                serverState = await apiClient.GetDiagnosticsState(cts.Token);
            }
            catch (Exception ex)
            {
                diagnosticsError = $"{ex.GetType().Name}: {ex.Message}";
            }
        }

        if (serverState != null)
            result["serverState"] = serverState;
        if (diagnosticsError != null)
            result["diagnosticsError"] = diagnosticsError;

        InfrastructureEventLog.Emit(
            "failure_context",
            new
            {
                reason,
                extras,
                serverState,
                diagnosticsError,
            }
        );

        // Stash for TestFailureReporter's enrichment-event emit. Writes go
        // through the Stash reference installed by ClearForTest at test start
        // (see class doc) so the outer-frame read after this await sees the
        // value. Last-write-wins is fine — multiple dumps within the same
        // test only happen on the failure path, and the UI surfaces just the
        // most recent one. If no test scope is active (Stash == null), the
        // write is a no-op rather than throwing — DumpAsync is called from
        // pre-test prewarm paths too.
        if (_stash.Value is { } stash)
            stash.Value = result;

        return result;
    }
}
