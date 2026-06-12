using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Networks;
using JunimoServer.Tests.Helpers;

namespace JunimoServer.Tests.Infrastructure;

/// <summary>
/// Owns one shared Docker network per host for the entire test run. All server
/// and client containers placed on a host attach to that host's network so
/// clients survive server swaps and Lidgren UDP discovery via the
/// <c>server-{runId}</c> alias works on each host's bridge.
///
/// Cross-host traffic does not exist — a test's server and clients always run
/// on the same host. The coordinator keeps one entry per host id.
/// </summary>
public static class TestNetworkManager
{
    private static readonly Dictionary<string, INetwork> _networks = new();
    private static readonly Dictionary<string, string> _names = new();
    private static readonly SemaphoreSlim _lock = new(1, 1);

    /// <summary>
    /// Returns (creating if needed) the shared network for the given host. Local
    /// hosts use the OS-default daemon; remote hosts use <c>host.EndpointConfig</c>.
    /// </summary>
    public static async Task<INetwork> GetOrCreateNetworkAsync(
        Infrastructure.DockerHost host,
        CancellationToken ct
    )
    {
        lock (_networks)
        {
            if (_networks.TryGetValue(host.Id, out var existing))
            {
                return existing;
            }
        }

        await _lock.WaitAsync(ct);
        try
        {
            lock (_networks)
            {
                if (_networks.TryGetValue(host.Id, out var existing))
                {
                    return existing;
                }
            }

            var networkId = Guid.NewGuid().ToString("N")[..8];
            var name = $"sdvd-test-shared-{host.Id}-{networkId}";

            var network = new NetworkBuilder()
                .WithDockerEndpoint(host.EndpointConfig)
                .WithName(name)
                .WithLabel("sdvd.test", "true")
                .WithLabel("sdvd.run-id", networkId)
                .Build();

            await network.CreateAsync(ct);

            lock (_networks)
            {
                _networks[host.Id] = network;
                _names[host.Id] = name;
            }
            return network;
        }
        finally
        {
            _lock.Release();
        }
    }

    public static async ValueTask DisposeAsync()
    {
        INetwork[] toDispose;
        lock (_networks)
        {
            toDispose = _networks.Values.ToArray();
            _networks.Clear();
            _names.Clear();
        }
        foreach (var n in toDispose)
        {
            try
            {
                await n.DisposeAsync();
            }
            catch { }
        }
    }
}
