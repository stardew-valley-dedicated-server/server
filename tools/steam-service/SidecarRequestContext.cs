namespace SteamService;

/// <summary>
/// Per-request correlation identifiers for the steam-auth sidecar, populated
/// from the inbound <c>X-Request-Id</c> / <c>X-Test-Id</c> headers by the
/// Kestrel middleware registered in <c>Program.cs</c>. Both flow to structured
/// events emitted inside the request handler so the test harness can stitch
/// sidecar logs into the unified timeline and attribute them to the
/// originating test.
///
/// <para>
/// Uses <see cref="AsyncLocal{T}"/> so the ids survive awaits and
/// continuation-thread hops inside request handlers. Events emitted from
/// SteamKit callback threads (where no HTTP context is active) legitimately
/// carry <c>requestId = null</c> and <c>testId = null</c>; see
/// docs/developers/events-schema.md.
/// </para>
/// </summary>
public static class SidecarRequestContext
{
    // One AsyncLocal for both ids: a bind is a single execution-context
    // value-map rebuild, and a boundary restores both or neither.
    private static readonly AsyncLocal<(string? RequestId, string? TestId)> _ids = new();

    /// <summary>The inbound <c>X-Request-Id</c>, if any.</summary>
    public static string? RequestId => _ids.Value.RequestId;

    /// <summary>The inbound <c>X-Test-Id</c> (the originating test's display name), if any.</summary>
    public static string? TestId => _ids.Value.TestId;

    /// <summary>
    /// Binds both ids for the duration of a request handler. The returned handle
    /// restores the previous values on <see cref="IDisposable.Dispose"/>.
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
