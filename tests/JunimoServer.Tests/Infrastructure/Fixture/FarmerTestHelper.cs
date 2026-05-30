using JunimoServer.Tests.Helpers;
using JunimoServer.Tests.Schema.Events;
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
        CancellationToken ct = default)
    {
        if (breakSession)
            await _testBase.PersistentSession.BreakSessionAsync();
        else
            await _testBase.Connect.EnsureDisconnectedAsync();

        var name = farmerName ?? GenerateName(namePrefix);

        var result = skipAutoLogin
            ? await _testBase.Connect.JoinWithoutAuthAsync(name, favoriteThing, preferExistingFarmer, ct)
            : await _testBase.Connect.JoinWithRetryAsync(name, favoriteThing, preferExistingFarmer, ct);

        if (assertAuthenticated)
            _testBase.Connect.AssertAuthenticated(result);
        else
            _testBase.Connect.AssertJoinSuccess(result);

        // Track only after join has succeeded — AssertJoinSuccess guarantees UID is set.
        TrackFarmer(name, result.UniqueMultiplayerId);

        return new ClientConnection(name, result);
    }

    /// <summary>
    /// Reconnects an existing farmer (no name generation, no tracking).
    /// Use after a disconnect when testing reconnect behavior.
    /// </summary>
    public async Task<ClientConnection> ReconnectAsync(
        string farmerName,
        bool skipAutoLogin = false,
        bool assertAuthenticated = false,
        CancellationToken ct = default)
    {
        var result = skipAutoLogin
            ? await _testBase.Connect.JoinWithoutAuthAsync(farmerName, ct: ct)
            : await _testBase.Connect.JoinWithRetryAsync(farmerName, ct: ct);

        if (assertAuthenticated)
            _testBase.Connect.AssertAuthenticated(result);
        else
            _testBase.Connect.AssertJoinSuccess(result);

        return new ClientConnection(farmerName, result);
    }

    /// <summary>
    /// Disconnects the current client and waits for the server to remove the player
    /// from its active player list, freeing the farmhand slot for reconnection.
    /// </summary>
    public async Task DisconnectAndWaitForSlotAsync(
        long farmerUid, string? farmerNameForLog = null, CancellationToken ct = default)
    {
        await _testBase.DisconnectAsyncInternal();
        var removed = await _testBase.ServerApi.WaitForPlayerRemovedByIdAsync(farmerUid, ct: ct);
        Assert.True(removed,
            $"Server should have removed uid={farmerUid} ({farmerNameForLog ?? "?"}) from active players after disconnect");
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
        string farmerName, CancellationToken ct = default)
    {
        var customized = await _testBase.ServerApi.WaitForFarmhandByNameAsync(
            farmerName, requireCustomized: true, ct: ct);
        Assert.True(customized,
            $"Farmhand '{farmerName}' should appear customized in /farmhands before disconnect");

        await _testBase.DisconnectAsyncInternal();
        var persisted = await _testBase.ServerApi.WaitForFarmhandByNameAsync(
            farmerName, requireCustomized: true, ct: ct);
        Assert.True(persisted,
            $"Farmhand '{farmerName}' should appear in /farmhands after disconnect");
    }
}
