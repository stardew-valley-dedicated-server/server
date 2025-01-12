using JunimoServer.Util;
using StardewValley.Network;
using System;
using System.IO;

namespace JunimoServer.Services.MessageInterceptors
{
    /// <summary>
    /// Represents the context for reading and manipulating outgoing messages.
    /// </summary>
    public class MessageContext : IDisposable
    {
        public long PeerId { get; }
        public int MessageType => OriginalMessage.MessageType;
        public OutgoingMessage OriginalMessage { get; }
        public OutgoingMessage ModifiedMessage { get; set; }
        public IncomingMessage IncomingMessage => _incomingMessageLazy.Value;

        private readonly Lazy<IncomingMessage> _incomingMessageLazy;

        public BinaryReader Reader
        {
            get => IncomingMessage.Reader;
        }

        public MessageContext(long peerId, OutgoingMessage message)
        {
            PeerId = peerId;
            OriginalMessage = message;
            ModifiedMessage = message;

            // Parse the outgoing message into an incoming message so we can read its content.
            //IncomingMessage = NetworkHelper.ParseOutgoingMessage(message);
            _incomingMessageLazy = new Lazy<IncomingMessage>(() => NetworkHelper.ParseOutgoingMessage(message));
        }

        public void Dispose()
        {
            if (_incomingMessageLazy.IsValueCreated)
            {
                IncomingMessage.Dispose();
            }
        }
    }
}
