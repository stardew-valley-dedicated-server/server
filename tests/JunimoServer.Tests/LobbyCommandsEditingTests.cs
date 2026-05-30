using JunimoServer.Tests.Infrastructure;
using Xunit;

namespace JunimoServer.Tests;

/// <summary>
/// Tests for lobby layout editing workflows, save/restore, copy, and rename operations.
/// Uses KeepConnected for admin session persistence.
/// </summary>
[TestServer(Password = "test-password-123", KeepConnected = true)]
public class LobbyCommandsEditingTests : LobbyCommandsTestBase
{
    public LobbyCommandsEditingTests() { }

    #region Editing Workflow

    /// <summary>
    /// Verifies editing an existing layout opens editing mode.
    /// </summary>
    [Fact]
    public async Task Edit_DefaultLayout_OpensForEditing()
    {
        await EnsureAdminSessionAsync();

        var hasResponse = await Chat.AssertResponseAsync(
            "!lobby edit default",
            "Editing layout",
            "default",
            "daylight"
        );

        Assert.True(hasResponse, "Should see editing mode started");

        await CancelEditingIfActive();

        await Exceptions.AssertNoExceptionsAsync("after editing layout");
    }

    /// <summary>
    /// Verifies save command without editing fails.
    /// </summary>
    [Fact]
    public async Task Save_NotEditing_Fails()
    {
        await EnsureAdminSessionAsync();

        var hasResponse = await Chat.AssertResponseAsync(
            "!lobby save",
            "not editing");

        Assert.True(hasResponse, "Should see not editing error");

        await Exceptions.AssertNoExceptionsAsync("after save without editing");
    }

    /// <summary>
    /// Verifies cancel command discards new layout.
    /// </summary>
    [Fact]
    public async Task Cancel_NewLayout_DiscardsLayout()
    {
        await EnsureAdminSessionAsync();

        var layoutName = GenerateLayoutName("canceltest");

        await Chat.SendAndWaitAsync($"!lobby create {layoutName}", "Created", "Editing mode");

        var hasResponse = await Chat.AssertResponseAsync(
            "!lobby cancel",
            "Cancelled", "discarded");

        Assert.True(hasResponse, "Should see cancelled message");

        await Chat.AssertResponseAsync("!lobby list", "Lobby Layouts", "default");
        var chatHistory = await GameClient.GetChatHistory(20);

        var layoutInList = chatHistory?.Messages.Any(m =>
            m.Message.Contains($"- {layoutName}", StringComparison.OrdinalIgnoreCase) ||
            m.Message.Contains($"\u2022 {layoutName}", StringComparison.OrdinalIgnoreCase)) ?? false;

        Assert.False(layoutInList, "Cancelled layout should not appear in list");

        _testLayouts.Remove(layoutName);

        await Exceptions.AssertNoExceptionsAsync("after cancelling new layout");
    }

    /// <summary>
    /// Verifies that starting to edit while already editing fails.
    /// </summary>
    [Fact]
    public async Task Edit_WhileAlreadyEditing_Fails()
    {
        await EnsureAdminSessionAsync();

        var layoutName1 = GenerateLayoutName("edit1");

        await Chat.SendAndWaitAsync($"!lobby create {layoutName1}", "Created", "Editing mode");

        var hasResponse = await Chat.AssertResponseAsync(
            "!lobby edit default",
            "already editing");

        Assert.True(hasResponse, "Should see already editing message");

        await CancelEditingIfActive();

        await Exceptions.AssertNoExceptionsAsync("after trying to edit while already editing");
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

        await Chat.SendAndWaitAsync($"!lobby create {layoutName1}", "Created", "Editing mode");

        var hasResponse = await Chat.AssertResponseAsync(
            $"!lobby create {layoutName2}",
            "already editing");

        Assert.True(hasResponse, "Should see already editing message");

        await CancelEditingIfActive();

        _testLayouts.Remove(layoutName2);

        await Exceptions.AssertNoExceptionsAsync("after trying to create while already editing");
    }

    #endregion

    #region Layout Operations During Editing

