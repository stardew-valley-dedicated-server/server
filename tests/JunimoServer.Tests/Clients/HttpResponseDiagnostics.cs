namespace JunimoServer.Tests.Clients;

/// <summary>
/// Ambient slot that <see cref="TracingHandler"/> writes after each successful
/// response so a polling helper can read the predicate-transition time of the
/// winning HTTP call (the one that flipped the condition true) without
/// threading the value back up through every <c>WaitFor*Async</c> signature.
///
/// <para>
/// The value is "ms ago in server-container time at the moment the server
/// sent the response": <c>X-Predicate-Changed-At-Ms-Ago</c>. It identifies
/// when the predicate became satisfiable on the server's clock — finer than
/// the snapshot's capture time, which is gated to the snapshot publish
/// cadence (1Hz). Used by
/// <see cref="JunimoServer.Tests.Helpers.PollingHelper.EmitWaitMatched"/> to
/// stamp the <c>wait_matched</c> envelope's <c>ts</c>/<c>runMs</c> at
/// producer-time.
/// </para>
///
/// <para>
/// <b>Reference-in-AsyncLocal pattern.</b> A naïve
/// <c>AsyncLocal&lt;long?&gt;</c> fails here: writes to an
/// <see cref="AsyncLocal{T}"/> made inside a deeper async frame do NOT
/// propagate up to the calling frame, because each <c>await</c> captures
/// the caller's ExecutionContext and restores it when the awaited task
/// completes. Verified empirically — a write inside an awaited method is
/// invisible to the caller after the await. (This is a different failure
/// mode from the queue-pump break described in
/// <c>.claude/rules/asynclocal-pitfalls.md</c>; the rule covers external
/// pumps flowing values forward across boundaries — here the missing
/// direction is the return path.)
/// </para>
///
/// <para>
/// To make a deep write visible at a shallow read, this class stores a
/// <i>reference</i> (the <see cref="Slot"/>) in the AsyncLocal. The
/// reference flows downward via standard EC propagation — TracingHandler
/// sees the same object the polling helper created. Writes are then plain
/// memory writes through that shared reference, not modifications of the
/// AsyncLocal slot itself, so they're visible to the polling helper
/// regardless of which async frame did the write.
/// </para>
///
/// <para>
/// Use <see cref="BeginScope"/> to install a fresh slot at the start of a
/// polling helper's lifetime; outer code's slot (if any) is restored on
/// dispose. When no scope is active, writes are a no-op — there's no
/// shared object to write through, so no leakage to unrelated contexts.
/// </para>
/// </summary>
internal static class HttpResponseDiagnostics
{
    private static readonly AsyncLocal<Slot?> _slot = new();

    /// <summary>
    /// Predicate-transition age (ms) reported by the server on the most
    /// recent HTTP response observed within the current <see cref="BeginScope"/>:
    /// the <c>X-Predicate-Changed-At-Ms-Ago</c> header value. <c>null</c>
    /// if no scope is active, no response has been observed yet in the
    /// scope, or the response did not carry the header (predicate-shape
    /// doesn't map to a single change-time). The setter is a no-op
    /// outside a scope.
    /// </summary>
    public static long? LastPredicateChangedMsAgo
    {
        get => _slot.Value?.Value;
        set { if (_slot.Value is { } s) s.Value = value; }
    }

    /// <summary>
    /// Installs a fresh slot in the AsyncLocal; restores the prior slot on
    /// dispose. Use at the start of a polling helper so the emitted
    /// <c>wait_matched</c> producer-time reflects only HTTP calls made
    /// during the helper's run, not stale values from outer code, and so
    /// writes don't leak to unrelated outer scopes.
    /// </summary>
    public static IDisposable BeginScope()
    {
        var prior = _slot.Value;
        _slot.Value = new Slot();
        return new ScopeRestorer(prior);
    }

    private sealed class Slot
    {
        public long? Value;
    }

    private sealed class ScopeRestorer : IDisposable
    {
        private readonly Slot? _prior;
        private bool _disposed;
        public ScopeRestorer(Slot? prior) { _prior = prior; }
        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _slot.Value = _prior;
        }
    }
}
