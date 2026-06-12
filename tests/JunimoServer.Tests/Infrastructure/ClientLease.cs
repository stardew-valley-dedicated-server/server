using JunimoServer.Tests.Clients;
using JunimoServer.Tests.Containers;
using JunimoServer.Tests.Helpers;

namespace JunimoServer.Tests.Infrastructure;

/// <summary>
/// RAII wrapper around a pooled GameClientContainer.
/// Exposes the container and its API client. On dispose, disconnects and returns to pool.
/// </summary>
public sealed class ClientLease : IAsyncDisposable
{
    private readonly ClientPool _pool;
    private readonly string _instanceId;
    private readonly string _serverKey;
    private bool _disposed;

    /// <summary>The underlying game client container.</summary>
    public GameClientContainer Container { get; }

    /// <summary>The game test client API for controlling the game.</summary>
    public GameTestClient Client => Container.Client;

    /// <summary>The instance ID for UI event tracking.</summary>
    public string InstanceId => _instanceId;

    /// <summary>
    /// When true, the caller has already disconnected (navigated to title).
    /// DisposeAsync will skip the redundant DisconnectAsync call.
    /// </summary>
    public bool AlreadyDisconnected { get; set; }

    internal ClientLease(ClientPool pool, GameClientContainer container, string serverKey)
    {
        _pool = pool;
        Container = container;
        _instanceId = $"client-{container.ClientIndex}";
        _serverKey = serverKey;
    }

    internal string ServerKey => _serverKey;

    /// <summary>Emit instance leased event (called by ResourceLease after construction).</summary>
    internal void EmitLeased(string testName, string? serverInstanceId = null)
    {
        SetupEventBus.EmitInstanceLeased(_instanceId, testName, serverInstanceId);
        InfrastructureEventLog.Emit(
            "client_acquired",
            new
            {
                clientIndex = Container.ClientIndex,
                instanceId = _instanceId,
                serverInstanceId,
                serverKey = _serverKey,
                steamAccountIndex = Container.SteamAccountIndex,
            }
        );
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        SetupEventBus.EmitInstanceReturned(_instanceId);

        // Disconnect client before returning to pool (skip if caller already disconnected)
        if (!AlreadyDisconnected)
        {
            try
            {
                await Container.DisconnectAsync();
            }
            catch (Exception ex)
            {
                // Disconnect failed; client is likely dead. Don't return it to the pool
                // for reuse, but keep it in _allClients so ClientPool.DisposeAsync() can
                // still retrieve the full recording from it.
                TestLog.Client(
                    $"client-{Container.ClientIndex} disconnect failed, marking dead: {ex.Message}"
                );
                _pool.MarkClientDead(Container);
                return;
            }
        }

        _pool.ReturnClient(Container, _serverKey);
    }
}
