# Password test classes share one server: three `KeepConnected` classes alongside an `Exclusive` one

**Status:** ready-to-implement
**Priority:** 2 (medium)
**GitHub Issue(s):** none
**Area:** tests
**Related:** [PR #494](https://github.com/stardew-valley-dedicated-server/server/pull/494); [`day-transition-wedge-with-lobby-player.md`](day-transition-wedge-with-lobby-player.md)
**Observed:** 2 of 4 full-suite runs on 2026-08-03 died this way (`config-21937f75bcf8`)
**Next step:** add the `SharedGroup` attribute to `LobbyHomedSpouseSteadyStateTests` and check the extra instance against host `serverSlots`

## Symptom

`.claude/rules/test-broker-invariants.md` already forbids this pairing ("DO NOT use
`[TestServer(Exclusive = true)]` on classes sharing a server with KeepConnected"). Nothing enforces
it, and the password config violates it today.

This pairing is what exposed the lobby-player day-transition wedge — two of four full-suite runs
on 2026-08-03 died with a lobby session parked on a server where another class drove a night.
That server bug is fixed (`LobbyService.BarrierReady_Postfix` checks in at every new-day barrier
on behalf of excluded players, for the vanilla farmhands' benefit), so the pairing no longer
wedges a server; it remains an invariant violation that lets one class's held session sit inside
another class's night.

## Root cause

`ComputeConfigHash` deliberately excludes `Clients` (`ResourceRequirements.cs`) and
`Isolation` defaults to `SharedClass` (`TestServerAttribute.cs`), which keys as
`config-{hash}` — the same form `SharedAssembly` produces. So every class with
`Password = "test-password-123"` and default farm/cabins/strategy lands on **one** server instance
(`config-21937f75bcf8`, label `lan+pw-farm0-CabinStack-c30`, 33 tests in the 2026-08-03 runs):

| Class | Attribute |
|---|---|
| `PasswordProtectionTests` | `KeepConnected` — parks an **unauthenticated** lobby session |
| `LobbyCommandsCRUDTests` | `KeepConnected` |
| `LobbyCommandsEditingTests` | `KeepConnected` |
| `LobbyCommandsPermissionsTests` | plain |
| `PasswordProtectionDisruptiveTests` | plain |
| `LobbyHomedSpouseSteadyStateTests` | **`Exclusive`**, drives day transitions |

`Exclusive` serializes a class's own methods and holds the server between them; it does not evict a
`KeepConnected` session already connected to that instance. So a lobby client can sit inside another
class's night.

**The cross-class sharing itself is by design, not the defect.** `test-broker-invariants.md`:
"same config produces the same server key, regardless of `SharedAssembly` vs `SharedClass`
lifetime — the broker reuses an existing matching server." Isolation governs server *lifetime and
reuse*, not exclusivity, and pooling by config is what keeps the run inside its per-host
`serverSlots`. Do not "fix" this by adding the test class to the `SharedClass` key: 14 of 33 test
classes are `SharedClass` (some explicitly, some by default), so that would split their pooling into
per-class servers at ~41s boot apiece. The defect is narrower — an *unauthenticated* session held on
a server where another class drives day transitions.

## Fix

Give `LobbyHomedSpouseSteadyStateTests` its own instance. On the class attribute in
`tests/JunimoServer.Tests/LobbyHomedSpouseTests.cs`:

```csharp
[TestServer(
    Clients = 2,
    Password = "test-password-123",
    Isolation = IsolationMode.SharedGroup,
    SharedGroup = "lobby-homed-spouse",
    Exclusive = true
)]
```

`SharedGroup` keys as `group-{SharedGroup}-{hash}` (`ResourceRequirements.cs`), so the class
gets its own server while keeping its password semantics. One attribute added.

Cost: one more server instance for the run. Check it against host `serverSlots` before landing —
`.claude/rules/provision-up-front-when-startup-exceeds-serviceable-tail.md` covers the capacity
trade-off.

## Do NOT add a broker-level throw for `Exclusive` + `KeepConnected`

The same pairing exists on the 90-test non-password config: `NoPasswordTests` is `KeepConnected`
while `CabinPlacementValidationTests`, `CabinStrategyFarmhouseStackTests`, `CabinStrategyNoneTests`,
`FarmMapTypeTests` and `RenderingTests` are `Exclusive`. Those held sessions are **authenticated**,
so they participate in barriers normally and the pairing is harmless there. A fail-fast would reject
the committed config on its first run — see
`.claude/rules/universal/verify-claims.md`.

A `Warn`-level prestart diagnostic naming the sharing classes is acceptable and cheap. The
hazardous combination to name is narrower than the rule's blanket wording: **an unauthenticated
session held on a server where another class drives day transitions.**

## Relationship to PR #494

None. Pure test configuration, unchanged by that PR, and independently landable.
