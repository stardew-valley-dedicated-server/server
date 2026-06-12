namespace JunimoServer.Tests.Helpers;

/// <summary>
/// Ambient per-async-scope correlation identifier used to group a logical
/// operation (e.g. <c>JoinWorld(farmerName=NoPwd20)</c>) across the many
/// inner HTTP calls it issues. <see cref="Clients.TracingHandler"/> reads
/// <see cref="Current"/> and — if set — reuses it as the <c>X-Request-Id</c>
/// header for every outbound request inside that scope, so all of those
/// calls share one <c>requestId</c> in <c>infrastructure.jsonl</c>.
///
/// If <see cref="Current"/> is null the handler mints a fresh per-request
/// id. Scopes nest correctly; disposing restores the outer id.
///
/// <para>
/// Deliberately <see cref="AsyncLocal{T}"/> rather than attached to
/// xUnit's <c>TestContext.Current</c> so non-test code paths (fixtures,
/// pre-warming, health checks) can also scope operations.
/// </para>
/// </summary>
public static class CorrelationContext
{
    private static readonly AsyncLocal<string?> _current = new();

    /// <summary>Current correlation id, or null if no scope is active.</summary>
    public static string? Current => _current.Value;

    /// <summary>
    /// Begins a scope with a caller-supplied id. Used by <see cref="Clients.TracingHandler"/>
    /// to re-enter a scope after receiving an id over the wire.
    /// </summary>
    public static IDisposable BeginWithId(string id)
    {
        var previous = _current.Value;
        _current.Value = id;
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
            _current.Value = _previous;
        }
    }
}
