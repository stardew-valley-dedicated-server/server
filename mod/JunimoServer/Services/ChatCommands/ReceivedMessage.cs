using StardewValley;
using System;

namespace JunimoServer.Services.ChatCommands
{
    public class ReceivedMessage
    {
        public enum ChatKinds
        {
            ChatMessage,
            ErrorMessage,
            UserNotification,
            PrivateMessage
        }

        public long SourceFarmer { get; set; }
        public ChatKinds ChatKind { get; set; }
        public LocalizedContentManager.LanguageCode Language { get; set; }
        public string Message { get; set; }

        public bool IsCommand
        {
            get 
            {
                return !String.IsNullOrEmpty(Message) && Message[0] == '!' && Message.Length > 1;
            }
        }

        public ChatCommandMessage Command
        {
            get
            {
                // Remove prefix and command name with ranges
                var commandParts = ArgUtility.SplitBySpace(Message[1..]);
                var commandName = commandParts[0];
                var commandArgs = commandParts[1..];

                return new ChatCommandMessage(commandName, commandArgs);
            }
        }
    }
}