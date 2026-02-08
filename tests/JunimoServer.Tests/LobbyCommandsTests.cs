using JunimoServer.Tests.Clients;
using JunimoServer.Tests.Fixtures;
using JunimoServer.Tests.Helpers;
using Xunit;
using Xunit.Abstractions;

namespace JunimoServer.Tests;

/// <summary>
/// Integration tests for lobby admin commands (!lobby).
/// Tests layout management, permissions, and editing workflows.
///
/// These tests require a player with admin role to execute lobby commands.
/// Admin is granted via the /roles/admin API endpoint.
///
/// Performance: Uses session persistence to avoid reconnecting for every test.
/// A shared admin session is established once and reused across tests.
/// Only the NonAdmin test temporarily disconnects and reconnects.
/// </summary>
[Collection("Integration")]
public class LobbyCommandsTests : IntegrationTestBase
{
    private readonly List<string> _testLayouts = new();

    // Shared admin session state (persists across test instances in the same collection run)
    private static string? _sharedAdminName;
    private static bool _sessionEstablished;

    public LobbyCommandsTests(IntegrationTestFixture fixture, ITestOutputHelper output)
        : base(fixture, output)
    {
    }

    public override async Task DisposeAsync()
    {
        // Clean up any test layouts we created (while still connected)
        if (_testLayouts.Count > 0 && _sessionEstablished)
        {
            Log($"Cleaning up {_testLayouts.Count} test layout(s)...");
            foreach (var layoutName in _testLayouts)
            {
                try { await GameClient.SendChat($"!lobby delete {layoutName}"); }
                catch { }
            }
            // Single wait for all deletions to process
            await Task.Delay(TestTimings.LayoutCleanupDelay);
        }

        // base.DisposeAsync() is safe to call here:
        // - The _didConnect tracking (Tier 1) ensures game client cleanup only runs
        //   if THIS test instance actually called a connect method.
        // - For session-reusing tests, _didConnect is false (EnsureAdminSessionAsync
        //   reuses the session without calling JoinWorldWithRetryAsync), so base
        //   skips the expensive Navigate("title") + disconnect + 500ms delay.
        // - For NonAdmin test, _didConnect is true but we already disconnected
        //   explicitly, so the Navigate("title") is a no-op.
        // - CreatedFarmers is per-instance and typically empty for session-reusing tests.
        await base.DisposeAsync();
    }

    #region Test Setup Helpers

    /// <summary>
    /// Ensures an admin session is active, reusing the existing connection if possible.
    /// Only reconnects if the session was lost or never established.
    /// </summary>
    private async Task EnsureAdminSessionAsync()
    {
        if (_sessionEstablished)
        {
            // Verify session is still valid
            try
            {
                var state = await GameClient.GetState();
                if (state?.IsConnected == true && state.IsInGame)
                {
                    LogDetail("Reusing existing admin session");
                    return;
                }
            }
            catch
            {
                // Session check failed, need to reconnect
            }

            LogDetail("Session lost, reconnecting...");
            _sessionEstablished = false;
        }

        // Establish new admin session
        _sharedAdminName ??= GenerateFarmerName("LobbyAdmin");

        await EnsureDisconnectedAsync();

        var joinResult = await JoinWorldWithRetryAsync(_sharedAdminName);
        AssertJoinSuccess(joinResult);

        // Grant admin role via API
        var adminResult = await ServerApi.GrantAdmin(_sharedAdminName);
        Assert.True(adminResult?.Success, $"Failed to grant admin: {adminResult?.Error}");
        Log($"Admin session established for {_sharedAdminName}");

        // Don't track the shared admin farmer in CreatedFarmers — we want to keep
        // this farmer alive across tests. It will be cleaned up naturally when the
        // next test class disconnects and runs its own cleanup.

        _sessionEstablished = true;
    }

    /// <summary>
    /// Sets up a player WITHOUT admin role.
    /// This temporarily breaks the shared admin session.
    /// </summary>
    private async Task SetupAsNonAdmin(string farmerNamePrefix = "User")
    {
        // Disconnect the shared admin session
        await EnsureDisconnectedAsync();
        _sessionEstablished = false;

        var farmerName = GenerateFarmerName(farmerNamePrefix);
        TrackFarmer(farmerName);

        // Join the server (auto-login handles authentication, but don't grant admin)
        var joinResult = await JoinWorldWithRetryAsync(farmerName);
        AssertJoinSuccess(joinResult);
    }

