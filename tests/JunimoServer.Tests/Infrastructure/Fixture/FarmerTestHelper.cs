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
        return await JoinSecondFarmerAsync(lease, clientLease, name, ct);
    }

    /// <summary>
    /// Connects BOTH farmers concurrently and returns once both are joined and visible
    /// server-side. The two client containers are leased up front (overlapping their cold
    /// start), then both join sequences run together via <see cref="Task.WhenAll(Task[])"/>.
    ///
    /// <para>
    /// The join gate serializes only each join's pre-approval phase and releases at approval (see
    /// <c>ConnectionHelper.AcquireJoinGate</c>), so each join's creation/world-ready/auth tail overlaps
    /// the other's pre-approval — plus the second client's cold-start and both clients' menu navigation.
    /// So B is present within seconds of A, not only after A's whole sequence completes.
    /// </para>
    ///
    /// <para>
    /// The primary uses the shared connection helpers (held as the test's <c>GameClient</c>);
    /// the secondary is wrapped as a <see cref="SecondFarmer"/> over its own lease — scope it
    /// with <c>await using</c> so it disconnects before the class's <c>/newgame</c> reset.
    /// Both farmers are tracked for cleanup via <see cref="TrackFarmer"/>.
    /// </para>
    /// </summary>
    public async Task<(
        ClientConnection Primary,
        SecondFarmer Secondary
    )> ConnectBothConcurrentlyAsync(
        string primaryPrefix = "FarmerA",
        string secondaryPrefix = "FarmerB",
        CancellationToken ct = default
    )
    {
        var lease = _testBase.LeaseInternal;
        if (lease == null)
        {
            throw new InvalidOperationException("Server not acquired; cannot connect farmers.");
        }

        // Lease the secondary client up front, concurrently with ensuring the primary client
        // is ready. The primary join leases its own client lazily, but pre-warming it here lets
        // the slow part (a cold client container start) of both leases overlap rather than the
        // secondary's start waiting on the primary's whole join.
        var primaryName = GenerateName(primaryPrefix);
        var secondaryName = GenerateName(secondaryPrefix);
        await _testBase.GetClientAsyncInternal(ct);
        var secondaryLease = await _testBase.LeaseClientForHelperAsync(ct);

        // Run both joins together. The join gate inside each path serializes the game-thread
        // portion; the client-side work overlaps. Use WhenAll so a failure in either still
        // lets the other settle before we observe it (and so the secondary lease is disposed
        // on its own failure path inside JoinSecondFarmerAsync).
        var primaryTask = _testBase.Connect.JoinWithRetryAsync(primaryName, ct: ct);
        var secondaryTask = JoinSecondFarmerAsync(lease, secondaryLease, secondaryName, ct);

        try
        {
            await Task.WhenAll(primaryTask, secondaryTask);
        }
        catch
        {
            // If only one task faulted, the other may have produced a resource that needs
            // releasing. JoinSecondFarmerAsync already disposes its own lease on failure; a
            // successful secondary whose primary failed is disposed here so it doesn't leak.
            if (secondaryTask.IsCompletedSuccessfully)
            {
                await secondaryTask.Result.DisposeAsync();
            }
            throw;
        }

        var primaryResult = primaryTask.Result;
        _testBase.Connect.AssertJoinSuccess(primaryResult);
        TrackFarmer(primaryName, primaryResult.UniqueMultiplayerId);

        return (new ClientConnection(primaryName, primaryResult), secondaryTask.Result);
    }

    /// <summary>
    /// Joins a second farmer over an already-leased client and wraps it as a
    /// <see cref="SecondFarmer"/>, gated (via its own <see cref="ConnectionHelper"/>) against the primary
    /// join's pre-approval phase. Disposes the supplied lease if the join throws; on success the returned
    /// <see cref="SecondFarmer"/> owns disposal.
    /// </summary>
    private async Task<SecondFarmer> JoinSecondFarmerAsync(
        ResourceLease lease,
        ClientLease clientLease,
        string name,
        CancellationToken ct
    )
    {
        try
        {
            // Carry the server password so the join core's auto-login (!login) runs for the
            // second farmer too — a password server otherwise leaves it stuck in the lobby.
            var conn = new ConnectionHelper(
                clientLease.Client,
                new ConnectionOptions { ServerPassword = lease.Password },
                serverApi: _testBase.ServerApi
            )
            {
                // Same gate as the primary path, so two concurrent joins serialize their pre-approval
                // phase against each other (see ConnectionHelper.AcquireJoinGate).
                AcquireJoinGate = lease.Managed.AcquireJoinGateAsync,
                ReleaseJoinGate = lease.Managed.ReleaseJoinGate,
            };
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
        // Cheap presence re-confirmation only — not a customization gate. The pre-disconnect
        // wait + disconnect-time saveFarmhand() clone already persisted the customized snapshot,
        // and a disconnected farmhand stays in farmhandData with isCustomized=true (there's no
        // post-disconnect window where customization reverts). So requireCustomized:false returns
        // near-instantly instead of paying the slow customization filter again.
        var persisted = await _testBase.ServerApi.WaitForFarmhandByNameAsync(
            farmerName,
            requireCustomized: false,
            ct: ct
        );
        Assert.True(
            persisted,
            $"Farmhand '{farmerName}' should appear in /farmhands after disconnect"
        );
    }
}
