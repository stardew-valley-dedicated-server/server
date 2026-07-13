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
/// outage with <see cref="ManagedServer.SuspendHealthChecks"/> /
/// <see cref="ManagedServer.ResumeHealthChecks"/> — otherwise the server is poisoned
/// during the cut, either by the watchdog (~25s in, 5 failed /health probes) or by
/// the log-error scan (the cut makes SMAPI log Steam/Galaxy ERRORs). One suspend
/// gates both poison paths. Keeping that bracket at the call site mirrors how
/// ReloadAsync/CreateNewGameAsync wrap intentional transitions and keeps the
/// suspend/resume visible.
/// </summary>
internal static class NetworkOutageHelper
{
    /// <summary>
    /// Disconnects the server container from its test network (force=true, so the
    /// daemon drops the endpoint even though the container is running). Resolves the
    /// network by inspecting the container — robust against the test-network name/id
    /// not being derivable from the lease.
    /// </summary>
    public static async Task DisconnectAsync(ResourceLease lease, CancellationToken ct = default)
    {
        var client = lease.Host.ApiClient;
        var containerId = lease.Server.Container.Id;
        var networkId = await ResolveAttachedNetworkIdAsync(client, containerId, ct);
        // A null id means the container is already gone (see ResolveAttachedNetworkIdAsync) —
        // nothing to disconnect.
        if (networkId == null)
        {
            return;
        }

        try
        {
            await client.Networks.DisconnectNetworkAsync(
                networkId,
                new NetworkDisconnectParameters { Container = containerId, Force = true },
                ct
            );
        }
        catch (DockerContainerNotFoundException)
        { /* container vanished mid-outage (e.g. a poison drain removed it); nothing to cut */
        }
        catch (DockerApiException ex) when ((int)ex.StatusCode == 404)
        { /* container or network gone; nothing to cut */
        }
    }

    /// <summary>
    /// Reconnects the server container to the given test network. The network id is
    /// captured by <see cref="DisconnectAsync"/>'s caller via
    /// <see cref="GetAttachedNetworkIdAsync"/> before the cut, because once the
    /// container is detached an inspect no longer reports the network. A null id (the
    /// container was already gone at capture time) is a no-op.
    /// </summary>
    public static async Task ReconnectAsync(
        ResourceLease lease,
        string? networkId,
        CancellationToken ct = default
    )
    {
        if (networkId == null)
        {
            return;
        }

        var client = lease.Host.ApiClient;
        var containerId = lease.Server.Container.Id;

        try
        {
            await client.Networks.ConnectNetworkAsync(
                networkId,
                new NetworkConnectParameters { Container = containerId },
                ct
            );
        }
        catch (DockerContainerNotFoundException)
        { /* container gone (e.g. a mid-outage poison drain disposed it); nothing to reconnect */
        }
        catch (DockerApiException ex) when ((int)ex.StatusCode == 404)
        { /* container or network gone; nothing to reconnect */
        }
    }

    /// <summary>
    /// Returns the id of the (single non-loopback) Docker network the server
    /// container is currently attached to, or <c>null</c> if the container is already
    /// gone. Call this BEFORE disconnecting and keep the result to pass to
    /// <see cref="ReconnectAsync"/> — after the cut the container has no network to inspect.
    /// </summary>
    public static Task<string?> GetAttachedNetworkIdAsync(
        ResourceLease lease,
        CancellationToken ct = default
    ) => ResolveAttachedNetworkIdAsync(lease.Host.ApiClient, lease.Server.Container.Id, ct);

    private static async Task<string?> ResolveAttachedNetworkIdAsync(
        DockerClient client,
        string containerId,
        CancellationToken ct
    )
    {
        ContainerInspectResponse inspect;
        try
        {
            inspect = await client.Containers.InspectContainerAsync(containerId, ct);
        }
        catch (DockerContainerNotFoundException)
        {
            // Container vanished (e.g. a mid-outage poison drain removed it). Not a raw
            // NotFound for the caller to surface — there's no network to cut or restore.
            return null;
        }
        catch (DockerApiException ex) when ((int)ex.StatusCode == 404)
        {
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