    /// <summary>
    /// Generates a unique layout name for test isolation.
    /// </summary>
    private string GenerateLayoutName(string prefix = "test")
    {
        var name = $"{prefix}-{DateTime.UtcNow.Ticks % 10000}";
        _testLayouts.Add(name);
        return name;
    }

    /// <summary>
    /// Sends a lobby command and polls until expected response strings appear in chat.
    /// </summary>
    private async Task<bool> AssertCommandResponse(string command, params string[] expectedContains)
    {
        await GameClient.SendChat(command);

        // Poll until ALL expected strings appear in chat (not just any one).
        // Polling for "any" can match stale messages from previous commands,
        // causing the poll to exit before the new command's response arrives.
        ChatHistoryResult? chatHistory = null;
        await PollingHelper.WaitUntilAsync(async () =>
        {
            chatHistory = await GameClient.GetChatHistory(20);
            return expectedContains.All(expected =>
                chatHistory?.Messages?.Any(m =>
                    m.Message.Contains(expected, StringComparison.OrdinalIgnoreCase)) == true);
        }, TestTimings.ChatCommandTimeout);

        Assert.NotNull(chatHistory);

        Log($"Command: {command}");
        Log($"Response ({chatHistory.Messages.Count} messages):");
        foreach (var msg in chatHistory.Messages)
        {
            Log($"  {msg.Message}");
        }

        var allFound = true;
        foreach (var expected in expectedContains)
        {
            var found = chatHistory.Messages.Any(m =>
                m.Message.Contains(expected, StringComparison.OrdinalIgnoreCase));

            if (!found)
            {
                Log($"  MISSING: '{expected}'");
                allFound = false;
            }
        }

        return allFound;
    }

    /// <summary>
    /// Sends a chat command and polls until a response containing any of the expected strings appears.
    /// Used for fire-and-forget commands where we need to wait for processing before continuing.
    /// </summary>
    private async Task SendCommandAndWaitAsync(string command, params string[] expectedContains)
    {
        await GameClient.SendChat(command);
        await PollingHelper.WaitUntilAsync(async () =>
        {
            var chat = await GameClient.GetChatHistory(20);
            return chat?.Messages?.Any(m =>
                expectedContains.Any(expected =>
                    m.Message.Contains(expected, StringComparison.OrdinalIgnoreCase))) == true;
        }, TestTimings.ChatCommandTimeout);
    }

    /// <summary>
    /// Cancels any active editing session.
    /// </summary>
    private async Task CancelEditingIfActive()
    {
        await GameClient.SendChat("!lobby cancel");
        // Poll until cancel response or "not editing" response appears
        await PollingHelper.WaitUntilAsync(async () =>
        {
            var chat = await GameClient.GetChatHistory(10);
            return chat?.Messages?.Any(m =>
                m.Message.Contains("Cancelled", StringComparison.OrdinalIgnoreCase) ||
                m.Message.Contains("not editing", StringComparison.OrdinalIgnoreCase)) == true;
        }, TestTimings.ChatCommandTimeout);
    }

    #endregion

    #region Category 1: Permissions

    /// <summary>
    /// Verifies that non-admin players cannot use lobby commands.
    /// This test temporarily breaks the shared session to connect as a non-admin.
    /// The next test's EnsureAdminSessionAsync() will re-establish the admin session.
    /// </summary>
    [Fact]
    public async Task NonAdmin_LobbyCommand_ReturnsDeniedMessage()
    {
        await SetupAsNonAdmin();

        var hasResponse = await AssertCommandResponse(
            "!lobby list",
            "admin");

        Assert.True(hasResponse, "Should receive permission denied message");

        await AssertNoExceptionsAsync("after non-admin lobby command");

        // Disconnect the non-admin so EnsureAdminSessionAsync can reconnect as admin
        await EnsureDisconnectedAsync();
    }

