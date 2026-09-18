using System.Threading;

namespace JunimoServer.Services.Diagnostics;

/// <summary>
/// Per-request correlation identifiers extracted from inbound headers by
/// <see cref="Api.ApiService.HandleRequestAsync"/>:
/// <list type="bullet">
///   <item><see cref="RequestId"/> — the <c>X-Request-Id</c> header, a
///   per-HTTP-call join key present only when tracing mints one.</item>
///   <item><see cref="TestId"/> — the <c>X-Test-Id</c> header, the display
///   name of the test that issued the call, attached regardless of tracing
///   level so every server event is attributable to its originating test.</item>
/// </list>
/// Both flow to mod-side structured events (<see cref="ModEventLog"/>) so a
/// single log stream can reconstruct "which test issued this call, and what
/// did the whole operation do" across server, client, and sidecar containers.
///
/// <para>
/// Uses <see cref="AsyncLocal{T}"/> — the handler is <c>async Task</c>
/// with many awaits and no <c>ConfigureAwait(false)</c>, so continuations
/// may resume on a different thread-pool thread. <c>AsyncLocal</c> flows
/// the ids across those boundaries; <see cref="ThreadLocal{T}"/> would
/// silently lose them. It does NOT flow across external pump boundaries
/// (the game-thread queue, the <c>/wait/*</c> await continuations) — those
/// sites capture the values and re-<see cref="Bind"/> them. See
/// <c>.claude/rules/asynclocal-pitfalls.md</c>.
/// </para>
/// </summary>
public static class ModRequestContext
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
    /// at an <c>AsyncLocal</c> pump boundary, rebinds the captured values).
    /// The returned handle restores the previous values on
    /// <see cref="System.IDisposable.Dispose"/>.
    /// </summary>
    public static System.IDisposable Bind(string? requestId, string? testId)
    {
        var previous = _ids.Value;
        _ids.Value = (requestId, testId);
        return new Scope(previous);
    }

    private sealed class Scope : System.IDisposable
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
