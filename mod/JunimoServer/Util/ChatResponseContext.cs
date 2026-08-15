using System;
using JunimoServer.Services.ChatCommands;

namespace JunimoServer.Util;

/// <summary>
/// Ambient holder for the pagination scope active during a chat-command dispatch, decoupling
/// <see cref="ModHelperExtensions.SendPrivateMessage"/> from <see cref="ChatCommandsService"/>.
/// [ThreadStatic]: the scope is set, buffered, and flushed synchronously on the game thread
/// (no awaits), so an off-thread SendPrivateMessage sees null and sends directly rather than
/// buffering into — or racing the buffer of — an unrelated command's scope.
/// </summary>
public static class ChatResponseContext
{
    [ThreadStatic]
    public static ChatResponseScope Current;
}