    /// <summary>
    /// Verifies that admin players can use lobby commands.
    /// </summary>
    [Fact]
    public async Task Admin_LobbyList_Succeeds()
    {
        await EnsureAdminSessionAsync();

        var hasResponse = await AssertCommandResponse(
            "!lobby list",
            "Lobby Layouts", "default");

        Assert.True(hasResponse, "Should see layouts list with default layout");

        await AssertNoExceptionsAsync("after admin lobby list");
    }

    #endregion

    #region Category 2: Layout CRUD

    /// <summary>
    /// Verifies creating a new layout works.
    /// </summary>
    [Fact]
    public async Task Create_NewLayout_Success()
    {
        await EnsureAdminSessionAsync();

        var layoutName = GenerateLayoutName("create");

        var hasResponse = await AssertCommandResponse(
            $"!lobby create {layoutName}",
            "Created", layoutName, "Editing mode");

        Assert.True(hasResponse, "Should see layout created message");

        // Cancel to exit editing mode
        await CancelEditingIfActive();

        await AssertNoExceptionsAsync("after creating layout");
    }

    /// <summary>
    /// Verifies listing layouts shows all available layouts.
    /// </summary>
    [Fact]
    public async Task List_ShowsAllLayouts()
    {
        await EnsureAdminSessionAsync();

        var hasResponse = await AssertCommandResponse(
            "!lobby list",
            "Lobby Layouts");

        Assert.True(hasResponse, "Should see layouts header");

        await AssertNoExceptionsAsync("after listing layouts");
    }

    /// <summary>
    /// Verifies setting an active layout works.
    /// </summary>
    [Fact]
    public async Task Set_ActiveLayout_Success()
    {
        await EnsureAdminSessionAsync();

        // Use default layout which always exists
        var hasResponse = await AssertCommandResponse(
            "!lobby set default",
            "Active layout set", "default");

        Assert.True(hasResponse, "Should see layout activated message");

        await AssertNoExceptionsAsync("after setting active layout");
    }

    /// <summary>
    /// Verifies setting a non-existent layout fails with error.
    /// </summary>
    [Fact]
    public async Task Set_NonExistentLayout_Fails()
    {
        await EnsureAdminSessionAsync();

        var hasResponse = await AssertCommandResponse(
            "!lobby set nonexistent-layout-12345",
            "not found");

        Assert.True(hasResponse, "Should see not found error");

        await AssertNoExceptionsAsync("after setting non-existent layout");
    }

    /// <summary>
    /// Verifies deleting the default layout fails.
    /// </summary>
    [Fact]
    public async Task Delete_DefaultLayout_Fails()
    {
        await EnsureAdminSessionAsync();

        var hasResponse = await AssertCommandResponse(
            "!lobby delete default",
            "Cannot delete", "default");

        Assert.True(hasResponse, "Should see cannot delete default message");

        await AssertNoExceptionsAsync("after trying to delete default");
    }

    #endregion

    #region Category 3: Editing Workflow

    /// <summary>
    /// Verifies editing an existing layout opens editing mode.
    /// </summary>
    [Fact]
    public async Task Edit_DefaultLayout_OpensForEditing()
    {
        await EnsureAdminSessionAsync();

        var hasResponse = await AssertCommandResponse(
            "!lobby edit default",
            "Editing layout", "default", "daylight");

        Assert.True(hasResponse, "Should see editing mode started");

        // Cancel editing
        await CancelEditingIfActive();

        await AssertNoExceptionsAsync("after editing layout");
    }

    /// <summary>
    /// Verifies save command without editing fails.
    /// </summary>
    [Fact]
    public async Task Save_NotEditing_Fails()
    {
        await EnsureAdminSessionAsync();

        var hasResponse = await AssertCommandResponse(
            "!lobby save",
            "not editing");

        Assert.True(hasResponse, "Should see not editing error");

        await AssertNoExceptionsAsync("after save without editing");
    }

