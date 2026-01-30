using Microsoft.Xna.Framework;
using StardewModdingAPI;
using StardewValley;
using StardewValley.Menus;

namespace JunimoTestClient.GameControl;

/// <summary>
/// Controls chat interactions.
/// </summary>
public class ChatController
{
    private readonly IMonitor _monitor;

    public ChatController(IMonitor monitor)
    {
        _monitor = monitor;
    }

    /// <summary>
    /// Send a chat message.
    /// </summary>
    public ChatResult SendMessage(string message)
    {
        try
        {
            if (!Context.IsWorldReady)
            {
                return new ChatResult { Success = false, Error = "Not in a game" };
            }

            if (Game1.chatBox == null)
            {
                return new ChatResult { Success = false, Error = "Chat box not available" };
            }

            if (string.IsNullOrEmpty(message))
            {
                return new ChatResult { Success = false, Error = "Message cannot be empty" };
            }

            // Use the chatBox's textBoxEnter method to send the message
            // This handles both regular messages and commands (starting with /)
            Game1.chatBox.textBoxEnter(message);

            _monitor.Log($"Sent chat message: {message}", LogLevel.Trace);

            return new ChatResult
            {
                Success = true,
                Message = "Message sent"
            };
        }
        catch (Exception ex)
        {
            _monitor.Log($"Failed to send chat message: {ex.Message}", LogLevel.Error);
            return new ChatResult { Success = false, Error = ex.Message };
        }
    }

    /// <summary>
    /// Send a local info message (only visible to this player).
    /// </summary>
    public ChatResult SendLocalInfo(string message)
    {
        try
        {
            if (!Context.IsWorldReady)
            {
                return new ChatResult { Success = false, Error = "Not in a game" };
            }

            if (Game1.chatBox == null)
            {
                return new ChatResult { Success = false, Error = "Chat box not available" };
            }

            Game1.chatBox.addInfoMessage(message);

            return new ChatResult
            {
                Success = true,
                Message = "Info message added"
            };
        }
        catch (Exception ex)
        {
            _monitor.Log($"Failed to add info message: {ex.Message}", LogLevel.Error);
            return new ChatResult { Success = false, Error = ex.Message };
        }
    }

    /// <summary>
    /// Add a colored message to the chat (local only).
    /// </summary>
    public ChatResult AddColoredMessage(string message, string colorHex)
    {
        try
        {
            if (!Context.IsWorldReady)
            {
                return new ChatResult { Success = false, Error = "Not in a game" };
            }

            if (Game1.chatBox == null)
            {
                return new ChatResult { Success = false, Error = "Chat box not available" };
            }

            var color = ParseColor(colorHex) ?? Color.White;
            Game1.chatBox.addMessage(message, color);

            return new ChatResult
            {
                Success = true,
                Message = "Colored message added"
            };
        }
        catch (Exception ex)
        {
            _monitor.Log($"Failed to add colored message: {ex.Message}", LogLevel.Error);
            return new ChatResult { Success = false, Error = ex.Message };
        }
    }

    /// <summary>
    /// Get recent chat messages.
    /// </summary>
    public ChatHistoryResult GetRecentMessages(int count = 10)
    {
        try
        {
            if (Game1.chatBox == null)
            {
                return new ChatHistoryResult { Success = false, Error = "Chat box not available" };
            }

            var messages = new List<ChatMessageInfo>();
            var chatMessages = Game1.chatBox.messages;

            var startIndex = Math.Max(0, chatMessages.Count - count);
            for (int i = startIndex; i < chatMessages.Count; i++)
            {
                var msg = chatMessages[i];
                messages.Add(new ChatMessageInfo
                {
                    // ChatMessage stores parsed emoji segments, we need to reconstruct text
                    Text = GetMessageText(msg),
                    ColorHex = ColorToHex(msg.color),
                    Alpha = msg.alpha
                });
            }

            return new ChatHistoryResult
            {
                Success = true,
                Messages = messages
            };
        }
        catch (Exception ex)
        {
            _monitor.Log($"Failed to get chat messages: {ex.Message}", LogLevel.Error);
            return new ChatHistoryResult { Success = false, Error = ex.Message };
        }
    }

    private static string GetMessageText(ChatMessage msg)
    {
        // ChatMessage stores segments in 'message' list
        var text = "";
        foreach (var segment in msg.message)
        {
            if (segment.message != null)
                text += segment.message;
            else if (segment.emojiIndex >= 0)
                text += $"[emoji:{segment.emojiIndex}]";
        }
        return text;
    }

    private static Color? ParseColor(string hex)
    {
        if (string.IsNullOrEmpty(hex))
            return null;

        hex = hex.TrimStart('#');

        if (hex.Length == 6)
        {
            var r = Convert.ToInt32(hex.Substring(0, 2), 16);
            var g = Convert.ToInt32(hex.Substring(2, 2), 16);
            var b = Convert.ToInt32(hex.Substring(4, 2), 16);
            return new Color(r, g, b);
        }

        return null;
    }

    private static string ColorToHex(Color color)
    {
        return $"#{color.R:X2}{color.G:X2}{color.B:X2}";
    }
}

#region DTOs

public class ChatResult
{
    public bool Success { get; set; }
    public string? Message { get; set; }
    public string? Error { get; set; }
}

public class ChatHistoryResult
{
    public bool Success { get; set; }
    public string? Error { get; set; }
    public List<ChatMessageInfo> Messages { get; set; } = new();
}

public class ChatMessageInfo
{
    public string Text { get; set; } = "";
    public string ColorHex { get; set; } = "#FFFFFF";
    public float Alpha { get; set; }
}

#endregion
