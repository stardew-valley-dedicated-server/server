namespace SteamService;

/// <summary>
/// State of one validate/repair pass over an installed game depot. <c>serve</c> runs the
/// boot-time pass in the background after HTTP is up and publishes this state on
/// <c>GET /game/validate-status</c>; the game container's entrypoint holds the game back while
/// the state is non-terminal, because the pass rewrites corrupt files in place on the shared
/// game volume. <c>POST /game/validate</c> runs an on-demand pass through a validator of its own.
/// </summary>
/// <remarks>
/// Every run ends in a terminal state (<see cref="State.Completed"/> or <see cref="State.Failed"/>),
/// whatever the pass throws, so the game container never waits forever. Repair is best-effort:
/// a failure releases the gate and the game boots with whatever is on disk.
/// </remarks>
public sealed class GameContentValidator
{
    public enum State
    {
        /// <summary>Validation turned off by configuration.</summary>
        Disabled,

        /// <summary>Nothing to validate on this boot (no installed depot, or no account).</summary>
        Skipped,

        /// <summary>Queued; the pass has not started yet.</summary>
        Pending,

        Running,
        Completed,
        Failed,
    }

    /// <summary>Immutable snapshot; a new instance replaces the field on every transition.</summary>
    /// <param name="Detail">
    /// Reason for <see cref="State.Skipped"/>/<see cref="State.Disabled"/>, the pass's own summary
    /// for <see cref="State.Completed"/>, the exception message for <see cref="State.Failed"/>.
    /// </param>
    public sealed record Snapshot(
        State State,
        string? Detail,
        DateTimeOffset? StartedAt,
        DateTimeOffset? FinishedAt
    )
    {
        public bool IsTerminal => State is not (State.Pending or State.Running);

        /// <summary>Wire shape of <c>GET /game/validate-status</c>.</summary>
        public object ToJson() =>
            new
            {
                status = StateName(State),
                detail = Detail,
                started_at = StartedAt?.ToString("o"),
                finished_at = FinishedAt?.ToString("o"),
            };
    }

    private volatile Snapshot _snapshot = new(State.Pending, null, null, null);

    public Snapshot Current => _snapshot;

    public static string StateName(State state) => state.ToString().ToLowerInvariant();

    public void MarkDisabled(string reason) =>
        _snapshot = new Snapshot(State.Disabled, reason, null, null);

    public void MarkSkipped(string reason) =>
        _snapshot = new Snapshot(State.Skipped, reason, null, null);

    /// <summary>
    /// Runs <paramref name="validate"/> and records the outcome. Never throws: any exception
    /// becomes <see cref="State.Failed"/> with the message as detail; the returned string
    /// becomes the <see cref="State.Completed"/> detail.
    /// </summary>
    public async Task RunAsync(Func<Task<string?>> validate)
    {
        var startedAt = DateTimeOffset.UtcNow;
        _snapshot = new Snapshot(State.Running, null, startedAt, null);
        try
        {
            var detail = await validate();
            _snapshot = new Snapshot(State.Completed, detail, startedAt, DateTimeOffset.UtcNow);
        }
        catch (Exception ex)
        {
            _snapshot = new Snapshot(State.Failed, ex.Message, startedAt, DateTimeOffset.UtcNow);
        }
    }
}