    /// <summary>
    /// Verifies that renaming a layout while it's being edited fails.
    /// </summary>
    [Fact]
    public async Task Rename_LayoutBeingEdited_Fails()
    {
        await EnsureAdminSessionAsync();

        var layoutName = GenerateLayoutName("rename-edit");

        await Chat.SendAndWaitAsync($"!lobby create {layoutName}", "Created", "Editing mode");

        var hasResponse = await Chat.AssertResponseAsync(
            $"!lobby rename {layoutName} newname",
            "Cannot rename");

        Assert.True(hasResponse, "Should see cannot rename message");

        await CancelEditingIfActive();

        await Exceptions.AssertNoExceptionsAsync("after trying to rename layout being edited");
    }

    /// <summary>
    /// Verifies that deleting a layout while it's being edited fails.
    /// </summary>
    [Fact]
    public async Task Delete_LayoutBeingEdited_Fails()
    {
        await EnsureAdminSessionAsync();

        var layoutName = GenerateLayoutName("delete-edit");

        await Chat.SendAndWaitAsync($"!lobby create {layoutName}", "Created", "Editing mode");

        var hasResponse = await Chat.AssertResponseAsync(
            $"!lobby delete {layoutName}",
            "Cannot delete");

        Assert.True(hasResponse, "Should see cannot delete message");

        await CancelEditingIfActive();

        await Exceptions.AssertNoExceptionsAsync("after trying to delete layout being edited");
    }

    #endregion

    #region Save/Restore and Copy/Rename

    /// <summary>
    /// Verifies full create -> save workflow succeeds.
    /// </summary>
    [Fact]
    public async Task Create_Save_FullWorkflow_Succeeds()
    {
        await EnsureAdminSessionAsync();

        var layoutName = GenerateLayoutName("workflow");

        await Chat.SendAndWaitAsync($"!lobby create {layoutName}", "Created", "Editing mode");

        var hasResponse = await Chat.AssertResponseAsync(
            "!lobby save",
            "Saved", layoutName);

        Assert.True(hasResponse, "Should see saved message");

        var listResponse = await Chat.AssertResponseAsync(
            "!lobby list",
            layoutName);

        Assert.True(listResponse, "Layout should appear in list after save");

        await Exceptions.AssertNoExceptionsAsync("after full create-save workflow");
    }

    /// <summary>
    /// Verifies copying a layout creates an independent copy.
    /// </summary>
    [Fact]
    public async Task Copy_CreatesIndependentCopy()
    {
        await EnsureAdminSessionAsync();

        var destName = GenerateLayoutName("copy-dest");

        var hasResponse = await Chat.AssertResponseAsync(
            $"!lobby copy default {destName}",
            "Copied", destName);

        Assert.True(hasResponse, "Should see copied message");

        var listResponse = await Chat.AssertResponseAsync(
            "!lobby list",
            destName, "default");

        Assert.True(listResponse, "Both original and copy should appear in list");

        await Exceptions.AssertNoExceptionsAsync("after copy operation");
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

        await Chat.SendAndWaitAsync($"!lobby create {originalName}", "Created", "Editing mode");
        await Chat.SendAndWaitAsync("!lobby save", "Saved", originalName);

        var hasResponse = await Chat.AssertResponseAsync(
            $"!lobby rename {originalName} {newName}",
            "Renamed", newName);

        Assert.True(hasResponse, "Should see renamed message");

        var listResponse = await Chat.AssertResponseAsync("!lobby list", "Lobby Layouts", newName);
        Assert.True(listResponse, "New name should appear in list");

        var chatHistory = await GameClient.GetChatHistory(20);
        var hasOldName = chatHistory?.Messages.Any(m =>
            m.Message.Contains($"- {originalName}", StringComparison.OrdinalIgnoreCase)) ?? false;
        Assert.False(hasOldName, "Old name should not appear in list");

        _testLayouts.Remove(originalName);
        _testLayouts.Add(newName);

        await Exceptions.AssertNoExceptionsAsync("after rename operation");
    }

    #endregion
}
