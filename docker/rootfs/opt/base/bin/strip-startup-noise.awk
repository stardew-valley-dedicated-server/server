# Drops known-benign, non-actionable server startup noise from the attach-cli console, which
# tails /tmp/server-output.log through this filter. The on-disk /tmp/server-output.log and
# docker logs stay raw. The modern image (docker/modern/rootfs/opt/bin) carries its own copy,
# which additionally drops the headless-audio XACT error block its stock SMAPI still prints;
# this image's SMAPI build suppresses that line at the source (patches/smapi).
#
#   1. "[S_API FAIL] ...SteamNetworkingUtils003 before SteamAPI_Init succeeded." —
#      Steamworks.NET's CSteamGameServerAPIContext.Init probes the client
#      networking-utils interface before falling back to the gameserver one inside
#      GameServer.Init (SteamGameServerService); the failed probe is cosmetic.
#   2. SMAPI's one-shot startup warnings acknowledging this server's deliberate config
#      (disabled content-integrity checks, disabled update checks, developer mode) —
#      accurate, but not actionable on a dedicated server.
#
# Matching is done on an ANSI-SGR-stripped copy so colorized and plain lines are
# handled alike; the original line is printed with its color intact, except that black
# text is recolored gray so trace logs stay readable on a dark pane. Every drop is an
# exact single-line message match, never a level match, so real warnings/errors always
# pass through.
BEGIN { esc = sprintf("%c", 27); sgr = esc "[[][0-9;]*m"; black = esc "[[]30m"; gray = esc "[90m" }
{
    clean = $0
    gsub(sgr, "", clean)

    # Drop the single benign native Steam interface-probe line.
    if (clean ~ /\[S_API FAIL\] Tried to access Steam interface SteamNetworkingUtils003 before SteamAPI_Init succeeded\./) {
        next
    }

    # Drop SMAPI's one-shot startup warnings acknowledging this server's deliberate config.
    if (clean ~ /You disabled content integrity checks/ ||
        clean ~ /You disabled update checks, so you won't be notified/ ||
        clean ~ /You enabled developer mode, so the console will be much more verbose/) {
        next
    }

    gsub(black, gray)
    print
    fflush()
}