    /// <summary>
    /// Verifies cancel command discards new layout.
    /// </summary>
    [Fact]
    public async Task Cancel_NewLayout_DiscardsLayout()
    {
        await EnsureAdminSessionAsync();

        var layoutName = GenerateLayoutName("canceltest");

        // Create new layout (enters editing mode)
        await SendCommandAndWaitAsync($"!lobby create {layoutName}", "Created", "Editing mode");

        // Cancel - should discard
        var hasResponse = await AssertCommandResponse(
            "!lobby cancel",
            "Cancelled", "discarded");

        Assert.True(hasResponse, "Should see cancelled message");

        // Verify layout doesn't exist by checking list output
        ChatHistoryResult? chatHistory = null;
        await SendCommandAndWaitAsync("!lobby list", "Lobby Layouts");
        chatHistory = await GameClient.GetChatHistory(10);

        // Look for layout name in list entries (format: "  - layoutname (X items)")
        // Exclude messages that are clearly not list entries (like our previous commands)
        var layoutInList = chatHistory?.Messages.Any(m =>
            m.Message.Contains($"- {layoutName}", StringComparison.OrdinalIgnoreCase) ||
            m.Message.Contains($"• {layoutName}", StringComparison.OrdinalIgnoreCase)) ?? false;

        Assert.False(layoutInList, "Cancelled layout should not appear in list");

        // Remove from cleanup list since it was discarded
        _testLayouts.Remove(layoutName);

        await AssertNoExceptionsAsync("after cancelling new layout");
    }

    #endregion

    #region Category 4: Input Validation

    /// <summary>
    /// Verifies creating with invalid name (special chars) fails.
    /// </summary>
    [Fact]
    public async Task Create_InvalidName_SpecialChars_Fails()
    {
        await EnsureAdminSessionAsync();

        var hasResponse = await AssertCommandResponse(
            "!lobby create test@layout!",
            "only contain");

        Assert.True(hasResponse, "Should see validation error about characters");

        await AssertNoExceptionsAsync("after invalid name");
    }

    /// <summary>
    /// Verifies create without name shows usage.
    /// </summary>
    [Fact]
    public async Task Create_EmptyName_ShowsUsage()
    {
        await EnsureAdminSessionAsync();

        var hasResponse = await AssertCommandResponse(
            "!lobby create",
            "Usage", "create");

        Assert.True(hasResponse, "Should see usage message");

        await AssertNoExceptionsAsync("after empty name");
    }

    #endregion

    #region Category 5: Help

    /// <summary>
    /// Verifies help command shows available commands.
    /// </summary>
    [Fact]
    public async Task Help_ShowsAllCommands()
    {
        await EnsureAdminSessionAsync();

        // Help output shows commands like "!lobby create <name> - Create new layout"
        // Note: Test client increases chat buffer to 20 messages to capture full help output
        var hasResponse = await AssertCommandResponse(
            "!lobby help",
            "Lobby Commands", "!lobby create", "!lobby save", "!lobby list");

        Assert.True(hasResponse, "Should see help with commands listed");

        await AssertNoExceptionsAsync("after help command");
    }

    /// <summary>
    /// Verifies unknown subcommand shows error.
    /// </summary>
    [Fact]
    public async Task UnknownSubcommand_ShowsError()
    {
        await EnsureAdminSessionAsync();

        var hasResponse = await AssertCommandResponse(
            "!lobby frobnicate",
            "Unknown", "help");

        Assert.True(hasResponse, "Should see unknown command error");

        await AssertNoExceptionsAsync("after unknown subcommand");
    }

    #endregion

    #region Category 6: Layout Operations During Editing

    /// <summary>
    /// Verifies that renaming a layout while it's being edited fails.
    /// This prevents data corruption when the editor saves.
    /// </summary>
    [Fact]
    public async Task Rename_LayoutBeingEdited_Fails()
    {
        await EnsureAdminSessionAsync();

        var layoutName = GenerateLayoutName("rename-edit");

        // Create and start editing a layout
        await SendCommandAndWaitAsync($"!lobby create {layoutName}", "Created", "Editing mode");

        // Try to rename it while editing
        // Note: Server returns "Cannot rename" and either "being edited" (new) or
        // "new name is available" (old behavior before guard was added to command handler)
        var hasResponse = await AssertCommandResponse(
            $"!lobby rename {layoutName} newname",
            "Cannot rename");

        Assert.True(hasResponse, "Should see cannot rename message");

        // Cancel editing
        await CancelEditingIfActive();

        await AssertNoExceptionsAsync("after trying to rename layout being edited");
    }

