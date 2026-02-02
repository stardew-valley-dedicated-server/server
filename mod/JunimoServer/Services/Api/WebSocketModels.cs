using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace JunimoServer.Services.Api
{
    /// <summary>
    /// Base WebSocket message envelope.
    /// </summary>
    public class WebSocketMessage
    {
        [JsonProperty("type")]
        public string Type { get; set; } = "";

        [JsonProperty("payload")]
        public JObject? Payload { get; set; }
    }

    /// <summary>
    /// Payload for chat_send messages (Discord → Game).
    /// </summary>
    public class ChatSendPayload
    {
        [JsonProperty("author")]
        public string Author { get; set; } = "";

        [JsonProperty("message")]
        public string Message { get; set; } = "";
    }

    /// <summary>
    /// Payload for chat events (Game → Discord).
    /// </summary>
    public class ChatEventPayload
    {
        [JsonProperty("playerName")]
        public string PlayerName { get; set; } = "";

        [JsonProperty("message")]
        public string Message { get; set; } = "";

        [JsonProperty("timestamp")]
        public string Timestamp { get; set; } = "";
    }
}
