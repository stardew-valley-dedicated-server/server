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

                var inviteCode = InviteCodes.Joinable;

                if (inviteCode == null)
                {
                    helper.SendPrivateMessage(
                        msg.SourceFarmer,
                        $"Invite code not yet available ({InviteCodes.UnavailableReason})."
                    );
                    return;
                }

                helper.SendPrivateMessage(msg.SourceFarmer, $"Invite code: {inviteCode}");
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

        if (inviteCode == null)
        {
            _monitor.Log(
                $"Invite code not yet available ({InviteCodes.UnavailableReason}).",
                LogLevel.Warn
            );
            return;
        }

        _monitor.Log($"Invite code: {inviteCode}", LogLevel.Info);
    }
}
