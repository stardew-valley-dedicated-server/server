namespace Diagnostics;

/// <summary>
/// What the collection run observed about the server. Lets the report explain an empty snapshot
/// (server still starting vs no save loaded) instead of showing bare "unknown" everywhere.
/// </summary>
internal enum ServerState
{
    /// <summary>Listener answered; live data is present (may still be pre-save — see NoWorldLoaded).</summary>
    Reachable,

    /// <summary>Every read failed to connect — the listener isn't up yet (server still starting).</summary>
    NotAccepting,

    /// <summary>Listener up, but no save is loaded (booting, or a runtime day/farm-map change).</summary>
    NoWorldLoaded,
}

internal static class ServerStateText
{
    /// <summary>The per-state caption for empty live sections, phrased to fit mid-sentence.</summary>
    public static string UnavailableReason(this ServerState state) =>
        state switch
        {
            ServerState.NotAccepting => "server still starting",
            ServerState.NoWorldLoaded =>
                "no save loaded — the server is booting or between saves (e.g. a day transition or farm-map change)",
            _ => "not available",
        };
}
