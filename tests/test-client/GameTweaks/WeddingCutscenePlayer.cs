using JunimoServer.Shared;
using StardewModdingAPI;
using StardewValley;
using StardewValley.Menus;

namespace JunimoTestClient.GameTweaks;

/// <summary>
/// Drives a wedding ceremony through to completion on a test client the honest way a real player does —
/// by clicking through each <c>speak</c> dialogue box — so the cutscene actually PLAYS and RENDERS
/// frame-by-frame instead of being force-skipped.
///
/// <para>
/// <b>Why this exists.</b> A wedding is an <see cref="Event"/> (id "-2") that every instance runs its
/// own copy of (weddings aren't a synced event — each client pops it from the synced
/// <c>Game1.weddingsToday</c> via <c>checkForEvents</c>). The cutscene advances time-based EXCEPT at a
/// <c>speak</c> command: <c>Event.Speak</c> opens a <c>DialogueBox</c>, sets <c>Game1.dialogueUp</c>,
/// and the event will not advance while a box is open (decompiled Event.cs Speak: early-returns while
/// <c>dialogueUp</c>). The test client has no human at the mouse, so without an auto-clicker the
/// ceremony parks on its first dialogue box forever. The old approach (<c>Event.skipEvent()</c>) jumped
/// straight to the end behaviours — which means the ceremony was never visibly rendered. This tweak
/// clicks the box each tick so the cutscene plays through to its <c>waitForOtherPlayers</c> gate, which
/// then auto-readies (vanilla <c>WaitForOtherPlayers</c> calls <c>SetLocalReady</c> on arrival). The
/// host completes its own slot of that gate (AlwaysOn.HandleWeddingEvent), so the whole wedding ends.
/// </para>
///
/// <para>
/// <b>Deliberate visible pauses.</b> So the recorded client video makes it obvious WHAT is happening,
/// the auto-clicker holds off at key beats: when a ceremony starts (the couple + guests assembled) and
/// at the final dialogue (the "I now pronounce you…" moment, just before the wait gate). These pauses
/// are wall-clock holds in the tick handler — the world keeps drawing during them, so they show up as
/// several seconds of the assembled scene in the video (<see cref="BeatPauseMs"/>).
/// </para>
///
/// <para>
/// <b>Render proof.</b> Each distinct ceremony (keyed by its <c>weddingEnd&lt;farmerId&gt;</c> gate) is
/// recorded with the groom + spouse (see <see cref="GetRenderedSnapshot"/>), exposed via the test
/// client's <c>/status</c> so the E2E test can assert each client rendered BOTH same-day ceremonies —
/// the thing the host-side spouse-warp signal can't prove (that warp is master-only, Event.cs "wedding"
/// case).
/// </para>
/// </summary>
public class WeddingCutscenePlayer
{
    private readonly IMonitor _monitor;

    // Wall-clock hold at each "make it obvious" beat — long enough that the assembled scene is clearly
    // visible in the recording, short enough not to pad the run (two beats per ceremony × two ceremonies).
    // The world keeps rendering during the hold (we just don't click).
    private const int BeatPauseMs = 1500;

    // The gate id of the ceremony currently being driven, so a new gate is detected as a new ceremony.
    private string? _activeGate;

    // The ceremony currently being driven, captured when it starts and committed to _renderedCeremonies
    // only when it COMPLETES. Recording on completion — not start — keeps the /status render count honest:
    // the count reaches 2 once both cutscenes have actually played through, not the instant the second one
    // begins. Null between ceremonies.
    private RenderedCeremony? _activeCeremony;

    // Whether the active ceremony's cutscene has played through to its waitForOtherPlayers gate. This is
    // the gate for committing a ceremony as complete — NOT a transient CurrentEvent==null. On a
    // back-to-back same-day wedding the player warps Town→Farm (ceremony 1 exit) then Farm→Town (ceremony
    // 2 entry), and CurrentEvent reads null mid-warp for a tick while ceremony 2 is in fact still running.
    // Committing on that null abandoned ceremony 2 (marked it "rendered" the instant it started, so the
    // auto-clicker stopped driving it), stalling it until the host's wall-clock backstop force-ended it
    // ~39s later. Latching on HasReachedWaitGate instead means we only treat a null as "ended" once we
    // actually drove the cutscene to its gate.
    private bool _activeGateReached;

