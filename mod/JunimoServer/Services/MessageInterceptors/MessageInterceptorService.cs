using HarmonyLib;
using JunimoServer.Util;
using StardewModdingAPI;
using StardewValley;
using StardewValley.Network;
using System;
using System.Collections.Generic;

namespace JunimoServer.Services.MessageInterceptors
{
    public interface IMessageInterceptor
    {
        void Intercept(MessageContext context);
    }

    public delegate void MessageInterceptor(MessageContext context);

    public class MessageInterceptorsService : ModService
    {
        private static MessageInterceptorsService _this;

        private readonly Dictionary<int, List<MessageInterceptor>> _interceptorsOutgoing = new();

        public MessageInterceptorsService(IMonitor monitor, Harmony harmony) : base(monitor)
        {
            harmony.Patch(
                original: AccessTools.Method(typeof(GameServer), "processIncomingMessage", new[] { typeof(IncomingMessage) }),
                prefix: new HarmonyMethod(typeof(MessageInterceptorsService), nameof(processIncomingMessage_Prefix))
            );

            harmony.Patch(
                original: AccessTools.Method(typeof(GameServer), "sendMessage", new[] { typeof(long), typeof(OutgoingMessage) }),
                prefix: new HarmonyMethod(typeof(MessageInterceptorsService), nameof(sendMessage_Prefix))
            );

            // Internal static self-reference is used to access the instance inside harmony patches
            // TODO: Pretty ugly, but using service-locator to locate the service inside itself feels even worse. Maaaaybe singletons... but meh, still thinking
            if (_this != null)
            {
                throw new Exception("MessageInterceptorsService should only exist once");
            }

            _this = this;
        }

        public MessageInterceptorsService Add(int messageType, MessageInterceptor interceptor)
        {
            if (!_interceptorsOutgoing.ContainsKey(messageType))
            {
                _interceptorsOutgoing[messageType] = new List<MessageInterceptor>();
            }
            _interceptorsOutgoing[messageType].Add(interceptor);

            return this;
        }

        private void HandleIncoming(ref IncomingMessage message)
        {
            // Just some temporary (?) trace logging
            switch (message.MessageType)
            {
                // Filter noisy messages
                case Multiplayer.worldDelta:
                case Multiplayer.locationDelta:
                    break;

                default:
                    string messageType = Enum.GetName(typeof(MessageTypes), message.MessageType);
                    Monitor.Log($"IncomingMessage {{ Type: {messageType}, From: {message.FarmerID} }}");
                    break;
            }
        }

        private void HandleOutgoing(long peerId, ref OutgoingMessage message)
        {
            // Just some temporary (?) trace logging
            switch (message.MessageType)
            {
                // Filter noisy messages
                case Multiplayer.worldDelta:
                case Multiplayer.locationDelta:
                    break;

                default:
                    string messageType = Enum.GetName(typeof(MessageTypes), message.MessageType);
                    Monitor.Log($"OutgoingMessage {{ Type: {messageType}, To: {peerId} }}");
                    break;
            }


            // The actual message interceptor
            if (_interceptorsOutgoing.TryGetValue(message.MessageType, out var interceptors))
            {
                using (var context = new MessageContext(peerId, message))
                {
                    foreach (var interceptor in interceptors)
                    {
                        interceptor(context);
                    }

                    message = context.ModifiedMessage;
                };
            }
        }

        private static void processIncomingMessage_Prefix(ref IncomingMessage message)
        {
            _this.HandleIncoming(ref message);
        }

        private static void sendMessage_Prefix(long peerId, ref OutgoingMessage message)
        {
            _this.HandleOutgoing(peerId, ref message);
        }
    }
}
