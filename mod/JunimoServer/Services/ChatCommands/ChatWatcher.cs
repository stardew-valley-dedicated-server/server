using System;
using StardewValley;
using System.Diagnostics;

namespace JunimoServer.Services.ChatCommands
{
    public static class ChatWatcher
    {
        private static Action<ReceivedMessage> _onChatMessage;

        public static void Initialize(Action<ReceivedMessage> onChatMessage)

        {
            _onChatMessage = onChatMessage;
        }

        public static void receiveChatMessage_Postfix(long sourceFarmer, int chatKind, LocalizedContentManager.LanguageCode language, string message)
        {
            var msg = new ReceivedMessage
            {
                SourceFarmer = sourceFarmer,
                ChatKind = (ReceivedMessage.ChatKinds) chatKind,
                Language = language,
                Message = message
            };
            
            _onChatMessage(msg);
        }
    }
}