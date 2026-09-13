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
/// carry <c>requestId = null</c> and <c>testId = null</c> — see
/// docs/developers/events-schema.md.
/// </para>
/// </summary>
public static class SidecarRequestContext
{
    private static readonly AsyncLocal<string?> _requestId = new();
    private static readonly AsyncLocal<string?> _testId = new();

    /// <summary>The inbound <c>X-Request-Id</c>, if any.</summary>
    public static string? Current => _requestId.Value;

    /// <summary>The inbound <c>X-Test-Id</c> (the originating test's display name), if any.</summary>
    public static string? TestId => _testId.Value;

    /// <summary>
    /// Binds both ids for the duration of a request handler. Set together so
    /// no scope can carry one and drop the other. The returned handle
    /// restores the previous values on <see cref="IDisposable.Dispose"/>.
    /// </summary>
    public static IDisposable Begin(string? requestId, string? testId)
    {
        var previousRequestId = _requestId.Value;
        var previousTestId = _testId.Value;
        _requestId.Value = requestId;
        _testId.Value = testId;
        return new Scope(previousRequestId, previousTestId);
    }

    private sealed class Scope : IDisposable
    {
        private readonly string? _previousRequestId;
        private readonly string? _previousTestId;
        private bool _disposed;

        public Scope(string? previousRequestId, string? previousTestId)
        {
            _previousRequestId = previousRequestId;
            _previousTestId = previousTestId;
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _requestId.Value = _previousRequestId;
            _testId.Value = _previousTestId;
        }
    }
}