    // Wall-clock instant until which the auto-clicker pauses (a visible beat). DateTime.MinValue = no
    // active pause. Wall-clock, not game-time: time is frozen during a wedding, so a game-time timer
    // would never elapse.
    private DateTime _pauseUntilUtc = DateTime.MinValue;

    // Whether we've already inserted the pre-gate ("marriage pronounced") pause for the active gate, so
    // it fires once per ceremony rather than every tick we sit on the final command.
    private bool _finalBeatPausedForActiveGate;

    // Wall-clock instant a ceremony last ended (CurrentEvent went wedding→not-wedding). Lets the warp
    // handler add a visible "just teleported home" pause right after the ceremony's exit warp.
    private DateTime _lastCeremonyEndedUtc = DateTime.MinValue;

    // How long after a ceremony ends we still treat a local warp as "the wedding exit warp".
    private const int CeremonyEndWarpWindowMs = 4000;

    // One entry per distinct wedding ceremony this client played through (keyed by gate id). Written on
    // the game thread (OnUpdateTicked), read off it (/status handler) — so guard both with _renderLock
    // to avoid a torn read / "collection modified" during the snapshot.
    private readonly Dictionary<string, RenderedCeremony> _renderedCeremonies = new();
    private readonly object _renderLock = new();

    public WeddingCutscenePlayer(IMonitor monitor)
    {
        _monitor = monitor;
    }

    /// <summary>
    /// Snapshot of the wedding ceremonies this client has played through (rendered) today, one per gate.
    /// Read by <c>/status</c> (off the game thread) for the E2E "both ceremonies rendered on this client"
    /// assertion. Returns a copy so the caller can't observe a concurrent mutation.
    /// </summary>
    public List<RenderedCeremony> GetRenderedSnapshot()
    {
        lock (_renderLock)
        {
            return new List<RenderedCeremony>(_renderedCeremonies.Values);
        }
    }

    private bool AlreadyRendered(string gate)
    {
        lock (_renderLock)
        {
            return _renderedCeremonies.ContainsKey(gate);
        }
    }

    /// <summary>Clear the render record for a new session. Call on SaveLoaded (join / new game), NOT on
    /// DayStarted: same-day weddings fire from <c>checkForEvents</c> on location entry as the day-start
    /// warps resolve, while <c>OnDayStarted</c> fires on its own tick conditions — there's no ordering
    /// guarantee between them, so a DayStarted reset can wipe a ceremony already counted this day (seen:
    /// reset logged 1s AFTER the first ceremony started). SaveLoaded fires once per session, far from any
    /// wedding day, so it can't race the record it clears.</summary>
    public void ResetForNewSession()
    {
        lock (_renderLock)
        {
            if (_renderedCeremonies.Count > 0)
            {
                _monitor.Log(
                    "[Wedding] New session — clearing rendered-ceremony record.",
                    LogLevel.Trace
                );
            }
            _renderedCeremonies.Clear();
            _activeGateReached = false;
        }
        _activeGate = null;
        _activeCeremony = null;
        _pauseUntilUtc = DateTime.MinValue;
        _finalBeatPausedForActiveGate = false;
    }

