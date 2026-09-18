namespace JunimoTestClient.Diagnostics;

/// <summary>
/// Per-request correlation identifiers for the test-client mod, populated from
/// inbound headers by <c>TestApiServer</c>:
/// <list type="bullet">
///   <item><see cref="RequestId"/> — the <c>X-Request-Id</c> header, present
///   only when the caller's tracing minted one.</item>
///   <item><see cref="TestId"/> — the <c>X-Test-Id</c> header (the originating
///   test's display name), attached regardless of tracing level so every
///   client-mod event during a request is attributable to its test.</item>
/// </list>
/// Both flow to structured events emitted inside the request handler so the
/// test harness can stitch client-side logs into the unified timeline.
///
/// <para>
/// Uses <see cref="AsyncLocal{T}"/> so the ids survive awaits and
/// continuation-thread hops inside route handlers. Does NOT flow across the
/// game-loop pump boundary — <c>ModEntry.ExecuteOnGameThread</c> captures and
/// re-binds them. Separate from the server mod's <c>ModRequestContext</c>
/// because the test-client mod is a different assembly and must not take a
/// dependency on it.
/// </para>
/// </summary>
public static class ClientRequestContext
{
    // Both ids share one AsyncLocal so a bind is a single execution-context
    // value-map rebuild (not one per id) and the pair can never be split — a
    // boundary restores both or neither.
    private static readonly AsyncLocal<(string? RequestId, string? TestId)> _ids = new();

    /// <summary>The inbound <c>X-Request-Id</c>, if any.</summary>
    public static string? RequestId => _ids.Value.RequestId;

    /// <summary>The inbound <c>X-Test-Id</c> (the originating test's display name), if any.</summary>
    public static string? TestId => _ids.Value.TestId;

    /// <summary>
    /// Binds both correlation ids for the duration of a request handler (or,
    /// at the game-loop pump boundary, rebinds the captured values). The
    /// returned handle restores the previous values on
    /// <see cref="IDisposable.Dispose"/>.
    /// </summary>
    public static IDisposable Bind(string? requestId, string? testId)
    {
        var previous = _ids.Value;
        _ids.Value = (requestId, testId);
        return new Scope(previous);
    }

    private sealed class Scope : IDisposable
    {
        private readonly (string? RequestId, string? TestId) _previous;
        private bool _disposed;

        public Scope((string? RequestId, string? TestId) previous)
        {
            _previous = previous;
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _ids.Value = _previous;
        }
    }
}
