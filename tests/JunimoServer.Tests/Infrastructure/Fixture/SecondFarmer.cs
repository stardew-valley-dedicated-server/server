using JunimoServer.Tests.Clients;

namespace JunimoServer.Tests.Infrastructure.Fixture;

/// <summary>
/// A second concurrently-connected farmer over its own client lease, for multi-player
/// E2E tests. The primary farmer uses the shared connection helpers (bound to the single
/// primary client); this wrapper leases an additional client and joins it via LAN.
///
/// <para>
/// <b>Disposal order.</b> Scope this with <c>await using</c> in the test body so it
/// disconnects and disposes its lease <em>before</em> the test class's
/// <c>DisposeAsync</c> runs its <c>/newgame</c> reset — <c>/newgame</c> and <c>/reload</c>
/// return 409 while any client is connected.
/// </para>
///
/// <para>
/// <b>Idempotent.</b> Tests often disconnect the second farmer mid-test (an idle connected
/// farmer blocks the day-end ready check, so it must leave before <c>SleepToSaveAsync</c>),
/// then <c>await using</c> disposes again at scope exit. <see cref="DisconnectAsync"/> and
/// <see cref="DisposeAsync"/> are both safe to call more than once.
/// </para>
/// </summary>
internal sealed class SecondFarmer : IAsyncDisposable
{
    private readonly ClientLease _lease;
    private bool _disconnected;
    private bool _disposed;

    /// <summary>The leased game client for the second farmer.</summary>
    public GameTestClient Client => _lease.Client;

    /// <summary>The underlying client lease (for warps, actions, etc.).</summary>
    public ClientLease Lease => _lease;

    /// <summary>Server-assigned UniqueMultiplayerID of the second farmer.</summary>
    public long Uid { get; }

    /// <summary>The generated farmer name.</summary>
    public string FarmerName { get; }

    internal SecondFarmer(ClientLease lease, long uid, string farmerName)
    {
        _lease = lease;
        Uid = uid;
        FarmerName = farmerName;
    }

    /// <summary>
    /// Disconnects the second farmer (navigates to title) so it stops blocking the day-end
    /// ready check, then marks the lease already-disconnected so disposal won't disconnect
    /// again. Idempotent.
    /// </summary>
    public async Task DisconnectAsync()
    {
        if (_disconnected)
        {
            return;
        }

        _disconnected = true;
        await _lease.Container.DisconnectAsync();
        _lease.AlreadyDisconnected = true;
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        // If DisconnectAsync wasn't called, the lease's own DisposeAsync disconnects;
        // if it was, AlreadyDisconnected makes that a no-op.
        await _lease.DisposeAsync();
    }
}
