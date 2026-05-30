# `!changewallet` mid-session safety

## Context

`mod/JunimoServer/Services/Commands/ChangeWalletCommand.cs` registers an admin-only `!changewallet` chat command that flips between shared and separate wallets by calling `ManorHouse.MergeWallets()` / `ManorHouse.SeparateWallets()` directly. The command runs immediately on the game thread with no checks for game state. Two failure modes are reachable from this:

1. **Day-transition race.** `Game1.cs:7990` and `Game1.cs:8036` (overnight shipping bin processing) branch on `team.useSeparateWallets.Value`. Flipping mid-overnight routes shipping items down the wrong path → host loses items or per-player wallets desync.
2. **Festival / event race.** Remote players may have a `ShopMenu` / `MoneyDial` open that reads money via whichever code path was current when the menu opened; flipping mid-event surprises those menus.

Two smaller issues exist alongside:

3. **Silent no-op.** Vanilla `MergeWallets` / `SeparateWallets` early-return when already in target state (`decompiled/sdv-1.6.15-24356/StardewValley/Locations/ManorHouse.cs:296-322, 324-342`). The current command reports `"Now using shared/separate money."` regardless, misleading the admin.
4. **Duplicate chat broadcast.** Vanilla emits `globalChatInfoMessage("MergedWallets")` / `("SeparatedWallets")`. The mod's `SendPublicMessage("Now using shared/separate money.")` routes through the same `Multiplayer.sendChatMessage` channel (`mod/JunimoServer/Util/ModHelperExtensions.cs:43-46`) — a duplicate of vanilla's chat line.
5. **Footgun ergonomics.** A bare `!changewallet` is a destructive toggle with no confirmation. The codebase has a precedent for confirmation phrases on destructive admin commands at `mod/JunimoServer/Services/Commands/JojaCommand.cs:23` (`IRREVERSIBLY_ENABLE_JOJA_RUN`).

The intended outcome: `!changewallet` cannot fire during unsafe states, requires an explicit direction arg, reports correctly when it's a no-op, and stops emitting duplicate chat.

## Approach

Single-file edit to `mod/JunimoServer/Services/Commands/ChangeWalletCommand.cs`. No new infrastructure — no cooldown service, no command-state tracker.

### Behavior

1. **Explicit-direction args.** `!changewallet TO_SHARED` and `!changewallet TO_SEPARATE`. A bare `!changewallet` (or any other arg) prints usage privately and does nothing. Matches the `JojaCommand` confirmation-phrase precedent.
2. **Admin gate** (unchanged): `roleService.IsPlayerAdmin(msg.SourceFarmer)`.
3. **Unsafe-state gate.** Block when any of:
    - `Game1.newDaySync != null && Game1.newDaySync.hasInstance() && !Game1.newDaySync.hasFinished()`
    - `Game1.isFestival()`
    - `Game1.eventUp`
      When blocked: send a private message and do nothing. The day-transition predicate matches the in-tree usage at `mod/JunimoServer/Services/PasswordProtection/PasswordProtectionService.cs:672`.
4. **Idempotency check.** If `Game1.player.team.useSeparateWallets.Value` already matches the requested target, send a private no-op message and don't call the vanilla static.
5. **Drop the `SendPublicMessage` lines.** Vanilla's `globalChatInfoMessage` is the canonical broadcast; the mod's lines are duplicates.

### Target method body

