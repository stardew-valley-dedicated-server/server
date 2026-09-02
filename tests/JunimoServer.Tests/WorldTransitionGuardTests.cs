using System.Net;
using JunimoServer.Tests.Infrastructure;
using Xunit;

namespace JunimoServer.Tests;

/// <summary>
/// The world-disruption lease and failed-creation recovery on the two HTTP world transitions.
/// Each test replaces the world, so the class is exclusive and leaves its server on a fresh
/// world (a valid pooled state). No clients: the 409s under test are the lease's, not the
/// connected-client count's.
/// </summary>
[TestServer(Isolation = IsolationMode.SharedClass, Exclusive = true, Clients = 0)]
public class WorldTransitionGuardTests : TestBase
{
    /// <summary>
    /// Long enough for the first request's game-thread action (lease check + acquire) to have
    /// drained — one tick at SERVER_TPS=5 is 200 ms — and far shorter than a world creation.
    /// </summary>
    private static readonly TimeSpan LeaseTakenSettle = TimeSpan.FromSeconds(2);

    private static readonly TimeSpan WorldLoadTimeout = TimeSpan.FromSeconds(120);

    [Fact]
    public async Task NewGame_WhileNewGameInFlight_Returns409WithLeaseHolder()
    {
        LogSection("Second /newgame during an in-flight /newgame");

        var first = ServerApi.TryCreateNewGameAsync(TestCt);
        await Task.Delay(LeaseTakenSettle, TestCt);

        var second = await ServerApi.TryCreateNewGameAsync(TestCt);
        Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);
        Assert.Equal("new game in progress", second.Body?.Error);
        Log($"Second /newgame rejected: {second.Body?.Error}");

        var firstResult = await first;
        Assert.Equal(HttpStatusCode.OK, firstResult.StatusCode);
        Assert.True(firstResult.Body?.Success, firstResult.Body?.Error);
        await WaitForWorldAsync();
    }

    [Fact]
    public async Task Reload_WhileNewGameInFlight_Returns409WithLeaseHolder()
    {
        LogSection("/reload during an in-flight /newgame");

        var first = ServerApi.TryCreateNewGameAsync(TestCt);
        await Task.Delay(LeaseTakenSettle, TestCt);

        var reload = await ServerApi.TryReloadAsync(TestCt);
        Assert.Equal(HttpStatusCode.Conflict, reload.StatusCode);
        Assert.Equal("new game in progress", reload.Body?.Error);
        Log($"/reload rejected: {reload.Body?.Error}");

        var firstResult = await first;
        Assert.Equal(HttpStatusCode.OK, firstResult.StatusCode);
        Assert.True(firstResult.Body?.Success, firstResult.Body?.Error);
        await WaitForWorldAsync();
    }

    [Fact]
    public async Task NewGame_WhenCreationThrows_ServerRecoversToLoadedWorldAndReleasesLease()
    {
        LogSection("Failed creation recovers to the previous world");

        var before = await ServerApi.GetStatus(TestCt);
        Assert.NotNull(before);
        Assert.True(before.IsOnline, "precondition: a world is loaded");

        var armed = await ServerApi.FailNextNewGame(TestCt);
        Assert.True(armed?.Success, armed?.Error);

        var failed = await ServerApi.TryCreateNewGameAsync(TestCt);
        Assert.Equal(HttpStatusCode.InternalServerError, failed.StatusCode);
        Assert.Contains("test-injected new game failure", failed.Body?.Error);
        Log($"Creation failed as injected: {failed.Body?.Error}");

        // The world was left at ExitToTitle before the creator ran, and the 1 Hz snapshot has
        // had a full automation cadence to publish that, so the offline state is observable
        // here — which is what makes the wait below a real recovery, not a stale read.
        var atTitle = await ServerApi.GetStatus(TestCt);
        Assert.False(atTitle?.IsOnline, "the failed creation leaves the server at title");

        var recovered = await WaitForWorldAsync();
        Assert.Equal(before.FarmName, recovered.FarmName);
        Log($"Recovered to the previous world: {recovered.FarmName}");

        // The lease was released when the faulted creation settled: a new transition is accepted.
        var next = await ServerApi.TryCreateNewGameAsync(TestCt);
        Assert.Equal(HttpStatusCode.OK, next.StatusCode);
        Assert.True(next.Body?.Success, next.Body?.Error);
        await WaitForWorldAsync();
    }

    private async Task<Clients.ServerStatus> WaitForWorldAsync()
    {
        var status = await ServerApi.WaitForServerOnline(
            WorldLoadTimeout,
            pollInterval: TimeSpan.FromSeconds(2),
            cancellationToken: TestCt,
            requireInviteCode: Server.Options.WithSteam
        );
        Assert.NotNull(status);
        return status;
    }
}
