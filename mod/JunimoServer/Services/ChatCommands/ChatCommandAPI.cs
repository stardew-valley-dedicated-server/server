using System;

namespace JunimoServer.Services.ChatCommands;

public interface IChatCommandApi
{
    public void RegisterCommand(
        string name,
        string description,
        Action<string[], ReceivedMessage> action,
        bool requiresAdmin = false
    );
    public void RegisterCommand(ChatCommand command);
}