```csharp
chatCommandsService.RegisterCommand("changewallet",
    "Type \"!changewallet TO_SHARED\" or \"!changewallet TO_SEPARATE\" to toggle wallet mode.",
    (args, msg) =>
    {
        if (!roleService.IsPlayerAdmin(msg.SourceFarmer))
        {
            helper.SendPrivateMessage(msg.SourceFarmer, "You are not an admin.");
            return;
        }

        bool wantSeparate;
        if (args.Length == 1 && args[0] == "TO_SEPARATE") wantSeparate = true;
        else if (args.Length == 1 && args[0] == "TO_SHARED") wantSeparate = false;
        else
        {
            helper.SendPrivateMessage(msg.SourceFarmer,
                "Usage: !changewallet TO_SHARED  or  !changewallet TO_SEPARATE");
            return;
        }

        var dayTransitionActive =
            Game1.newDaySync != null && Game1.newDaySync.hasInstance() && !Game1.newDaySync.hasFinished();
        if (dayTransitionActive || Game1.isFestival() || Game1.eventUp)
        {
            helper.SendPrivateMessage(msg.SourceFarmer,
                "Cannot change wallet mode during a day transition, festival, or event. Try again later.");
            return;
        }

        var alreadySeparate = Game1.player.team.useSeparateWallets.Value;
        if (wantSeparate == alreadySeparate)
        {
            helper.SendPrivateMessage(msg.SourceFarmer,
                wantSeparate ? "Wallets are already separate." : "Wallets are already shared.");
            return;
        }

        if (wantSeparate) ManorHouse.SeparateWallets();
        else ManorHouse.MergeWallets();
    });
```

No `LogLevel.Error` (per `.claude/rules/debugging.md`). No comments narrating the change (per `.claude/rules/universal/no-refactor-history-in-code.md`). No new `using` directives needed.

### Admin-visible messages by branch

| Branch                  | Recipient | Message                                                                                   |
| ----------------------- | --------- | ----------------------------------------------------------------------------------------- |
| Not admin               | private   | "You are not an admin."                                                                   |
| Bad / missing args      | private   | "Usage: !changewallet TO_SHARED or !changewallet TO_SEPARATE"                             |
| Unsafe state            | private   | "Cannot change wallet mode during a day transition, festival, or event. Try again later." |
| Already in target state | private   | "Wallets are already shared." or "Wallets are already separate."                          |
| Success                 | public    | (vanilla `MergedWallets` / `SeparatedWallets` only)                                       |

## Critical files

- **Edit:** `mod/JunimoServer/Services/Commands/ChangeWalletCommand.cs` (the entire `Register` body).
- **Reference (read-only):**
    - `mod/JunimoServer/Services/PasswordProtection/PasswordProtectionService.cs:672` — `newDaySync` predicate pattern.
    - `mod/JunimoServer/Services/Commands/JojaCommand.cs:23-28` — confirmation-phrase pattern.
    - `mod/JunimoServer/Util/ModHelperExtensions.cs:43-53` — `SendPublicMessage` / `SendPrivateMessage`.
    - `decompiled/sdv-1.6.15-24356/StardewValley/Locations/ManorHouse.cs:296-342` — vanilla statics being called.

No changes to public API, DI registration, or config schema.

## Verification

**Manual smoke (primary):**

1. Boot a server with at least one farmhand connected. As admin at 9am with no event/festival, run `!changewallet TO_SEPARATE`. Expect: vanilla `SeparatedWallets` chat line; `team.useSeparateWallets.Value` is `true`; per-farmer money is `total / N` (floor).
2. Re-run `!changewallet TO_SEPARATE`. Expect: private "Wallets are already separate."; no public chat.
3. Run `!changewallet TO_SHARED`. Expect: vanilla `MergedWallets` chat line; flag flips back; sum of pre-merge per-farmer money equals new shared total.
4. Trigger sleep on all players; while overnight processing is running (`newDaySync.hasInstance()` true), have a queued admin chat command fire `!changewallet TO_SEPARATE`. Expect: private unsafe-state message; flag unchanged after the day completes.
5. Warp the host into an active festival; run the command. Expect: private unsafe-state message.
6. Run `!changewallet` (no arg) and `!changewallet FOO`. Expect: private usage message in both cases; no toggle.

**Build verification:**

- `dotnet build mod/JunimoServer/JunimoServer.csproj` clean.

**Integration test (optional):**
The repo has E2E scaffolding for chat-driven commands in `tests/JunimoServer.Tests/`. A new test class would drive the four blocked branches and the success branch via `ChatWatcher`, asserting `useSeparateWallets.Value` after each. Per `CLAUDE.md`, this layer is integration-only. Defer unless requested; manual smoke covers the gate.