    /// <summary>
    /// Per-tick driver. Call from <c>UpdateTicked</c> on the game thread. No-op unless a wedding event
    /// is up on this client.
    /// </summary>
    public void OnUpdateTicked()
    {
        if (!Context.IsWorldReady)
        {
            return;
        }

        var ev = Game1.CurrentEvent;
        if (ev is not { isWedding: true })
        {
            // No wedding event up right now. This is only a REAL ceremony end if we already drove the
            // active ceremony to its wait gate (_activeGateReached). Otherwise it's the transient
            // CurrentEvent==null that occurs mid-warp during a back-to-back same-day wedding's
            // Town↔Farm exit/entry — the event is still running and about to resume, so committing now
            // would prematurely mark the ceremony "rendered" and stop us driving it (see _activeGateReached).
            if (_activeGate != null && _activeGateReached)
            {
                CommitActiveCeremony();
                _lastCeremonyEndedUtc = DateTime.UtcNow;
                _activeGate = null;
                _activeCeremony = null;
                _activeGateReached = false;
                _pauseUntilUtc = DateTime.MinValue;
                _finalBeatPausedForActiveGate = false;
            }
            // If the gate wasn't reached yet, leave the active-ceremony state intact so driving resumes
            // when CurrentEvent comes back on the next tick — don't reset anything here.
            return;
        }

        var gate = WeddingEvent.ExtractWaitGate(ev);
        if (gate == null)
        {
            return; // no wait gate in this event's commands (unexpected) — nothing to anchor on
        }

        // New ceremony (first time we've seen this gate)? Capture it (committed to the render record only
        // when it completes) and open with a visible beat so the assembled couple is on screen before we
        // start clicking through the dialogue. Guarded on AlreadyRendered, not just gate != _activeGate:
        // CurrentEvent can briefly flicker wedding→null→same wedding across the exit warp / back-to-back
        // transition, and we don't want to re-fire the STARTED log + beat (or reset the final-beat latch)
        // for a ceremony already completed.
        if (gate != _activeGate && !AlreadyRendered(gate))
        {
            // A genuinely different gate means the previous ceremony is over (the engine fired the next
            // queued wedding). Commit the previous one now — its render count would be lost if
            // CurrentEvent flipped straight here without ever reading null.
            _activeGateReached = false;
            CommitActiveCeremony();
            _activeGate = gate;
            _finalBeatPausedForActiveGate = false;
            CaptureCeremonyStarted(ev, gate);
            BeginBeatPause("ceremony started (couple assembled)");
            return;
        }

        // Re-detected the gate we're already driving (or one already rendered). Latch once the cutscene
        // has played through to its wait gate — the signal the CurrentEvent==null branch uses to tell a
        // real ceremony end from a transient mid-warp flicker — then keep clicking it forward.
        if (!_activeGateReached && WeddingEvent.HasReachedWaitGate(ev))
        {
            _activeGateReached = true;
        }
        _activeGate = gate;

        // Holding on a visible beat — let the scene render, don't click yet.
        if (DateTime.UtcNow < _pauseUntilUtc)
        {
            return;
        }
        _pauseUntilUtc = DateTime.MinValue;

        // No dialogue box up: the cutscene is advancing on its own (move/pause commands). Let it.
        if (Game1.activeClickableMenu is not DialogueBox box)
        {
            return;
        }

        // A dialogue box is up. If this is the LAST speak before the wait gate (the "marriage
        // pronounced" beat), pause once so it's obvious in the video, then resume clicking next tick.
        if (!_finalBeatPausedForActiveGate && IsAtFinalDialogue(ev))
        {
            _finalBeatPausedForActiveGate = true;
            BeginBeatPause("marriage pronounced (final dialogue)");
            return;
        }

        // Click the box the way a player does. The box's own gating handles timing: a first click
        // completes the typed text, a later click (once its safetyTimer elapses) advances/closes it —
        // so calling this each tick walks the dialogue forward without us re-implementing the timers
        // (DialogueBox.receiveLeftClick, decompiled DialogueBox.cs).
        box.receiveLeftClick(0, 0, playSound: false);
    }

    /// <summary>
    /// If a ceremony just ended (the local player is warping out of it via the wedding's exit warp),
    /// hold the player still for a beat so the recorded video lingers on the "teleported home" arrival.
    /// Call from the local-player Warped handler. No-op outside the post-ceremony window.
    /// </summary>
    public void PauseAfterWeddingWarp()
    {
        if ((DateTime.UtcNow - _lastCeremonyEndedUtc).TotalMilliseconds > CeremonyEndWarpWindowMs)
        {
            return;
        }
        // pauseTime freezes the player but the world keeps drawing, so the arrival shows in the video.
        // Only bump it (don't stack) so two same-day ceremonies don't compound into one long freeze.
        if (Game1.pauseTime < BeatPauseMs)
        {
            Game1.pauseTime = BeatPauseMs;
        }
        _monitor.Log(
            $"[Wedding] visible beat — pausing {BeatPauseMs}ms after the ceremony exit warp (teleported home).",
            LogLevel.Info
        );
    }

