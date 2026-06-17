# Issue #2 — Players see/select farmhands they don't own

**Verdict:** ❌ still present (by design, blocked on platform-ID mapping).
**Action:** keep open; post a root-cause comment with `file:line`.

## Root cause

`mod/JunimoServer/Services/AuthService/FarmhandSenderService.cs:496-506` —
`IsFarmhandSelectableByUserId` takes the joining player's `userId` but ignores it
for owned farmhands:

```csharp
// OWNED: show to all clients, authCheck() during join handles verification
return true;
```

The method's doc comment (`:490-495`) names why this is deliberate: farmhands are
tied to players via platform `userID` only for Steam/GOG connections (IP
connections bypass it entirely), and Steam SDR provides a Steam ID while
`farmhand.userID` may be a Galaxy ID — different ID spaces, so a reliable
per-user match at send time isn't possible today.

So every player still **sees** all other players' farmhands in the co-op join
menu (the selectability filter at `:277` passes them through); they merely can't
**join** one, because vanilla `authCheck` rejects the join. The reported symptom
— seeing/selecting farmhands you don't own — is unchanged.

## Fix surface

Per-`userId` filtering at send time (the `isSelectable` filter feeding
`sendAvailableFarmhands`), blocked on the still-open Steam/GOG ownership-ID
mapping: a fix needs a way to compare the joining client's platform ID against
`farmhand.userID` across the Steam-vs-Galaxy ID-space split before any
filtering can be correct.

## Task

1. Comment on #2: root cause (`FarmhandSenderService.cs:496-506`), the
   see-vs-join distinction, and the ID-mapping blocker.
2. Keep open. No code change until the ownership-ID mapping exists.
