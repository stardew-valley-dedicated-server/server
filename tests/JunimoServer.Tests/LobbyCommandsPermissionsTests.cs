using JunimoServer.Tests.Infrastructure;
using Xunit;

namespace JunimoServer.Tests;

/// <summary>
/// Tests for lobby command permissions and help.
/// These are lightweight checks that don't need persistent sessions.
/// </summary>
[TestServer(Password = "test-password-123")]
public class LobbyCommandsPermissionsTests : LobbyCommandsTestBase
{
    public LobbyCommandsPermissionsTests() { }

    /// <summary>
    /// Verifies that non-admin players cannot use lobby commands.
    /// </summary>
    [Fact]
    public async Task NonAdmin_LobbyCommand_ReturnsDeniedMessage()
    {
        await SetupAsNonAdmin();

        var hasResponse = await Chat.AssertResponseAsync("!lobby list", "admin");

        Assert.True(hasResponse, "Should receive permission denied message");

        await Exceptions.AssertNoExceptionsAsync("after non-admin lobby command");

        await Connect.EnsureDisconnectedAsync();
    }

    /// <summary>
    /// Verifies that admin players can use lobby commands.
    /// </summary>
    [Fact]
    public async Task Admin_LobbyList_Succeeds()
    {
        await EnsureAdminSessionAsync();

        var hasResponse = await Chat.AssertResponseAsync("!lobby list", "Lobby Layouts", "default");

        Assert.True(hasResponse, "Should see layouts list with default layout");

        await Exceptions.AssertNoExceptionsAsync("after admin lobby list");
    }

    /// <summary>
    /// Verifies help command shows available commands.
    /// </summary>
    [Fact]
    public async Task Help_ShowsAllCommands()
    {
        await EnsureAdminSessionAsync();

        var hasResponse = await Chat.AssertResponseAsync(
            "!lobby help",
            "Lobby Commands",
            "!lobby create",
            "!lobby save",
            "!lobby list"
        );

        Assert.True(hasResponse, "Should see help with commands listed");

        await Exceptions.AssertNoExceptionsAsync("after help command");
    }

    /// <summary>
    /// Verifies unknown subcommand shows error.
    /// </summary>
    [Fact]
    public async Task UnknownSubcommand_ShowsError()
    {
        await EnsureAdminSessionAsync();

        var hasResponse = await Chat.AssertResponseAsync("!lobby frobnicate", "Unknown", "help");

        Assert.True(hasResponse, "Should see unknown command error");

        await Exceptions.AssertNoExceptionsAsync("after unknown subcommand");
    }
}
