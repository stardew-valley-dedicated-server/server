using Docker.DotNet;
using Docker.DotNet.Models;
using JunimoServer.Tests.Infrastructure;

namespace JunimoServer.Tests.Helpers;

/// <summary>
/// Simulates total connectivity loss for a server container by disconnecting it from
/// its (single) Docker test network and later reconnecting it. The container is
/// attached to exactly one user-defined network with a published API port
/// (<c>ServerContainer</c> uses <c>.WithNetwork(network).WithPortBinding(...)</c>),
/// so disconnecting it drops the container's only network connection — severing client↔server
/// game traffic AND the container's outbound internet (Steam + GOG Galaxy) at
/// once. That is precisely the total connectivity loss the Galaxy-reinit repro needs;
/// the already-shipped #391 fix only covered a partial Steam-CM cut.
///
/// Empirically verified (Docker 29.2.1): while disconnected the container keeps
/// running with only <c>lo</c> up, its published API port is unreachable, and its
/// outbound internet is dead — then both return on reconnect. So the test cannot
/// poll the HTTP API during the cut; it reads recovery from infrastructure.jsonl
/// (mod events stream over stdout, not the network) and re-checks the API after
/// reconnect.
///
/// This helper does NOT touch the health watchdog. The caller must bracket the
/// outage with <see cref="ManagedServer.SuspendHealthChecks"/> (pass
/// includeLogErrorScan: true) / <see cref="ManagedServer.ResumeHealthChecks"/> —
/// otherwise the server is poisoned during the cut, either by the watchdog (~25s in,
/// 5 failed /health probes) or by the log-error scan (the cut makes SMAPI log
/// Steam/Galaxy ERRORs). Keeping that bracket at the call site mirrors how
/// ReloadAsync/CreateNewGameAsync wrap intentional transitions and keeps the
/// suspend/resume visible.
/// </summary>
internal static class NetworkOutageHelper
{
    /// <summary>
    /// Disconnects the server container from the given test network (force=true, so the
    /// daemon drops the endpoint even though the container is running). The network id
    /// comes from <see cref="GetAttachedNetworkIdAsync"/>, captured by the caller before
    /// the cut and shared with <see cref="ReconnectAsync"/>. A 404 means the container or
    /// network vanished between capture and cut — that invalidates the outage setup, so
    /// this throws immediately instead of leaving the caller to wait out a doomed
    /// steam_session_lost gate and blame the cut for a container that was already gone.
    /// </summary>
    public static async Task DisconnectAsync(
        ResourceLease lease,
        string networkId,
        CancellationToken ct = default
    )
    {
        var client = lease.Host.ApiClient;
        var containerId = lease.Server.Container.Id;
        try
        {
            await client.Networks.DisconnectNetworkAsync(
                networkId,
                new NetworkDisconnectParameters { Container = containerId, Force = true },
                ct
            );
        }
        catch (DockerApiException ex)
            when (ex is DockerContainerNotFoundException || (int)ex.StatusCode == 404)
        {
            throw new InvalidOperationException(
                $"Cannot cut network {networkId[..12]}: container {containerId[..12]} (or the "
                    + "network) vanished before the disconnect, so the outage setup is invalid.",
                ex
            );
        }
    }

    /// <summary>
    /// Reconnects the server container to the given test network. The network id is
    /// captured by <see cref="DisconnectAsync"/>'s caller via
    /// <see cref="GetAttachedNetworkIdAsync"/> before the cut, because once the
    /// container is detached an inspect no longer reports the network. Returns false —
    /// without throwing, because this runs in the caller's cleanup path where a raw
    /// NotFound would mask the test's real failure — when the container or network is
    /// already gone and there was nothing to reconnect; the caller's log must say so.
    /// </summary>
    public static async Task<bool> ReconnectAsync(
        ResourceLease lease,
        string networkId,
        CancellationToken ct = default
    )
    {
        var client = lease.Host.ApiClient;
        var containerId = lease.Server.Container.Id;

        try
        {
            await client.Networks.ConnectNetworkAsync(
                networkId,
                new NetworkConnectParameters { Container = containerId },
                ct
            );
            return true;
        }
        catch (DockerApiException ex)
            when (ex is DockerContainerNotFoundException || (int)ex.StatusCode == 404)
        {
            return false;
        }
    }

    /// <summary>
    /// Returns the id of the (single non-loopback) Docker network the server
    /// container is currently attached to, or <c>null</c> if the container is already
    /// gone. Call this BEFORE disconnecting and keep the result to pass to
    /// <see cref="DisconnectAsync"/> / <see cref="ReconnectAsync"/> — after the cut the
    /// container has no network to inspect.
    /// </summary>
    public static async Task<string?> GetAttachedNetworkIdAsync(
        ResourceLease lease,
        CancellationToken ct = default
    )
    {
        var client = lease.Host.ApiClient;
        var containerId = lease.Server.Container.Id;
        ContainerInspectResponse inspect;
        try
        {
            inspect = await client.Containers.InspectContainerAsync(containerId, ct);
        }
        catch (DockerApiException ex)
            when (ex is DockerContainerNotFoundException || (int)ex.StatusCode == 404)
        {
            // Container already gone at capture time. Null (not a raw NotFound throw) so
            // the caller's pre-cut assert reports the invalid outage setup with context.
            return null;
        }

        var networks = inspect.NetworkSettings?.Networks;
        if (networks == null || networks.Count == 0)
        {
            throw new InvalidOperationException(
                $"Container {containerId[..12]} has no attached networks to disconnect "
                    + "(already detached, or inspect returned no NetworkSettings)."
            );
        }

        // The server container is attached to exactly one user-defined network
        // (the shared test network). EndpointSettings.NetworkID is the daemon-side
        // id that DisconnectNetworkAsync/ConnectNetworkAsync expect.
        var endpoint = networks.Values.First();
        var networkId = endpoint.NetworkID;
        if (string.IsNullOrEmpty(networkId))
        {
            throw new InvalidOperationException(
                $"Container {containerId[..12]}'s endpoint has no NetworkID."
            );
        }

        return networkId;
    }
}
