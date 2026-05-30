namespace JunimoServer.Tests.Containers;

/// <summary>
/// Provides a game client for E2E tests. Supports reference counting
/// for shared resources (containerized client) and per-fixture instances.
/// </summary>
public interface IGameClientProvider
{
    /// <summary>Base URL for the game client's test API.</summary>
    string BaseUrl { get; }

    /// <summary>
    /// Acquires a reference, starting the client if needed.
    /// Returns when the client is ready to accept requests.
    /// </summary>
    Task AcquireAsync(Action<string>? logCallback = null, CancellationToken ct = default);

    /// <summary>
    /// Releases a reference. When the last reference is released, the client stops.
    /// </summary>
    Task ReleaseAsync();
}
