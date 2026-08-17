using System;

namespace JunimoServer.Services.ChatCommands;

public class ChatCommand
{
    public string Name;
    public string Description;
    public Action<string[], ReceivedMessage> Action;

    /// <summary>
    /// When true, only admins may run the command and it is hidden from non-admins in the
    /// <c>!help</c> listing. Both the access check and the listing filter are enforced
    /// centrally in <see cref="ChatCommandsService"/>, so commands don't repeat the check.
    /// </summary>
    public bool RequiresAdmin;

    public string CommandUsage
    {
        get { return $"!{Name}: {Description}"; }
    }

    public ChatCommand(
        string name,
        string description,
        Action<string[], ReceivedMessage> action,
        bool requiresAdmin = false
    )
    {
        Name = name;
        Description = description;
        Action = action;
        RequiresAdmin = requiresAdmin;
    }
}
