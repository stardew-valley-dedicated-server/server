# Password test classes share one server: three `KeepConnected` classes alongside an `Exclusive` one

Status: root-caused, fix is one attribute. Not applied.

`.claude/rules/test-broker-invariants.md` already forbids this pairing ("DO NOT use
`[TestServer(Exclusive = true)]` on classes sharing a server with KeepConnected"). Nothing enforces
it, and the password config violates it today.

## Root cause

`ComputeConfigHash` deliberately excludes `Clients` (`ResourceRequirements.cs:63-72`) and
`Isolation` defaults to `SharedClass` (`TestServerAttribute.cs:103-105`), which keys as
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

## Consequence

This is the CI trigger for `day-transition-wedge-with-lobby-player.md` — two of four full-suite runs
on 2026-08-03 died this way. Fixing it removes the collision in the suite; it does **not** fix the
underlying server bug (a real player sitting at the password prompt at 2am hits the same wedge).

## Fix

Give `LobbyHomedSpouseSteadyStateTests` its own instance. At
`tests/JunimoServer.Tests/LobbyHomedSpouseTests.cs:38-43`:

```csharp
[TestServer(
    Clients = 2,
    Password = "test-password-123",
    Isolation = IsolationMode.SharedGroup,
    SharedGroup = "lobby-homed-spouse",
    Exclusive = true
)]
```

`SharedGroup` keys as `group-{SharedGroup}-{hash}` (`ResourceRequirements.cs:38-45`), so the class
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
`.claude/rules/universal/preflight-check-vs-committed-config.md`.

A `Warn`-level prestart diagnostic naming the sharing classes is acceptable and cheap. The
hazardous combination to name is narrower than the rule's blanket wording: **an unauthenticated
session held on a server where another class drives day transitions.**

## Relationship to PR #494

None. Pure test configuration, unchanged by that PR, and independently landable.
