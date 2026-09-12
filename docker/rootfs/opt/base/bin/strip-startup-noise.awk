# Drops known-benign, non-actionable server startup noise from the attach-cli console, which
# tails /tmp/server-output.log through this filter. The on-disk /tmp/server-output.log and
# docker logs stay raw. Keep in sync with the copy in the other image's rootfs (legacy
# /opt/base/bin, modern /opt/bin).
#
#   1. "[S_API FAIL] ...SteamNetworkingUtils003 before SteamAPI_Init succeeded." —
#      Steamworks.NET's CSteamGameServerAPIContext.Init probes the client
#      networking-utils interface before falling back to the gameserver one inside
#      GameServer.Init (SteamGameServerService); the failed probe is cosmetic.
#   2. The "[ERROR game] Game.Initialize() caught exception initializing XACT."
#      block (header + exception + stack trace) — audio content is absent by design
#      on the headless server, so this failure is expected.
#   3. SMAPI's one-shot startup warnings acknowledging this server's deliberate config
#      (disabled content-integrity checks, disabled update checks, developer mode) —
#      accurate, but not actionable on a dedicated server.
#
# Matching is done on an ANSI-SGR-stripped copy so colorized and plain lines are
# handled alike; the original line is printed with its color intact, except that black
# text is recolored gray so trace logs stay readable on a dark pane. The XACT
# block ends at the next SMAPI log line (prefix mirrors ServerContainer
# .SmapiLogLinePrefix) or any unprefixed fatal-crash header the runtime writes without a
# SMAPI prefix — .NET FailFast ("Process terminated."), an unhandled exception, or a
# stack overflow — so a real crash landing in the XACT window is never swallowed. Every
# drop is an exact message match, never a level match, so real warnings/errors always
# pass through.
BEGIN { esc = sprintf("%c", 27); sgr = esc "[[][0-9;]*m"; black = esc "[[]30m"; gray = esc "[90m"; suppress = 0 }
{
    clean = $0
    gsub(sgr, "", clean)

    # A new SMAPI log line, or any unprefixed fatal-crash header (FailFast, an unhandled
    # exception, or a stack overflow), ends suppression and prints — so a crash landing
    # in the XACT window is never swallowed.
    if (clean ~ /^\[[0-9:]+[ \t]+[A-Za-z]+[ \t]+/ ||
        clean ~ /^Process terminated\./ ||
        clean ~ /^Unhandled [Ee]xception[.:]/ ||
        clean ~ /^Stack overflow\./) {
        suppress = 0
    } else if (suppress) {
        next
    }

    # Start dropping the headless-audio XACT error block (header included).
    if (clean ~ /^\[[0-9:]+[ \t]+ERROR[ \t]+game\][ \t]+Game\.Initialize\(\) caught exception initializing XACT/) {
        suppress = 1
        next
    }

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
