using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;
using JunimoServer.Services.Diagnostics;
using StardewModdingAPI;
using StardewModdingAPI.Events;

namespace JunimoServer.Services.GameThread;

/// <summary>
/// Marshals discrete actions from background threads (HTTP handlers, scheduled jobs) onto the
/// game thread and completes a <see cref="Task"/> with the action's outcome. Owns its own
/// <c>UpdateTicked</c> pump so it drains regardless of which consumers are enabled
/// (<c>ApiService.Entry</c> returns before subscribing anything when <c>API_ENABLED=false</c>).
/// Draining happens in <c>UpdateTicked</c>, not <c>UnvalidatedUpdateTicked</c>: SMAPI suppresses
/// it while a save is in progress, which keeps mutations out of save data. Read-only API
/// endpoints serve from a periodic snapshot instead of queuing here.
/// </summary>
public class GameThreadDispatcher : ModService
{
    /// <summary>
    /// A queued game-thread action whose execution and cancellation compete for an atomic claim:
    /// the game thread claims pending→executing before running, the cancellation callback claims
    /// pending→canceled before faulting. Exactly one side wins, so a timed-out or canceled
    /// request can never mutate the world after its caller was told nothing changed, and an
    /// in-flight action is never reported as canceled (the caller awaits its real result
    /// instead). Must be a reference type — the claim state has to be shared between the queue
    /// and the cancellation callback, not copied per dequeue. Same claim pattern as the test
    /// client's <c>ExecuteOnGameThread</c> (tests/test-client/ModEntry.cs); keep the two in sync.
    /// </summary>
    private sealed class PendingGameAction
    {
        // 0 = pending, 1 = canceled (execution must skip), 2 = executing (cancel must not land).
        private int _state;

        public PendingGameAction(Action action, TaskCompletionSource<bool> completion)
        {
            Action = action;
            Completion = completion;
        }

        public Action Action { get; }
        public TaskCompletionSource<bool> Completion { get; }

        public bool TryClaimExecution() => Interlocked.CompareExchange(ref _state, 2, 0) == 0;

        public bool TryClaimCancel() => Interlocked.CompareExchange(ref _state, 1, 0) == 0;
    }

    private readonly ConcurrentQueue<PendingGameAction> _pending = new();

    /// <summary>Number of actions queued and not yet drained. Read from any thread.</summary>
    public int PendingCount => _pending.Count;

    /// <summary>
    /// Raised on the game thread after a drain pass that executed at least one action, so
    /// consumers can refresh state derived from the mutated world without waiting a tick.
    /// </summary>
    public event Action? Drained;

    public GameThreadDispatcher(IModHelper helper, IMonitor monitor)
        : base(helper, monitor) { }

    public override void Entry()
    {
        Helper.Events.GameLoop.UpdateTicked += OnUpdateTicked;
    }

    /// <summary>
    /// Queues <paramref name="action"/> for the game thread and waits for it to complete. Throws
    /// <see cref="TaskCanceledException"/> if the action is still queued when
    /// <paramref name="timeout"/> elapses; an action the game thread has already started runs to
    /// completion and reports its real result. Request-style calls use this overload.
    /// </summary>
    public async Task RunAsync(Action action, TimeSpan timeout)
    {
        using var cts = new CancellationTokenSource(timeout);
        await RunAsync(action, cts.Token).ConfigureAwait(false);
    }

    /// <summary>
    /// Queues <paramref name="action"/> for the game thread and waits, with no timeout, for it to
    /// complete. Cancelling <paramref name="ct"/> while the action is still queued skips it and
    /// throws <see cref="TaskCanceledException"/> carrying that token; an action the game thread
    /// has already started runs to completion and reports its real result. Background work that
    /// must wait out a multi-second save uses this overload.
    /// Captures the ambient <see cref="ModRequestContext.RequestId"/> at queue time and re-binds
    /// it on the game-thread side so structured events emitted inside the action carry the
    /// triggering request id — <c>AsyncLocal</c> does not flow across the external pump boundary.
    /// </summary>
    public async Task RunAsync(Action action, CancellationToken ct)
    {
        var tcs = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        var capturedRequestId = ModRequestContext.RequestId;
        Action wrapped = () =>
        {
            using var _correlationScope = ModRequestContext.Bind(capturedRequestId);
            action();
        };
        var item = new PendingGameAction(wrapped, tcs);

        // Register before enqueueing so an already-canceled token claims the item before the
        // drain can see it; the action then never runs.
        using var registration = ct.Register(() =>
        {
            // Claim pending→canceled before faulting. If the game thread already claimed
            // execution, don't cancel — the caller awaits the action's real result instead of
            // being told a mutation that is running right now didn't apply.
            if (item.TryClaimCancel())
            {
                tcs.TrySetCanceled(ct);
            }
        });
        _pending.Enqueue(item);

        await tcs.Task.ConfigureAwait(false);
    }

    private void OnUpdateTicked(object? sender, UpdateTickedEventArgs e)
    {
        var actionsProcessed = false;
        while (_pending.TryDequeue(out var item))
        {
            if (!item.TryClaimExecution())
            {
                // The caller's wait timed out or was canceled and it already observed that; the
                // atomic claim guarantees the cancel can no longer land once execution starts
                // (and vice versa).
                continue;
            }
            actionsProcessed = true;
            try
            {
                item.Action();
                item.Completion.TrySetResult(true);
            }
            catch (Exception ex)
            {
                // Warn, not Error: the exception is faulted to the caller, which decides how to
                // report it, and Error lines cancel E2E runs (debugging.md).
                Monitor.Log($"Error executing pending game action: {ex}", LogLevel.Warn);
                item.Completion.TrySetException(ex);
            }
        }

        if (actionsProcessed)
        {
            Drained?.Invoke();
        }
    }
}
