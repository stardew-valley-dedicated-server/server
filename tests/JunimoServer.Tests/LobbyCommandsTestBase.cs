using JunimoServer.Tests.Helpers;
using JunimoServer.Tests.Infrastructure;
using Xunit;

namespace JunimoServer.Tests;

/// <summary>
/// Shared base class for lobby command test classes.
/// Provides admin session management, layout name generation, and cleanup.
///
/// Sub-classes must declare their own [TestServer(Password = "test-password-123", ...)]
/// attribute -- abstract classes have no [TestServer] and are filtered by TestCollectionOrderer.
/// </summary>
public abstract class LobbyCommandsTestBase : TestBase
{
    protected readonly List<string> _testLayouts = new();
    private int _nameCounter;

    protected LobbyCommandsTestBase() { }

    public override async ValueTask DisposeAsync()
    {
        // Clean up any test layouts we created (while still connected)
        if (_testLayouts.Count > 0)
        {
            try
            {
                var state = await GameClient.GetState();
                if (state?.IsConnected == true)
                {
                    Log($"Cleaning up {_testLayouts.Count} test layout(s)...");
                    string[] deleteKeywords = { "Deleted", "Cannot delete", "not found" };
                    foreach (var layoutName in _testLayouts)
                    {
                        try
                        {
                            await GameClient.Chat.SendAndWaitForResponseAsync(
                                $"!lobby delete {layoutName}",
                                deleteKeywords
                            );
                        }
                        catch (Exception ex)
                        {
                            Log($"Layout cleanup failed for '{layoutName}': {ex.Message}");
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Log($"Layout cleanup skipped: {ex.Message}");
            }
        }

        await base.DisposeAsync();
    }

    /// <summary>
    /// Ensures an admin session is active, reusing the persistent session if possible.
    /// Grants admin role idempotently on each call.
    /// </summary>
    protected async Task EnsureAdminSessionAsync()
    {
        await EnsureConnectedAsync("LobbyAdmin");

        var ct = TestCt;
        var uid =
            PersistentSession.ConnectedFarmerUid
            ?? throw new InvalidOperationException(
                $"EnsureAdminSessionAsync requires a joined session (ConnectedFarmerUid=null for '{PersistentSession.ConnectedFarmerName}')"
            );

        // Visibility is guaranteed by the ConnectionHelper gate (UID-based) before
        // EnsureConnectedAsync returns. Grant admin by UID so this call is stable
        // even when name XML sync is still in flight.
        var adminGranted = await PollingHelper.WaitUntilAsync(
            WaitName.Polling_LobbyCommands_AdminGranted,
            async () =>
            {
                var result = await ServerApi.GrantAdminById(uid, ct);
                return result?.Success == true;
            },
            TimeSpan.FromSeconds(10),
            cancellationToken: ct
        );
        Assert.True(
            adminGranted,
            $"Failed to grant admin to uid={uid} ('{PersistentSession.ConnectedFarmerName}') within timeout"
        );
    }

    /// <summary>
    /// Sets up a player WITHOUT admin role.
    /// This temporarily breaks the shared admin session.
    /// </summary>
    protected async Task SetupAsNonAdmin()
    {
        await Farmers.ConnectNewAsync(breakSession: true, assertAuthenticated: true, ct: TestCt);

        var welcome = await GameClient.Chat.WaitForMessageContainingAsync(
            "Welcome",
            TestTimings.ChatCommandTimeout
        );
        Assert.True(
            welcome?.Messages?.Count > 0,
            "Client must receive 'Welcome' message after auth. Client may have disconnected during post-auth warp."
        );
    }

    /// <summary>
    /// Generates a unique layout name for test isolation.
    /// Uses the session farmer name as a discriminator to prevent collisions
    /// when multiple lobby command classes run in parallel on the same server.
    /// Must be called AFTER EnsureAdminSessionAsync() (which sets the persistent session's connected farmer).
    /// </summary>
    protected string GenerateLayoutName(string prefix = "test")
    {
        var farmer = PersistentSession.ConnectedFarmerName ?? "anon";
        var name = $"{prefix}-{farmer}-{_nameCounter++}";
        _testLayouts.Add(name);
        return name;
    }

    /// <summary>
    /// Cancels any active editing session.
    /// </summary>
    protected async Task CancelEditingIfActive()
    {
        string[] cancelKeywords = { "Cancelled", "not editing" };
        await GameClient.Chat.SendAndWaitForResponseAsync("!lobby cancel", cancelKeywords);
    }
}
