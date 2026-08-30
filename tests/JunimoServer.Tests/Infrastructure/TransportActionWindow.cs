using JunimoServer.Tests.Helpers;
using JunimoServer.Tests.Schema.Events;
using JunimoServer.Tests.Schema.Json;

namespace JunimoServer.Tests.Infrastructure;

/// <summary>
/// Answers "did the runner just act on the transport?" from owned state rather than
/// exception shape. The runner (master monitor in <c>TestRunner/Program.cs</c>) is the only
/// process that kills or respawns a master; it publishes each action per host to
/// <c>{runDir}/diagnostics/transport-state.{hostId}.json</c> (<see cref="TransportStateFile"/>),
/// which the xUnit child reads here on demand. The classifier has no host for the
/// fault it is looking at, so an open window on any host counts.
/// </summary>
internal static class TransportActionWindow
{
    /// <summary>
    /// True while <paramref name="nowUtc"/> lies in the action's attribution window:
    /// from the runner's decision to act until <see cref="TransportState.WindowEndUtc"/>,
    /// or open-ended while the action is still in progress.
    /// </summary>
    public static bool Contains(TransportState? state, DateTime nowUtc) =>
        state is not null
        && nowUtc >= state.ActionStartedAtUtc
        && (state.WindowEndUtc is null || nowUtc <= state.WindowEndUtc.Value);

    /// <summary>
    /// Reads every host's state file. Callers run inside exception filters, where a
    /// throw is swallowed as <c>false</c> without a trace, so an unreadable file is reported
    /// as a <see cref="TransportEventNames.TransportStateUnreadable"/> event and treated as
    /// "no action" instead of propagating.
    /// </summary>
    public static bool IsOpenNow()
    {
        var now = DateTime.UtcNow;
        foreach (var path in TransportStateFile.PathsIn(TestArtifacts.RunDir))
        {
            TransportState? state;
            try
            {
                state = TransportStateFile.ReadPath(path);
            }
            catch (Exception ex)
            {
                InfrastructureEventLog.Emit(
                    TransportEventNames.TransportStateUnreadable,
                    new TransportStateUnreadableEvent(path, ex.GetType().Name, ex.Message)
                );
                continue;
            }

            if (Contains(state, now))
            {
                return true;
            }
        }

        return false;
    }
}