    /// <summary>
    /// Verifies that deleting a layout while it's being edited fails.
    /// This prevents orphaned editing sessions.
    /// </summary>
    [Fact]
    public async Task Delete_LayoutBeingEdited_Fails()
    {
        await EnsureAdminSessionAsync();

        var layoutName = GenerateLayoutName("delete-edit");

        // Create and start editing a layout
        await SendCommandAndWaitAsync($"!lobby create {layoutName}", "Created", "Editing mode");

        // Try to delete it while editing
        // Note: Server returns "Cannot delete" and either "being edited" (new) or
        // "not the active layout" (old behavior before guard was added to command handler)
        var hasResponse = await AssertCommandResponse(
            $"!lobby delete {layoutName}",
            "Cannot delete");

        Assert.True(hasResponse, "Should see cannot delete message");

        // Cancel editing
        await CancelEditingIfActive();

        await AssertNoExceptionsAsync("after trying to delete layout being edited");
    }

    /// <summary>
    /// Verifies that starting to edit while already editing fails.
    /// </summary>
    [Fact]
    public async Task Edit_WhileAlreadyEditing_Fails()
    {
        await EnsureAdminSessionAsync();

        var layoutName1 = GenerateLayoutName("edit1");

        // Start editing first layout
        await SendCommandAndWaitAsync($"!lobby create {layoutName1}", "Created", "Editing mode");

        // Try to edit default layout while already editing
        var hasResponse = await AssertCommandResponse(
            "!lobby edit default",
            "already editing");

        Assert.True(hasResponse, "Should see already editing message");

        // Cancel editing
        await CancelEditingIfActive();

        await AssertNoExceptionsAsync("after trying to edit while already editing");
    }

    /// <summary>
    /// Verifies that creating a new layout while already editing fails.
    /// </summary>
    [Fact]
    public async Task Create_WhileAlreadyEditing_Fails()
    {
        await EnsureAdminSessionAsync();

        var layoutName1 = GenerateLayoutName("create1");
        var layoutName2 = GenerateLayoutName("create2");

        // Start editing first layout
        await SendCommandAndWaitAsync($"!lobby create {layoutName1}", "Created", "Editing mode");

        // Try to create another layout while editing
        var hasResponse = await AssertCommandResponse(
            $"!lobby create {layoutName2}",
            "already editing");

        Assert.True(hasResponse, "Should see already editing message");

        // Cancel editing (cleans up layoutName1)
        await CancelEditingIfActive();

        // layoutName2 was never created, so remove from cleanup list
        _testLayouts.Remove(layoutName2);

        await AssertNoExceptionsAsync("after trying to create while already editing");
    }

    #endregion

    #region Category 7: Editor Decoupling Messages

    /// <summary>
    /// Verifies that entering editing mode shows the decoupling messages
    /// about permanent daylight and sleep immunity.
    /// </summary>
    [Fact]
    public async Task Create_ShowsDecouplingMessages()
    {
        await EnsureAdminSessionAsync();

        var layoutName = GenerateLayoutName("decoupling");

        var hasResponse = await AssertCommandResponse(
            $"!lobby create {layoutName}",
            "daylight", "exhaustion", "sleep");

        Assert.True(hasResponse, "Should see editor decoupling messages");

        // Cancel editing
        await CancelEditingIfActive();

        await AssertNoExceptionsAsync("after checking decoupling messages");
    }

    /// <summary>
    /// Verifies that editing an existing layout shows the decoupling messages.
    /// </summary>
    [Fact]
    public async Task Edit_ShowsDecouplingMessages()
    {
        await EnsureAdminSessionAsync();

        var hasResponse = await AssertCommandResponse(
            "!lobby edit default",
            "daylight", "exhaustion", "sleep");

        Assert.True(hasResponse, "Should see editor decoupling messages");

        // Cancel editing
        await CancelEditingIfActive();

        await AssertNoExceptionsAsync("after checking decoupling messages on edit");
    }

