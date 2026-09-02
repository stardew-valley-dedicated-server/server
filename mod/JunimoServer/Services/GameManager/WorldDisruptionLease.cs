namespace JunimoServer.Services.GameManager;

/// <summary>
/// One mutual-exclusion point for everything that drops players or has told them it will.
/// Two classes of holder share it: <b>world transitions</b> (new game, reload — the world is
/// replaced and every peer is disconnected) and <b>announced disruptions</b> (a countdown that
/// has told players a drop is coming, so a second announcer must not interleave). Any new code
/// path that disconnects players or announces a disconnect must acquire it before acting and
/// release it when its operation settles — a transition's <see cref="System.Threading.Tasks.Task"/>
/// completing or faulting, not an HTTP timeout. A holder that exits early before acting never
/// takes it: check, then take, in one game-thread action.
/// Memory-only: a process kill clears it. Locked so a holder can release from a task
/// continuation on the thread pool as well as from the game thread.
/// </summary>
public sealed class WorldDisruptionLease
{
    private readonly object _lock = new();
    private string? _holder;

    /// <summary>The current holder's name, or null when free.</summary>
    public string? Holder
    {
        get
        {
            lock (_lock)
            {
                return _holder;
            }
        }
    }

    /// <summary>Takes the lease for <paramref name="holder"/>; false if any holder has it.</summary>
    public bool TryAcquire(string holder)
    {
        lock (_lock)
        {
            if (_holder != null)
            {
                return false;
            }

            _holder = holder;
            return true;
        }
    }

    /// <summary>
    /// Releases the lease only if <paramref name="holder"/> holds it, so a stale release can
    /// never clear a later holder.
    /// </summary>
    public void Release(string holder)
    {
        lock (_lock)
        {
            if (_holder == holder)
            {
                _holder = null;
            }
        }
    }
}
