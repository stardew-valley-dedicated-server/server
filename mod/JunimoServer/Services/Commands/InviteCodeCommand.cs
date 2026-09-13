using JunimoServer.Services.ChatCommands;
using JunimoServer.Util;
using StardewModdingAPI;
using StardewValley;

namespace JunimoServer.Services.Commands;

public class InviteCodeCommand
{
    private static IModHelper _helper;
    private static IMonitor _monitor;

    public static void Register(
        IModHelper helper,
        IMonitor monitor,
        ChatCommandsService chatCommandsService
    )
    {
        _helper = helper;
        _monitor = monitor;

        // Register chat command
        chatCommandsService.RegisterCommand(
            "invitecode",
            "Shows the server invite code.",
            (args, msg) =>
            {
                if (Game1.server == null)
                {
                    helper.SendPrivateMessage(msg.SourceFarmer, "Server is not running.");
                    return;
                }

                helper.SendPrivateMessage(
                    msg.SourceFarmer,
                    $"Invite code: {InviteCodes.Describe()}"
                );
            }
        );

        // Register console command
        helper.ConsoleCommands.Register(
            "invitecode",
            "Shows the server invite code.",
            InviteCodeConsoleCommand
        );
    }

    private static void InviteCodeConsoleCommand(string command, string[] args)
    {
        if (Game1.server == null)
        {
            _monitor.Log("Server is not running.", LogLevel.Warn);
            return;
        }

        var inviteCode = InviteCodes.Joinable;
        var status = InviteCodes.StatusCodeOf(inviteCode);
        // A missing code is worth a warning only while one is expected; a server without Steam auth
        // never has one.
        var level =
            inviteCode == null && status != ConnectionStatusCode.InviteUnavailable
                ? LogLevel.Warn
                : LogLevel.Info;
        _monitor.Log($"Invite code: {ConnectionStatus.Render(status, inviteCode)}", level);
    }
}
