using System;

namespace JunimoServer.Services.ChatCommands
{
    public interface IChatCommandApi
    {
        public void RegisterCommand(string name, string description, Action<string[], ReceivedMessage> action);
        public void RegisterCommand(ChatCommand command);
    }
}