using JunimoServer.Tests.Clients;
using JunimoServer.Tests.Containers;
using JunimoServer.Tests.Helpers;

namespace JunimoServer.Tests.Infrastructure;

/// <summary>
/// RAII wrapper returned by broker to tests. Exposes server, API client,
/// connection details, and client leasing.
/// </summary>
public sealed class ResourceLease : IAsyncDisposable
{
    private readonly ManagedServer _managed;
    private readonly ResourceRequirements _requirements;
    private readonly ClientPool _clientPool;
    private readonly List<ClientLease> _activeLeases = new();
    private readonly string _testName;

    public ServerContainer Server => _managed.Server;
    public string ServerKey => _managed.Key;

    /// <summary>
    /// The Docker host the underlying server runs on. Test cleanup paths read this
    /// to release the per-host <see cref="DockerHost.ClientCapacity"/> slots that
    /// the broker acquired on behalf of the test (paired Acquire/Release across
    /// the broker / TestBase / PersistentSession boundaries).
    /// </summary>
    public DockerHost Host => _managed.Host;
    public ServerApiClient Api { get; }
    public string? Password => _requirements.Password;
    public CancellationToken ErrorToken => _managed.ErrorToken;
    public bool IsPoisoned => _managed.IsPoisoned;
    public string? AbortReason => _managed.AbortReason;
    public string? ServerInstanceId => _managed.InstanceId;

    /// <summary>
    /// Whether the server requires Steam/Galaxy connections.
    /// </summary>
    public bool RequiresSteamConnection => _requirements.WithSteam;

    /// <summary>
    /// Server address for LAN connections.
    /// Uses the Docker network alias since clients are containerized.
    /// </summary>
    public string ServerLanAddress => _managed.Server.NetworkAlias;

    /// <summary>
    /// Server game port for LAN connections (internal container port).
    /// </summary>
    public int ServerLanPort => ServerContainer.ContainerGamePort;

    /// <summary>
    /// Server invite code (for Steam connections).
    /// </summary>
    public string? InviteCode => _managed.Server.InviteCode;

    private readonly bool _exclusive;
    private int _disposed;

    internal ResourceLease(
        ManagedServer managed,
        ResourceRequirements requirements,
        string testName,
        ClientPool clientPool
    )
    {
        _managed = managed;
        _requirements = requirements;
        _testName = testName;
        _clientPool = clientPool;
        _exclusive = requirements.Exclusive;
        Api = managed.Server.CreateApiClient();
    }

    /// <summary>Lease a client on demand. NOT called automatically.</summary>
    public async Task<ClientLease> LeaseClientAsync(CancellationToken ct = default)
    {
        if (_managed.IsPoisoned)
            throw new InvalidOperationException(
                $"Server {_managed.Key} is poisoned: {_managed.AbortReason}"
            );
        var lease = await _clientPool.LeaseClientAsync(
            _managed.Key,
            ct,
            requireSteam: _requirements.WithSteam
        );
        lease.EmitLeased(_testName, _managed.InstanceId);
        if (_managed.InstanceId != null)
            SetupEventBus.EmitInstanceClientAttached(_managed.InstanceId, lease.InstanceId);
        _activeLeases.Add(lease);
        var shortTest = TestLog.Short(_testName);
        var displayLabel = _requirements.GetDisplayLabel();
        TestLog.Test(
            $"{shortTest} leased client-{lease.Container.ClientIndex} ({displayLabel}, {_managed.RefCount} active tests)"
        );
        return lease;
    }

    /// <summary>
    /// Creates a new game on the server, suspending health checks during the transition.
    /// </summary>
    public async Task CreateNewGameAsync(
        FarmTypeSetting farmType,
        string farmName = "Junimo",
        int startingCabins = 1,
        string cabinStrategy = "CabinStack",
        CancellationToken ct = default
    )
    {
        await _managed.CreateNewGameAsync(farmType, farmName, startingCabins, cabinStrategy, ct);
    }

    /// <summary>
    /// Re-reads settings and reloads the current world, suspending health checks
    /// during the transition.
    /// </summary>
    public async Task ReloadAsync(CancellationToken ct = default)
    {
        await _managed.ReloadAsync(ct);
    }

    /// <summary>
    /// Current reference count on the underlying managed server.
    /// </summary>
    internal int RefCount => _managed.RefCount;

    /// <summary>
    /// Exposes the managed server for KeepConnected exclusive access coordination.
    /// KeepConnected tests bypass the normal AcquireAsync path (which handles exclusive),
    /// so TestBase needs direct access to coordinate exclusive gates on the server.
    /// </summary>
    internal ManagedServer Managed => _managed;

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        // Dispose every active lease even if one throws; rethrow the
        // aggregated errors at the end so infra failures surface loudly
        // rather than silently leaking state.
        List<Exception>? disposeErrors = null;
        foreach (var lease in _activeLeases)
        {
            try
            {
                await lease.DisposeAsync();
            }
            catch (Exception ex)
            {
                (disposeErrors ??= new()).Add(ex);
            }
        }
        _activeLeases.Clear();

        // Release exclusive access before releasing the ref, so waiting tests
        // can proceed as soon as our ref is gone.
        if (_exclusive)
            _managed.ReleaseExclusive();

        try
        {
            Api.Dispose();
        }
        finally
        {
            var shortTest = TestLog.Short(_testName);
            var displayLabel = _requirements.GetDisplayLabel();
            TestLog.Test($"{shortTest} released server {displayLabel}");
            await TestResourceBroker.Instance.ReleaseAsync(_managed, _exclusive);
        }

        if (disposeErrors is { Count: > 0 })
            throw new AggregateException(
                "One or more ClientLeases failed to dispose.",
                disposeErrors
            );
    }
}
