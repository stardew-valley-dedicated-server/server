using HarmonyLib;
using JunimoServer.Services.Api;
using JunimoServer.Services.Roles;
using JunimoServer.Util;
using StardewModdingAPI;
using StardewValley;
using StardewValley.Menus;
using System;
using System.Collections.Generic;
using System.Linq;

namespace JunimoServer.Services.ChatCommands
{
    /// <summary>
    /// Support server side chat commands without client side mods.
    /// Server checks incoming messages for our custom command pattern `!command arg1 arg2` and reacts accordingly.
    /// </summary>
    public class ChatCommandsService : ModService, IChatCommandApi
    {
        private readonly IMonitor _monitor;
        private readonly IModHelper _helper;
        private readonly ApiService _apiService;

        private readonly List<ChatCommand> _registeredCommands = new List<ChatCommand>();

        //private static readonly char _commandPrefix = '!';

        public ChatCommandsService(IMonitor monitor, Harmony harmony, IModHelper helper, RoleService roleService, ApiService apiService)
        {
            _monitor = monitor;
            _helper = helper;
            _apiService = apiService;

            // Enable cheat/debug commands work (https://stardewvalleywiki.com/Modding:Console_commands#Debug_commands)
            Program.enableCheats = true;

            ChatWatcher.Initialize(OnChatMessage);

            harmony.Patch(
                original: AccessTools.Method(typeof(ChatBox), nameof(ChatBox.receiveChatMessage)),
                postfix: new HarmonyMethod(typeof(ChatWatcher), nameof(ChatWatcher.receiveChatMessage_Postfix))
            );

            RegisterCommand(new ChatCommand("help", "Displays available commands.", HelpCommand));

            // Subscribe to chat messages from WebSocket clients (e.g., Discord bot)
            _apiService.OnExternalChatMessage += HandleExternalChatMessage;
        }

        /// <summary>
        /// Sends a message from an external source (like Discord) to the game chat.
        /// </summary>
        public void SendExternalMessage(string author, string message)
        {
            // Sanitize author name: limit length, remove newlines/control chars
            author = SanitizeInput(author, maxLength: 32);
            if (string.IsNullOrWhiteSpace(author))
            {
                author = "Anonymous";
            }

            // Sanitize message: limit length, remove control chars
            message = SanitizeInput(message, maxLength: 450);
            if (string.IsNullOrWhiteSpace(message))
            {
                return; // Don't send empty messages
            }

            var formatted = $"(Web) {author}: {message}";
            _helper.SendPublicMessage(formatted);
            _monitor.Log($"[ChatCommands] External message sent: {formatted}", LogLevel.Trace);
        }

        /// <summary>
        /// Sanitizes input by removing control characters and limiting length.
        /// </summary>
        private static string SanitizeInput(string input, int maxLength)
        {
            if (string.IsNullOrEmpty(input))
            {
                return string.Empty;
            }

            // Remove control characters (except space) and trim
            var sanitized = new string(input
                .Where(c => !char.IsControl(c) || c == ' ')
                .ToArray())
                .Trim();

            // Collapse multiple spaces
            while (sanitized.Contains("  "))
            {
                sanitized = sanitized.Replace("  ", " ");
            }

            // Truncate if needed
            if (sanitized.Length > maxLength)
            {
                sanitized = sanitized[..maxLength] + "...";
            }

            return sanitized;
        }

        private void HandleExternalChatMessage(string author, string message)
        {
            SendExternalMessage(author, message);
        }

        private void HelpCommand(string[] args, ReceivedMessage msg)
        {
            if (args.Length > 0)
            {
                // Show help for specific commands passed as args
                foreach (var command in _registeredCommands.Where(command => args.Contains(command.Name)))
                {
                    _helper.SendPrivateMessage(msg.SourceFarmer, command.CommandUsage);
                }
            }
            else
            {
                // Show help for all commands
                foreach (var command in _registeredCommands)
                {
                    _helper.SendPrivateMessage(msg.SourceFarmer, command.CommandUsage);
                }
            }
        }

        private void OnChatMessage(ReceivedMessage receivedMessage)
        {
            _monitor.Log($"[ChatCommands] OnChatMessage: {receivedMessage.Message}", LogLevel.Trace);

            if (!receivedMessage.IsCommand)
            {
                // Broadcast non-command player chat messages to WebSocket clients
                if (receivedMessage.ChatKind == ReceivedMessage.ChatKinds.ChatMessage)
                {
                    var playerName = _helper.GetFarmerNameById(receivedMessage.SourceFarmer) ?? "Unknown";
                    _apiService.BroadcastChatMessage(playerName, receivedMessage.Message);
                }
                return;
            }

            foreach (var command in _registeredCommands.Where(command => command.Name.Equals(receivedMessage.Command.Name)))
            {
                _monitor.Log($"[ChatCommands] Found command: {command.Name}", LogLevel.Trace);
                command.Action(receivedMessage.Command.Args, receivedMessage);
            }
        }

        public void RegisterCommand(string name, string description, Action<string[], ReceivedMessage> action)
        {
            _registeredCommands.Add(new ChatCommand(name, description, action));
        }

        public void RegisterCommand(ChatCommand command)
        {
            _registeredCommands.Add(command);
        }
    }
}
