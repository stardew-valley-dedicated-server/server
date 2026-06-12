using System.Threading;

namespace JunimoServer.Services.Diagnostics;

/// <summary>
/// Per-request correlation identifier extracted from the inbound
/// <c>X-Request-Id</c> header by <see cref="Api.ApiService.HandleRequestAsync"/>.
/// The value flows to mod-side structured events so a single log stream
/// can reconstruct a logical operation across server, client, and
/// sidecar containers.
///
/// <para>
/// Uses <see cref="AsyncLocal{T}"/> — the handler is <c>async Task</c>
/// with many awaits and no <c>ConfigureAwait(false)</c>, so continuations
/// may resume on a different thread-pool thread. <c>AsyncLocal</c> flows
/// the id across those boundaries; <see cref="ThreadLocal{T}"/> would
/// silently lose it.
/// </para>
/// </summary>
public static class ModRequestContext
{
    private static readonly AsyncLocal<string?> _requestId = new();

    /// <summary>The inbound <c>X-Request-Id</c>, if any.</summary>
    public static string? RequestId => _requestId.Value;

    /// <summary>
    /// Binds the id for the duration of a request handler. The returned
    /// handle restores the previous value on <see cref="System.IDisposable.Dispose"/>.
    /// </summary>
    public static System.IDisposable Bind(string? requestId)
    {
        var previousRequestId = _requestId.Value;
        _requestId.Value = requestId;
        return new Scope(previousRequestId);
    }

    private sealed class Scope : System.IDisposable
    {
        private readonly string? _previousRequestId;
        private bool _disposed;

        public Scope(string? previousRequestId)
        {
            _previousRequestId = previousRequestId;
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _requestId.Value = _previousRequestId;
        }
    }
}
