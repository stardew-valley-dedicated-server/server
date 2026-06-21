using JunimoServer.Tests.Helpers;
using Xunit;

namespace JunimoServer.Tests.Infrastructure.Fixture;

/// <summary>
/// A farmer tracked for cleanup. Name is retained for logs and purpose-driven
/// name-sync assertions; Uid is the canonical identifier used by the server for
/// remove / delete operations.
/// </summary>
internal readonly record struct TrackedFarmer(string Name, long Uid);

internal sealed class FarmerTestHelper
{
    private readonly TestBase _testBase;
    private readonly string _displayName;
    private static int _farmerCounter;

    /// <summary>
    /// Farmers created during this test, tracked for cleanup.
    /// </summary>
    internal List<TrackedFarmer> CreatedFarmers { get; } = new();

    public FarmerTestHelper(TestBase testBase, string displayName)
    {
        _testBase = testBase;
        _displayName = displayName;
    }

    public void TrackFarmer(string name, long uid) =>
        CreatedFarmers.Add(new TrackedFarmer(name, uid));

    public string GenerateName(string prefix = "Test") =>
        $"{prefix}{Interlocked.Increment(ref _farmerCounter)}";

    /// <summary>
    /// Result of connecting a farmer to the server.
    /// </summary>
    public record ClientConnection(string FarmerName, JoinWorldResult JoinResult);

    /// <summary>
    /// Ensures disconnected state, joins the server as a new farmer, and returns a
    /// <see cref="ClientConnection"/>. The farmer is registered with TrackFarmer for
    /// automatic cleanup on dispose.
    /// </summary>
    public async Task<ClientConnection> ConnectNewAsync(
        string namePrefix = "Farmer",
        string? farmerName = null,
        string favoriteThing = "Testing",
        bool preferExistingFarmer = true,
        bool skipAutoLogin = false,
        bool assertAuthenticated = false,
        bool breakSession = false,
        CancellationToken ct = default
    )
    {
        if (breakSession)
        {
            await _testBase.PersistentSession.BreakSessionAsync();
        }
        else
        {
            await _testBase.Connect.EnsureDisconnectedAsync();
        }

        var name = farmerName ?? GenerateName(namePrefix);

        var result = skipAutoLogin
            ? await _testBase.Connect.JoinWithoutAuthAsync(
                name,
                favoriteThing,
                preferExistingFarmer,
                ct
            )
            : await _testBase.Connect.JoinWithRetryAsync(
                name,
                favoriteThing,
                preferExistingFarmer,
                ct
            );

        if (assertAuthenticated)
        {
            _testBase.Connect.AssertAuthenticated(result);
        }
        else
        {
            _testBase.Connect.AssertJoinSuccess(result);
        }

        // Track only after join has succeeded — AssertJoinSuccess guarantees UID is set.
        TrackFarmer(name, result.UniqueMultiplayerId);

        return new ClientConnection(name, result);
    }

