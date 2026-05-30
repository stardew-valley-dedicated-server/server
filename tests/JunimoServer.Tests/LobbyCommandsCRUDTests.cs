using JunimoServer.Tests.Infrastructure;
using Xunit;

namespace JunimoServer.Tests;

/// <summary>
/// Tests for lobby layout CRUD operations, input validation, and decoupling messages.
/// Uses KeepConnected for admin session persistence.
/// </summary>
[TestServer(Password = "test-password-123", KeepConnected = true)]
public class LobbyCommandsCRUDTests : LobbyCommandsTestBase
{
    public LobbyCommandsCRUDTests() { }

    #region Layout CRUD

    /// <summary>
    /// Verifies creating a new layout works.
    /// </summary>
    [Fact]
    public async Task Create_NewLayout_Success()
    {
        await EnsureAdminSessionAsync();

        var layoutName = GenerateLayoutName("create");

        var hasResponse = await Chat.AssertResponseAsync(
            $"!lobby create {layoutName}",
            "Created", layoutName, "Editing mode");

        Assert.True(hasResponse, "Should see layout created message");

        await CancelEditingIfActive();

        await Exceptions.AssertNoExceptionsAsync("after creating layout");
    }

    /// <summary>
    /// Verifies listing layouts shows all available layouts.
    /// </summary>
    [Fact]
    public async Task List_ShowsAllLayouts()
    {
        await EnsureAdminSessionAsync();

        var hasResponse = await Chat.AssertResponseAsync(
            "!lobby list",
            "Lobby Layouts");

        Assert.True(hasResponse, "Should see layouts header");

        await Exceptions.AssertNoExceptionsAsync("after listing layouts");
    }

    /// <summary>
    /// Verifies setting an active layout works.
    /// </summary>
    [Fact]
    public async Task Set_ActiveLayout_Success()
    {
        await EnsureAdminSessionAsync();

        var hasResponse = await Chat.AssertResponseAsync(
            "!lobby set default",
            "Active layout set", "default");

        Assert.True(hasResponse, "Should see layout activated message");

        await Exceptions.AssertNoExceptionsAsync("after setting active layout");
    }

    /// <summary>
    /// Verifies setting a non-existent layout fails with error.
    /// </summary>
    [Fact]
    public async Task Set_NonExistentLayout_Fails()
    {
        await EnsureAdminSessionAsync();

        var hasResponse = await Chat.AssertResponseAsync(
            "!lobby set nonexistent-layout-12345",
            "not found");

        Assert.True(hasResponse, "Should see not found error");

        await Exceptions.AssertNoExceptionsAsync("after setting non-existent layout");
    }

    /// <summary>
    /// Verifies deleting the default layout fails.
    /// </summary>
    [Fact]
    public async Task Delete_DefaultLayout_Fails()
    {
        await EnsureAdminSessionAsync();

        var hasResponse = await Chat.AssertResponseAsync(
            "!lobby delete default",
            "Cannot delete", "default");

        Assert.True(hasResponse, "Should see cannot delete default message");

        await Exceptions.AssertNoExceptionsAsync("after trying to delete default");
    }

    #endregion

    #region Input Validation

    /// <summary>
    /// Verifies creating with invalid name (special chars) fails.
    /// </summary>
    [Fact]
    public async Task Create_InvalidName_SpecialChars_Fails()
    {
        await EnsureAdminSessionAsync();

        var hasResponse = await Chat.AssertResponseAsync(
            "!lobby create test@layout!",
            "only contain");

        Assert.True(hasResponse, "Should see validation error about characters");

        await Exceptions.AssertNoExceptionsAsync("after invalid name");
    }

    /// <summary>
    /// Verifies create without name shows usage.
    /// </summary>
    [Fact]
    public async Task Create_EmptyName_ShowsUsage()
    {
        await EnsureAdminSessionAsync();

        var hasResponse = await Chat.AssertResponseAsync(
            "!lobby create",
            "Usage", "create");

        Assert.True(hasResponse, "Should see usage message");

        await Exceptions.AssertNoExceptionsAsync("after empty name");
    }

    #endregion

    #region Decoupling Messages

    /// <summary>
    /// Verifies that entering editing mode (via both create and edit) shows
    /// immunity notices (permanent daylight, no exhaustion, sleep exclusion).
    /// </summary>
    [Fact]
    public async Task EditMode_ShowsImmunityNotices()
    {
        await EnsureAdminSessionAsync();

        // Via create
        var layoutName = GenerateLayoutName("immunity");
        var createResult = await Chat.AssertResponseAsync(
            $"!lobby create {layoutName}",
            "daylight", "exhaustion", "sleep");
        Assert.True(createResult, "Create should show immunity notices");
        await CancelEditingIfActive();

        // Via edit
        var editResult = await Chat.AssertResponseAsync(
            "!lobby edit default",
            "daylight", "exhaustion", "sleep");
        Assert.True(editResult, "Edit should show immunity notices");
        await CancelEditingIfActive();

        await Exceptions.AssertNoExceptionsAsync("after checking immunity notices");
    }

    /// <summary>
    /// Verifies edit -> cancel preserves original layout.
    /// </summary>
    [Fact]
    public async Task Edit_Cancel_PreservesOriginal()
    {
        await EnsureAdminSessionAsync();

        await Chat.SendAndWaitAsync("!lobby info default", "default");

        await Chat.SendAndWaitAsync("!lobby edit default", "Editing layout", "default");

        var hasResponse = await Chat.AssertResponseAsync(
            "!lobby cancel",
            "Cancelled", "discarded");

        Assert.True(hasResponse, "Should see cancel message");

        var listResponse = await Chat.AssertResponseAsync(
            "!lobby list",
            "default");

        Assert.True(listResponse, "Default layout should still exist after cancel");

        await Exceptions.AssertNoExceptionsAsync("after edit-cancel workflow");
    }

    #endregion
}
