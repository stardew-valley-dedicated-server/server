using DotNet.Testcontainers.Networks;
using JunimoServer.Tests.Helpers;

namespace JunimoServer.Tests.Containers;

/// <summary>
/// Manages multiple containerized game clients for multi-player E2E tests.
/// </summary>
public class GameClientManager : IAsyncDisposable
{
    private readonly List<GameClientContainer> _clients = new();
    private readonly GameClientOptions _defaultOptions;
    private readonly INetwork? _network;
    private readonly Action<string>? _logCallback;
    private readonly object _lock = new();
    private int _nextClientIndex;

    /// <summary>
    /// All active game client containers.
    /// </summary>
    public IReadOnlyList<GameClientContainer> Clients => _clients;

    /// <summary>
    /// Number of active clients.
    /// </summary>
    public int ClientCount => _clients.Count;

    /// <summary>
    /// Creates a new game client manager.
    /// </summary>
    /// <param name="defaultOptions">Default options for new clients.</param>
    /// <param name="network">Optional Docker network for clients to join.</param>
    /// <param name="logCallback">Optional callback for log output.</param>
    public GameClientManager(
        GameClientOptions? defaultOptions = null,
        INetwork? network = null,
        Action<string>? logCallback = null)
    {
        _defaultOptions = defaultOptions ?? new GameClientOptions();
        _network = network;
        _logCallback = logCallback;
    }

    /// <summary>
    /// Get a client by index.
    /// </summary>
    public GameClientContainer this[int index]
    {
        get
        {
            lock (_lock)
            {
                if (index < 0 || index >= _clients.Count)
                    throw new ArgumentOutOfRangeException(nameof(index),
                        $"Client index {index} out of range. Active clients: {_clients.Count}");
                return _clients[index];
            }
        }
    }

    /// <summary>
    /// Creates and starts a new game client container.
    /// </summary>
    /// <param name="options">Optional custom options (uses defaults if null).</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The newly created and started client container.</returns>
    public async Task<GameClientContainer> CreateClientAsync(
        GameClientOptions? options = null,
        CancellationToken ct = default)
    {
        int clientIndex;
        lock (_lock)
        {
            clientIndex = _nextClientIndex++;
        }

        var effectiveOptions = options ?? _defaultOptions;

        _logCallback?.Invoke($"Creating game client {clientIndex}...");

        var client = await GameClientContainer.CreateAsync(
            clientIndex,
            effectiveOptions,
            _network,
            _logCallback,
            ct);

        await client.StartAsync(ct);

        lock (_lock)
        {
            _clients.Add(client);
        }

        _logCallback?.Invoke($"Game client {clientIndex} ready at {client.BaseUrl}");

        return client;
    }

    /// <summary>
    /// Connect all clients to the server via LAN.
    /// </summary>
    /// <param name="address">Server address.</param>
    /// <param name="port">Server game port.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Connection results for each client.</returns>
    public async Task<ConnectionResult[]> ConnectAllViaLanAsync(
        string address,
        int port,
        CancellationToken ct = default)
    {
        var tasks = _clients.Select(c => c.ConnectViaLanAsync(address, port, ct));
        return await Task.WhenAll(tasks);
    }

    /// <summary>
    /// Connect all clients to the server via invite code.
    /// </summary>
    /// <param name="inviteCode">Server invite code.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Connection results for each client.</returns>
    public async Task<ConnectionResult[]> ConnectAllViaInviteCodeAsync(
        string inviteCode,
        CancellationToken ct = default)
    {
        var tasks = _clients.Select(c => c.ConnectViaInviteCodeAsync(inviteCode, ct));
        return await Task.WhenAll(tasks);
    }

    /// <summary>
    /// Disconnect all clients from the server.
    /// </summary>
    public async Task DisconnectAllAsync(CancellationToken ct = default)
    {
        var tasks = _clients.Select(c => c.DisconnectAsync(ct));
        await Task.WhenAll(tasks);
    }

    /// <summary>
    /// Disposes all client containers.
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        List<GameClientContainer> clientsToDispose;
        lock (_lock)
        {
            clientsToDispose = new List<GameClientContainer>(_clients);
            _clients.Clear();
        }

        _logCallback?.Invoke($"Disposing {clientsToDispose.Count} game client(s)...");

        // Dispose in parallel for speed
        var disposeTasks = clientsToDispose.Select(async client =>
        {
            try
            {
                await client.DisposeAsync();
            }
            catch (Exception ex)
            {
                _logCallback?.Invoke($"Error disposing client {client.ClientIndex}: {ex.Message}");
            }
        });

        await Task.WhenAll(disposeTasks);

        _logCallback?.Invoke("All game clients disposed");
    }
}