    /// <summary>True once the event has reached its final command — the <c>waitForOtherPlayers</c> gate
    /// is the last thing left, so the dialogue currently up is the ceremony's closing line.</summary>
    private static bool IsAtFinalDialogue(Event ev)
    {
        var commands = ev.eventCommands;
        if (commands == null || commands.Length == 0)
        {
            return false;
        }
        // The wait gate is the terminal command in the vanilla wedding script; if we're on (or past)
        // the second-to-last command with a dialogue up, this is the closing line.
        return ev.CurrentCommand >= commands.Length - 2;
    }

    /// <summary>Capture the just-started ceremony into <see cref="_activeCeremony"/>. Not yet added to
    /// the rendered record — that happens in <see cref="CommitActiveCeremony"/> when the ceremony ends.</summary>
    private void CaptureCeremonyStarted(Event ev, string gate)
    {
        // ev.farmer is the wedding's groom (falls back to Game1.player, never null — decompiled
        // Event.farmer getter). For the OTHER couple's ceremony on this client it's that other farmer.
        var groom = ev.farmer;
        var spouse = groom.spouse ?? "(unknown)";
        var isLocal = groom.UniqueMultiplayerID == Game1.player?.UniqueMultiplayerID;

        _activeCeremony = new RenderedCeremony
        {
            Gate = gate,
            GroomId = groom.UniqueMultiplayerID,
            GroomName = groom.Name ?? "(unknown)",
            Spouse = spouse,
            IsLocalPlayer = isLocal,
        };

        _monitor.Log(
            $"[Wedding] ceremony STARTED gate={gate} groom={groom.Name}({groom.UniqueMultiplayerID}) "
                + $"spouse={spouse} local={isLocal} localPlayer={Game1.player?.UniqueMultiplayerID} "
                + $"weddingsTodayRemaining={Game1.weddingsToday?.Count}",
            LogLevel.Info
        );
    }

    /// <summary>Commit the active (now-finished) ceremony to the rendered record. Called when the wedding
    /// event ends, so the /status render count reflects ceremonies actually PLAYED THROUGH, not just
    /// started. Idempotent: a CurrentEvent flicker that re-enters here finds the gate already recorded.</summary>
    private void CommitActiveCeremony()
    {
        var ceremony = _activeCeremony;
        if (ceremony == null)
        {
            return;
        }

        _activeCeremony = null;

        int renderedCount;
        lock (_renderLock)
        {
            _renderedCeremonies[ceremony.Gate] = ceremony;
            renderedCount = _renderedCeremonies.Count;
        }

        _monitor.Log(
            $"[Wedding] ceremony COMPLETED gate={ceremony.Gate} groom={ceremony.GroomName}({ceremony.GroomId}) "
                + $"spouse={ceremony.Spouse} local={ceremony.IsLocalPlayer} "
                + $"renderedSoFar={renderedCount}",
            LogLevel.Info
        );
    }

    private void BeginBeatPause(string reason)
    {
        _pauseUntilUtc = DateTime.UtcNow.AddMilliseconds(BeatPauseMs);
        _monitor.Log(
            $"[Wedding] visible beat — pausing {BeatPauseMs}ms so it shows in the video: {reason}.",
            LogLevel.Info
        );
    }
}

/// <summary>One wedding ceremony a client played through and rendered, for the /status render proof.</summary>
public class RenderedCeremony
{
    public string Gate { get; set; } = "";
    public long GroomId { get; set; }
    public string GroomName { get; set; } = "";
    public string Spouse { get; set; } = "";

    /// <summary>True if the groom is this client's own player (vs the other couple's ceremony).</summary>
    public bool IsLocalPlayer { get; set; }
}
