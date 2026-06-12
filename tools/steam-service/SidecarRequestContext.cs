namespace SteamService;

/// <summary>
/// Per-request correlation identifier for the steam-auth sidecar, populated
/// from the inbound <c>X-Request-Id</c> header by the Kestrel middleware
/// registered in <c>Program.cs</c>. Flows to structured events emitted
/// inside the request handler so the test harness can stitch sidecar logs
/// into the unified timeline.
///
/// <para>
/// Uses <see cref="AsyncLocal{T}"/> so the id survives awaits and
/// continuation-thread hops inside request handlers. Events emitted from
/// SteamKit callback threads (where no HTTP context is active) legitimately
/// carry <c>requestId = null</c> — see docs/developers/events-schema.md.
/// </para>
/// </summary>
public static class SidecarRequestContext
{
    private static readonly AsyncLocal<string?> _requestId = new();

    /// <summary>The inbound <c>X-Request-Id</c>, if any.</summary>
    public static string? Current => _requestId.Value;

    /// <summary>
    /// Binds the id for the duration of a request handler. The returned
    /// handle restores the previous value on <see cref="IDisposable.Dispose"/>.
    /// </summary>
    public static IDisposable Begin(string? requestId)
    {
        var previous = _requestId.Value;
        _requestId.Value = requestId;
        return new Scope(previous);
    }

    private sealed class Scope : IDisposable
    {
        private readonly string? _previous;
        private bool _disposed;

        public Scope(string? previous)
        {
            _previous = previous;
        }

        public void Dispose()
        {
            if (_disposed)
                return;
            _disposed = true;
            _requestId.Value = _previous;
        }
    }
}
