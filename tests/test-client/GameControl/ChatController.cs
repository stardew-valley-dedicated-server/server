using HarmonyLib;
using JunimoServer.Shared;
using JunimoTestClient.Diagnostics;
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
    private readonly Harmony _harmony;

    /// <summary>
    /// Monotonically increasing counter of total chat messages received.
    /// Incremented by Harmony postfix patches on ChatBox methods.
    /// </summary>
    private static long _totalReceived;

    public ChatController(IMonitor monitor, Harmony harmony)
    {
        _monitor = monitor;
        _harmony = harmony;
    }

    /// <summary>
    /// Installs Harmony patches to track chat message counts.
    /// </summary>
    public void InstallPatches()
    {
        var receiveChatMessage = AccessTools.Method(typeof(ChatBox), nameof(ChatBox.receiveChatMessage));
        if (receiveChatMessage != null)
        {
            _harmony.Patch(receiveChatMessage,
                postfix: new HarmonyMethod(typeof(ChatController), nameof(ChatMessage_Postfix)));
        }

        var addMessage = AccessTools.Method(typeof(ChatBox), nameof(ChatBox.addMessage),
            new[] { typeof(string), typeof(Color) });
        if (addMessage != null)
        {
            _harmony.Patch(addMessage,
                postfix: new HarmonyMethod(typeof(ChatController), nameof(ChatMessage_Postfix)));
        }

        var addInfoMessage = AccessTools.Method(typeof(ChatBox), nameof(ChatBox.addInfoMessage));
        if (addInfoMessage != null)
        {
            _harmony.Patch(addInfoMessage,
                postfix: new HarmonyMethod(typeof(ChatController), nameof(ChatMessage_Postfix)));
        }

        _monitor.Log("Chat tracking patches installed", LogLevel.Trace);
    }

    /// <summary>
    /// Harmony postfix that increments the global message counter.
    /// </summary>
    public static void ChatMessage_Postfix()
    {
        Interlocked.Increment(ref _totalReceived);
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

            _monitor.Log($"Sent chat message: {ChatRedaction.MaskSecrets(message)}", LogLevel.Trace);
            ClientEventLog.Emit("client_chat_sent", new
            {
                message = ChatRedaction.MaskSecrets(message),
                length = message.Length
            });

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
            var total = Interlocked.Read(ref _totalReceived);

            var startIndex = Math.Max(0, chatMessages.Count - count);
            var windowSize = chatMessages.Count - startIndex;
            for (int i = startIndex; i < chatMessages.Count; i++)
            {
                var msg = chatMessages[i];
                // Assign sequence numbers: most recent message gets Seq = total,
                // earlier messages count down from there
                var offset = chatMessages.Count - 1 - i;
                messages.Add(new ChatMessageInfo
                {
                    // ChatMessage stores parsed emoji segments, we need to reconstruct text
                    Text = GetMessageText(msg),
                    ColorHex = ColorToHex(msg.color),
                    Alpha = msg.alpha,
                    Seq = total - offset
                });
            }

            return new ChatHistoryResult
            {
                Success = true,
                Messages = messages,
                TotalReceived = total
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
        // The game's ChatBox wraps long messages via Game1.parseText(), inserting
        // newlines to fit the chat box pixel width. Collapse them back to spaces
        // so each logical message stays on one line in test output.
        return text.Replace("\r\n", " ").Replace("\n", " ").TrimEnd();
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
    public long TotalReceived { get; set; }
}

public class ChatMessageInfo
{
    public string Text { get; set; } = "";
    public string ColorHex { get; set; } = "#FFFFFF";
    public float Alpha { get; set; }
    public long Seq { get; set; }
}

#endregion
