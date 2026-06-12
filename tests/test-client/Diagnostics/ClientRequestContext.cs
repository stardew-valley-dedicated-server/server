using System;
using System.Threading;

namespace JunimoTestClient.Diagnostics;

/// <summary>
/// Per-request correlation identifier for the test-client mod, populated
/// from the inbound <c>X-Request-Id</c> header by <c>TestApiServer</c>.
/// Flows to structured events emitted inside the request handler so the
/// test harness can stitch client-side logs into the unified timeline.
///
/// <para>
/// Uses <see cref="AsyncLocal{T}"/> so the id survives awaits and
/// continuation-thread hops inside route handlers. Separate from the
/// server mod's <c>ModRequestContext</c> because the test-client mod is
/// a different assembly and must not take a dependency on it.
/// </para>
/// </summary>
public static class ClientRequestContext
{
    private static readonly AsyncLocal<string?> _requestId = new();

    /// <summary>The inbound <c>X-Request-Id</c>, if any.</summary>
    public static string? RequestId => _requestId.Value;

    /// <summary>
    /// Binds the id for the duration of a request handler. The returned
    /// handle restores the previous value on <see cref="IDisposable.Dispose"/>.
    /// </summary>
    public static IDisposable Bind(string? requestId)
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
            {
                return;
            }

            _disposed = true;
            _requestId.Value = _previous;
        }
    }
}