    /// <summary>
    /// Connects a second concurrent farmer over its own client lease and joins it via LAN.
    /// The primary farmer keeps using the shared connection helpers; this is the one missing
    /// primitive for multi-player E2E tests. LAN-only is sufficient — none of the planned
    /// consumers need a client-stamped userID (which requires Steam). The farmer is tracked
    /// for cleanup via <see cref="TrackFarmer"/>.
    ///
    /// Scope the returned <see cref="SecondFarmer"/> with <c>await using</c> so it
    /// disconnects before the test class's <c>/newgame</c> reset. No
    /// <c>[TestServer(Clients = 2)]</c> is needed: the cabin pool replenishes on join
    /// (EnsureAtLeastXCabins runs in the patched sendAvailableFarmhands path), so the second
    /// farmer always finds a slot, and a runtime lease avoids splitting the server pool.
    /// </summary>
    public async Task<SecondFarmer> ConnectSecondFarmerAsync(
        string namePrefix = "FarmerB",
        CancellationToken ct = default
    )
    {
        var lease = _testBase.LeaseInternal;
        if (lease == null)
        {
            throw new InvalidOperationException(
                "Server not acquired; cannot connect a second farmer."
            );
        }

        var name = GenerateName(namePrefix);
        var clientLease = await _testBase.LeaseClientForHelperAsync(ct);

        // Dispose the lease if the join (or its assert) throws — otherwise it leaks client
        // capacity and leaves a connected client around during cleanup. On success, the
        // returned SecondFarmer owns disposal.
        try
        {
            var conn = new ConnectionHelper(clientLease.Client, serverApi: _testBase.ServerApi);
            var join = await conn.JoinWorldViaLanAsync(
                lease.ServerLanAddress,
                lease.ServerLanPort,
                name,
                cancellationToken: ct
            );
            Assert.True(
                join.Success,
                $"Second farmer '{name}' failed to join via LAN: {join.Error}"
            );

            TrackFarmer(name, join.UniqueMultiplayerId);
            return new SecondFarmer(clientLease, join.UniqueMultiplayerId, name);
        }
        catch
        {
            await clientLease.DisposeAsync();
            throw;
        }
    }

    /// <summary>
    /// Reconnects an existing farmer (no name generation, no tracking).
    /// Use after a disconnect when testing reconnect behavior.
    /// </summary>
    public async Task<ClientConnection> ReconnectAsync(
        string farmerName,
        bool skipAutoLogin = false,
        bool assertAuthenticated = false,
        CancellationToken ct = default
    )
    {
        var result = skipAutoLogin
            ? await _testBase.Connect.JoinWithoutAuthAsync(farmerName, ct: ct)
            : await _testBase.Connect.JoinWithRetryAsync(farmerName, ct: ct);

        if (assertAuthenticated)
        {
            _testBase.Connect.AssertAuthenticated(result);
        }
        else
        {
            _testBase.Connect.AssertJoinSuccess(result);
        }

        return new ClientConnection(farmerName, result);
    }

    /// <summary>
    /// Disconnects the current client and waits for the server to remove the player
    /// from its active player list, freeing the farmhand slot for reconnection.
    /// </summary>
    public async Task DisconnectAndWaitForSlotAsync(
        long farmerUid,
        string? farmerNameForLog = null,
        CancellationToken ct = default
    )
    {
        await _testBase.DisconnectAsyncInternal();
        var removed = await _testBase.ServerApi.WaitForPlayerRemovedByIdAsync(farmerUid, ct: ct);
        Assert.True(
            removed,
            $"Server should have removed uid={farmerUid} ({farmerNameForLog ?? "?"}) from active players after disconnect"
        );
    }

    /// <summary>
    /// Disconnects the current client and waits for the server to persist the farmhand data.
    /// Asserts that the farmhand appears in /farmhands with IsCustomized=true.
    ///
    /// First waits for the server to observe the customized farmer *before* disconnecting:
    /// saveFarmhand() clones live farmer state into farmhandData on disconnect, so if we
    /// disconnect before the character XML has synced back, the server persists an
    /// uncustomized snapshot. The UID-based join gate returns as soon as the peer is
    /// added (seconds before character XML round-trip), so callers that need the
    /// customization visible server-side must wait for it explicitly here.
    /// </summary>
    public async Task DisconnectAndWaitForPersistenceAsync(
        string farmerName,
        CancellationToken ct = default
    )
    {
        var customized = await _testBase.ServerApi.WaitForFarmhandByNameAsync(
            farmerName,
            requireCustomized: true,
            ct: ct
        );
        Assert.True(
            customized,
            $"Farmhand '{farmerName}' should appear customized in /farmhands before disconnect"
        );

        await _testBase.DisconnectAsyncInternal();
        var persisted = await _testBase.ServerApi.WaitForFarmhandByNameAsync(
            farmerName,
            requireCustomized: true,
            ct: ct
        );
        Assert.True(
            persisted,
            $"Farmhand '{farmerName}' should appear in /farmhands after disconnect"
        );
    }
}
