using HarmonyLib;
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

        private readonly List<ChatCommand> _registeredCommands = new List<ChatCommand>();

        //private static readonly char _commandPrefix = '!';

        public ChatCommandsService(IMonitor monitor, Harmony harmony, IModHelper helper, RoleService roleService)
        {
            _monitor = monitor;
            _helper = helper;

            // Enable cheat/debug commands work (https://stardewvalleywiki.com/Modding:Console_commands#Debug_commands)
            Program.enableCheats = true;

            ChatWatcher.Initialize(OnChatMessage);

            harmony.Patch(
                original: AccessTools.Method(typeof(ChatBox), nameof(ChatBox.receiveChatMessage)),
                postfix: new HarmonyMethod(typeof(ChatWatcher), nameof(ChatWatcher.receiveChatMessage_Postfix))
            );

            RegisterCommand(new ChatCommand("help", "XXXDisplays available commands.", HelpCommand));
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