    #endregion

    #region Category 8: Save and Restore Workflow

    /// <summary>
    /// Verifies full create -> save workflow succeeds.
    /// </summary>
    [Fact]
    public async Task Create_Save_FullWorkflow_Succeeds()
    {
        await EnsureAdminSessionAsync();

        var layoutName = GenerateLayoutName("workflow");

        // Create layout
        await SendCommandAndWaitAsync($"!lobby create {layoutName}", "Created", "Editing mode");

        // Save layout
        var hasResponse = await AssertCommandResponse(
            "!lobby save",
            "Saved", layoutName);

        Assert.True(hasResponse, "Should see saved message");

        // Verify layout exists in list
        var listResponse = await AssertCommandResponse(
            "!lobby list",
            layoutName);

        Assert.True(listResponse, "Layout should appear in list after save");

        await AssertNoExceptionsAsync("after full create-save workflow");
    }

    /// <summary>
    /// Verifies edit -> cancel preserves original layout.
    /// </summary>
    [Fact]
    public async Task Edit_Cancel_PreservesOriginal()
    {
        await EnsureAdminSessionAsync();

        // Get info about default layout first
        await SendCommandAndWaitAsync("!lobby info default", "default");

        // Edit default layout
        await SendCommandAndWaitAsync("!lobby edit default", "Editing layout", "default");

        // Cancel without saving
        var hasResponse = await AssertCommandResponse(
            "!lobby cancel",
            "Cancelled", "discarded");

        Assert.True(hasResponse, "Should see cancel message");

        // Verify default layout still exists
        var listResponse = await AssertCommandResponse(
            "!lobby list",
            "default");

        Assert.True(listResponse, "Default layout should still exist after cancel");

        await AssertNoExceptionsAsync("after edit-cancel workflow");
    }

    #endregion

    #region Category 9: Copy and Rename Operations

    /// <summary>
    /// Verifies copying a layout creates an independent copy.
    /// </summary>
    [Fact]
    public async Task Copy_CreatesIndependentCopy()
    {
        await EnsureAdminSessionAsync();

        var destName = GenerateLayoutName("copy-dest");

        // Copy default layout
        var hasResponse = await AssertCommandResponse(
            $"!lobby copy default {destName}",
            "Copied", destName);

        Assert.True(hasResponse, "Should see copied message");

        // Verify copy exists in list
        var listResponse = await AssertCommandResponse(
            "!lobby list",
            destName, "default");

        Assert.True(listResponse, "Both original and copy should appear in list");

        await AssertNoExceptionsAsync("after copy operation");
    }

    /// <summary>
    /// Verifies renaming a layout updates references.
    /// </summary>
    [Fact]
    public async Task Rename_UpdatesLayoutName()
    {
        await EnsureAdminSessionAsync();

        var originalName = GenerateLayoutName("rename-orig");
        var newName = GenerateLayoutName("rename-new");

        // Create a layout to rename
        await SendCommandAndWaitAsync($"!lobby create {originalName}", "Created", "Editing mode");
        await SendCommandAndWaitAsync("!lobby save", "Saved", originalName);

        // Rename it
        var hasResponse = await AssertCommandResponse(
            $"!lobby rename {originalName} {newName}",
            "Renamed", newName);

        Assert.True(hasResponse, "Should see renamed message");

        // Verify old name gone, new name exists
        await SendCommandAndWaitAsync("!lobby list", "Lobby Layouts");

        var chatHistory = await GameClient.GetChatHistory(15);
        var hasNewName = chatHistory?.Messages.Any(m =>
            m.Message.Contains(newName, StringComparison.OrdinalIgnoreCase)) ?? false;
        var hasOldName = chatHistory?.Messages.Any(m =>
            m.Message.Contains($"- {originalName}", StringComparison.OrdinalIgnoreCase)) ?? false;

        Assert.True(hasNewName, "New name should appear in list");
        Assert.False(hasOldName, "Old name should not appear in list");

        // Update cleanup list
        _testLayouts.Remove(originalName);
        _testLayouts.Add(newName);

        await AssertNoExceptionsAsync("after rename operation");
    }

    #endregion
}
