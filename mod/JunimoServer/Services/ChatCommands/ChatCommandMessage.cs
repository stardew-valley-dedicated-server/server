using System;

namespace JunimoServer.Services.ChatCommands
{
    public class ChatCommandMessage
    {
        public string Name;
        public string[] Args;

        public ChatCommandMessage(string name, string[] args)
        {
            Name = name;
            Args = args;
        }
    }
}