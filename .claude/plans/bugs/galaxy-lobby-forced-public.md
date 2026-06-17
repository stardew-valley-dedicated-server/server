# `CreateLobby_Prefix` forces the Galaxy lobby Public with 150 members regardless of server privacy

Status: open. Confirmed in current code. Scope clarified below — this affects the
**Galaxy/GOG** lobby path, not the Steam lobby privacy plumbing.

## Incident

`ServerOptimizerOverrides.CreateLobby_Prefix`
(`Services/ServerOptim/ServerOptimizerOverrides.cs:128-134`) is a Harmony prefix
on `GalaxySocket.CreateLobby(ref ServerPrivacy privacy, ref uint memberLimit)`
(patched at `ServerOptimizer.cs:109-115`). It overwrites both `ref` parameters
unconditionally:

```csharp
public static void CreateLobby_Prefix(ref ServerPrivacy privacy, ref uint memberLimit)
{
    // Used by GoG
    privacy = ServerPrivacy.Public;
    memberLimit = 150;
    _galaxyLobbyFailureCount = 0;
}
```

The original `privacy` argument is the game-chosen value derived from the host's
in-game visibility/invite setting. Vanilla `GalaxySocket.CreateLobby` passes it
through `privacyToLobbyType`
(`decompiled/sdv-1.6.15-24356/StardewValley/SDKs/GogGalaxy/GalaxySocket.cs:132-153`):
`InviteOnly → LOBBY_TYPE_PRIVATE`, `FriendsOnly → LOBBY_TYPE_FRIENDS_ONLY`,
`Public → LOBBY_TYPE_PUBLIC`. By forcing `ServerPrivacy.Public`, the prefix makes
the Galaxy lobby publicly discoverable even when the server is configured as
invite-only or friends-only.

## Scope (verified)

This is the **Galaxy/GOG** lobby only. The Steam path has separate, correct
privacy plumbing: `SteamGameServerNetServer.setPrivacy`
(`Services/SteamGameServer/SteamGameServerNetServer.cs:682-686`) forwards the
game-chosen `ServerPrivacy` to `GalaxyAuthService.SetSteamLobbyPrivacy`
(`Services/AuthService/AuthService.cs:742`), which honors it. So on a Steam-hosted
server the Steam lobby tracks the configured privacy; the Galaxy lobby created via
`GalaxySocket.CreateLobby` is the one forced Public.

The plan must first confirm, at sign-off, **whether the Galaxy lobby is
externally discoverable on this server's normal (Steam) deployment** or is a
vestigial/secondary path. If the Galaxy lobby is not reachable by outside players
on the shipped config, the severity is low and the fix is hygiene; if it is
discoverable, this is a genuine privacy violation (a "private" server exposing a
public Galaxy lobby). Per `runtime-post-conditions-are-gates.md`, do not assume —
check what the Galaxy lobby actually exposes before sizing the fix.

## Why the hardcode exists (don't blindly delete)

The `memberLimit = 150` raises the vanilla cap (Stardew's normal lobby cap is far
lower) to allow many farmhands — that part may be intentional and should be
preserved. The `_galaxyLobbyFailureCount = 0` reset is unrelated bookkeeping for
the retry-limiting `OnGalaxyLobbyCreated_Prefix` (`:143`) and must stay. Only the
`privacy = ServerPrivacy.Public` override is the bug.

## Fix

Stop overriding `privacy`; let the game-chosen value flow through. Keep the
member-limit raise and the failure-count reset:

```csharp
public static void CreateLobby_Prefix(ref ServerPrivacy privacy, ref uint memberLimit)
{
    // privacy flows through unchanged so an invite-only/friends-only server
    // does not expose a public Galaxy lobby.
    memberLimit = 150;
    _galaxyLobbyFailureCount = 0;
}
```

If the server's configured privacy is not already reaching `CreateLobby` as the
right `ServerPrivacy` (e.g. the dedicated-host path defaults it), trace where the
game sets the privacy argument and ensure the server-settings visibility maps to
it — mirror how the Steam path resolves privacy in `setPrivacy`. Per
`mirror-target-component-resolution.md`, the fix must reproduce the full privacy
resolution, not just remove the override and hope the default is correct.

## Verification

Per `runtime-post-conditions-are-gates.md` (a privacy claim is a runtime
observation):

1. Start a server configured **invite-only** (or friends-only). Confirm the
   created Galaxy lobby type is `LOBBY_TYPE_PRIVATE` / `LOBBY_TYPE_FRIENDS_ONLY`,
   not `LOBBY_TYPE_PUBLIC`.
2. Start a server configured **public** and confirm it is still publicly
   discoverable (the fix must not break the intended-public case).
3. Confirm `memberLimit` is still 150 and the Galaxy-retry limiting
   (`OnGalaxyLobbyCreated_Prefix`) still resets/behaves as before.
4. Confirm the Steam lobby privacy (via `setPrivacy` → `SetSteamLobbyPrivacy`) is
   unaffected.

## Related files

| File | Role |
| --- | --- |
| `Services/ServerOptim/ServerOptimizerOverrides.cs:128-134` | `CreateLobby_Prefix` — the unconditional `Public`/150 override |
| `Services/ServerOptim/ServerOptimizer.cs:109-115` | registers the patch on `GalaxySocket.CreateLobby` |
| `decompiled/.../SDKs/GogGalaxy/GalaxySocket.cs:132-153` | `privacyToLobbyType` + `CreateLobby` — what `privacy` controls |
| `Services/SteamGameServer/SteamGameServerNetServer.cs:682-686` | `setPrivacy` — the Steam path that already honors privacy (don't touch) |
| `Services/AuthService/AuthService.cs:742` | `SetSteamLobbyPrivacy` — Steam lobby privacy plumbing |
