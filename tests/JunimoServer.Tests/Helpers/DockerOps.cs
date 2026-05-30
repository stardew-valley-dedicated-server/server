using Docker.DotNet;
using Docker.DotNet.Models;

namespace JunimoServer.Tests.Helpers;

/// <summary>
/// Thin wrapper over Docker.DotNet for the Docker operations the test harness
/// performs outside the Testcontainers builder lifecycle (volume create with
/// labels, force-remove of containers/volumes/networks, label-filter list +
/// remove, daemon version reporting).
///
/// Routes each call through the per-host <see cref="DockerClient"/> the caller
/// hands in, so multi-host runs target the right daemon for each operation.
/// (The <c>docker</c> CLI cannot target a remote daemon address per call without
/// <c>DOCKER_HOST</c> gymnastics.)
///
/// Methods come in async and sync flavors. Sync flavors are used only by
/// <see cref="EmergencyCleanup"/>'s ProcessExit handler, which cannot await.
/// They use <see cref="System.Threading.Tasks.Task.GetAwaiter"/>-style blocking
/// with a short ceiling so the process can still exit even if the daemon is
/// unreachable.
/// </summary>
internal static class DockerOps
{
    /// <summary>Sync ceiling for ProcessExit-handler operations.</summary>
    private static readonly TimeSpan SyncCeiling = TimeSpan.FromSeconds(3);

    /// <summary>
    /// Creates a labeled named volume. Idempotent per docker semantics — the
    /// daemon returns 200 OK for an existing volume with matching name.
    /// </summary>
    public static async Task CreateVolumeAsync(
        DockerClient client,
        string name,
        IDictionary<string, string> labels,
        CancellationToken ct = default)
    {
        await client.Volumes.CreateAsync(new VolumesCreateParameters
        {
            Name = name,
            Labels = new Dictionary<string, string>(labels),
        }, ct);
    }

    /// <summary>
    /// Force-removes a container by name. Tolerates the container not existing
    /// (e.g. emergency cleanup runs after Testcontainers already reaped it).
    /// </summary>
    public static async Task ForceRemoveContainerAsync(
        DockerClient client,
        string name,
        CancellationToken ct = default)
    {
        try
        {
            await client.Containers.RemoveContainerAsync(name, new ContainerRemoveParameters
            {
                Force = true,
                RemoveVolumes = false,
            }, ct);
        }
        catch (DockerContainerNotFoundException) { /* tolerable */ }
        catch (DockerApiException ex) when ((int)ex.StatusCode == 404) { /* tolerable */ }
    }

    /// <summary>Force-removes a named volume. Tolerates "not found".</summary>
    public static async Task RemoveVolumeAsync(
        DockerClient client,
        string name,
        bool force = true,
        CancellationToken ct = default)
    {
        try
        {
            await client.Volumes.RemoveAsync(name, force, ct);
        }
        catch (DockerApiException ex) when ((int)ex.StatusCode == 404) { /* tolerable */ }
    }

    /// <summary>
    /// Returns the daemon's <c>Server.Version</c> string, or null if the call
    /// fails. Used by <see cref="RunMetadata"/> for run-metadata.json reporting.
    /// </summary>
    public static async Task<string?> TryGetServerVersionAsync(
        DockerClient client, CancellationToken ct = default)
    {
        try
        {
            var v = await client.System.GetVersionAsync(ct);
            return v?.Version;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Lists all containers (running + stopped) carrying the given label, then
    /// force-removes each one. Returns the count of containers found.
    /// Errors per-id are swallowed — the caller is emergency cleanup, "best
    /// effort, no throw" is the contract.
    /// </summary>
    public static async Task<int> BulkForceRemoveContainersByLabelAsync(
        DockerClient client,
        string labelKey,
        string labelValue,
        TimeSpan? perCallTimeout = null,
        CancellationToken ct = default)
    {
        var containers = await client.Containers.ListContainersAsync(new ContainersListParameters
        {
            All = true,
            Filters = LabelFilter(labelKey, labelValue),
        }, ct);

        foreach (var c in containers)
        {
            using var cts = perCallTimeout is { } t
                ? CancellationTokenSource.CreateLinkedTokenSource(ct)
                : null;
            cts?.CancelAfter(perCallTimeout!.Value);
            try
            {
                await client.Containers.RemoveContainerAsync(c.ID, new ContainerRemoveParameters
                {
                    Force = true,
                    RemoveVolumes = false,
                }, cts?.Token ?? ct);
            }
            catch { /* best effort */ }
        }
        return containers.Count;
    }

    /// <summary>
    /// Removes all networks carrying the given label. Returns the count
    /// of networks found. Errors per-id swallowed.
    /// </summary>
    public static async Task<int> BulkRemoveNetworksByLabelAsync(
        DockerClient client,
        string labelKey,
        string labelValue,
        TimeSpan? perCallTimeout = null,
        CancellationToken ct = default)
    {
        var networks = await client.Networks.ListNetworksAsync(new NetworksListParameters
        {
            Filters = LabelFilter(labelKey, labelValue),
        }, ct);

        foreach (var n in networks)
        {
            using var cts = perCallTimeout is { } t
                ? CancellationTokenSource.CreateLinkedTokenSource(ct)
                : null;
            cts?.CancelAfter(perCallTimeout!.Value);
            try { await client.Networks.DeleteNetworkAsync(n.ID, cts?.Token ?? ct); }
            catch { /* best effort */ }
        }
        return networks.Count;
    }

    /// <summary>
    /// Removes all volumes carrying the given label. Returns the count
    /// of volumes found. Errors per-name swallowed.
    /// </summary>
    public static async Task<int> BulkRemoveVolumesByLabelAsync(
        DockerClient client,
        string labelKey,
        string labelValue,
        TimeSpan? perCallTimeout = null,
        CancellationToken ct = default)
    {
        var listed = await client.Volumes.ListAsync(new VolumesListParameters
        {
            Filters = LabelFilter(labelKey, labelValue),
        }, ct);

        if (listed?.Volumes == null) return 0;
        foreach (var v in listed.Volumes)
        {
            using var cts = perCallTimeout is { } t
                ? CancellationTokenSource.CreateLinkedTokenSource(ct)
                : null;
            cts?.CancelAfter(perCallTimeout!.Value);
            try { await client.Volumes.RemoveAsync(v.Name, force: true, cts?.Token ?? ct); }
            catch { /* best effort */ }
        }
        return listed.Volumes.Count;
    }

    /// <summary>
    /// Sync wrapper for ProcessExit-handler use. Bounded by <see cref="SyncCeiling"/>
    /// so a hung daemon can't hold up process exit. Errors swallowed.
    /// </summary>
    public static void ForceRemoveContainerSync(DockerClient client, string name)
    {
        try
        {
            using var cts = new CancellationTokenSource(SyncCeiling);
            ForceRemoveContainerAsync(client, name, cts.Token).GetAwaiter().GetResult();
        }
        catch { /* best effort */ }
    }

    /// <summary>Sync force-remove of a volume by name. Bounded.</summary>
    public static void RemoveVolumeSync(DockerClient client, string name)
    {
        try
        {
            using var cts = new CancellationTokenSource(SyncCeiling);
            RemoveVolumeAsync(client, name, force: true, cts.Token).GetAwaiter().GetResult();
        }
        catch { /* best effort */ }
    }

    private static IDictionary<string, IDictionary<string, bool>> LabelFilter(string key, string value)
    {
        var k = $"{key}={value}";
        return new Dictionary<string, IDictionary<string, bool>>
        {
            ["label"] = new Dictionary<string, bool> { [k] = true },
        };
    }
}
