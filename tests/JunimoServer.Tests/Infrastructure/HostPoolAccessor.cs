using Docker.DotNet;

namespace JunimoServer.Tests.Infrastructure;

/// <summary>
/// Indirection between <see cref="JunimoServer.Tests.Helpers.EmergencyCleanup"/>
/// (and any other process-exit-time consumer) and the live <c>HostPool</c> singleton.
///
/// Why a separate accessor: <c>EmergencyCleanup</c> may run from <c>ProcessExit</c>
/// before <c>HostPool</c> is initialized (early crash, fail-fast on bad config) or
/// after it has been disposed. The accessor is set at <c>HostPool</c>
/// initialization and reset on shutdown; consumers that find no registered hosts
/// fall back to the local default daemon.
///
/// During steps 1-4 this returns null because <c>HostPool</c> doesn't yet exist.
/// Step 5 wires <c>HostPool.Initialize</c> to register here.
/// </summary>
internal static class HostPoolAccessor
{
    private static Func<IReadOnlyList<(DockerClient Client, bool IsRemote)>>? _provider;

    public static void Register(Func<IReadOnlyList<(DockerClient Client, bool IsRemote)>> provider)
    {
        _provider = provider;
    }

    public static void Clear()
    {
        _provider = null;
    }

    /// <summary>
    /// Returns the per-host clients for emergency cleanup, or null if HostPool
    /// is not yet initialized.
    /// </summary>
    public static IReadOnlyList<(DockerClient Client, bool IsRemote)>? GetHostsForCleanup()
    {
        try
        {
            return _provider?.Invoke();
        }
        catch
        {
            return null;
        }
    }
}
