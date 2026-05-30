using StardewModdingAPI;
using StardewValley;
using StardewValley.Network;
using StardewValley.SDKs;

namespace JunimoTestClient.Util;

/// <summary>
/// Helper for accessing protected/internal game APIs via reflection.
/// </summary>
public class ReflectionHelper
{
    private readonly IModHelper _helper;

    public ReflectionHelper(IModHelper helper)
    {
        _helper = helper;
    }

    /// <summary>
    /// Get the Game1.multiplayer instance.
    /// </summary>
    public Multiplayer GetMultiplayer()
    {
        return _helper.Reflection.GetField<Multiplayer>(typeof(Game1), "multiplayer").GetValue();
    }

    /// <summary>
    /// Get the SDK helper (Program._sdk).
    /// </summary>
    public SDKHelper? GetSdk()
    {
        return _helper.Reflection.GetField<SDKHelper>(typeof(Program), "_sdk").GetValue();
    }

    /// <summary>
    /// Check if networking is ready.
    /// </summary>
    public bool IsNetworkingReady()
    {
        var sdk = GetSdk();
        return sdk?.Networking != null;
    }

    /// <summary>
    /// Check if invite codes are supported.
    /// </summary>
    public bool SupportsInviteCodes()
    {
        var sdk = GetSdk();
        return sdk?.Networking?.SupportsInviteCodes() ?? false;
    }

    /// <summary>
    /// Get lobby from invite code.
    /// </summary>
    public object? GetLobbyFromInviteCode(string code)
    {
        var sdk = GetSdk();
        return sdk?.Networking?.GetLobbyFromInviteCode(code);
    }

    /// <summary>
    /// Create a client for a lobby.
    /// </summary>
    public Client? CreateClient(object lobby)
    {
        var sdk = GetSdk();
        return sdk?.Networking?.CreateClient(lobby);
    }

    /// <summary>
    /// Send a chat message to all players.
    /// </summary>
    public void SendChatMessage(string message)
    {
        GetMultiplayer().sendChatMessage(
            LocalizedContentManager.CurrentLanguageCode,
            message,
            Multiplayer.AllPlayers
        );
    }
}
