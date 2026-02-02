using DotNet.Testcontainers.Networks;

namespace JunimoServer.Tests.Containers;

/// <summary>
/// Manages multiple server containers for E2E tests.
/// Supports both per-test isolated servers and shared servers across tests.
/// </summary>
public class ServerContainerManager : IAsyncDisposable
{
    private readonly List<ServerContainer> _servers = new();
    private readonly ServerContainerOptions _defaultOptions;
    private readonly Dictionary<string, string> _envVars;
    private readonly Action<string>? _logCallback;
    private readonly object _lock = new();
    private int _nextServerIndex;

    /// <summary>
    /// All active server containers.
    /// </summary>
    public IReadOnlyList<ServerContainer> Servers => _servers;

    /// <summary>
    /// Number of active servers.
    /// </summary>
    public int ServerCount => _servers.Count;

    /// <summary>
    /// Creates a new server container manager.
    /// </summary>
    /// <param name="defaultOptions">Default options for new servers.</param>
    /// <param name="envVars">Environment variables (Steam credentials, etc.).</param>
    /// <param name="logCallback">Optional callback for log output.</param>
    public ServerContainerManager(
        ServerContainerOptions? defaultOptions = null,
        Dictionary<string, string>? envVars = null,
        Action<string>? logCallback = null)
    {
        _defaultOptions = defaultOptions ?? new ServerContainerOptions();
        _envVars = envVars ?? LoadEnvFile();
        _logCallback = logCallback;
    }

    /// <summary>
    /// Get a server by index.
    /// </summary>
    public ServerContainer this[int index]
    {
        get
        {
            lock (_lock)
            {
                if (index < 0 || index >= _servers.Count)
                    throw new ArgumentOutOfRangeException(nameof(index),
                        $"Server index {index} out of range. Active servers: {_servers.Count}");
                return _servers[index];
            }
        }
    }

    /// <summary>
    /// Creates, starts, and waits for a new server to be ready.
    /// </summary>
    /// <param name="options">Optional custom options (uses defaults if null).</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The ready server container.</returns>
    public async Task<ServerContainer> CreateServerAsync(
        ServerContainerOptions? options = null,
        CancellationToken ct = default)
    {
        int serverIndex;
        lock (_lock)
        {
            serverIndex = _nextServerIndex++;
        }

        var effectiveOptions = options ?? _defaultOptions;

        _logCallback?.Invoke($"Creating server {serverIndex} ({effectiveOptions.FarmTypeName} farm)...");

        var server = await ServerContainer.CreateAsync(
            serverIndex,
            effectiveOptions,
            _envVars,
            _logCallback,
            ct);

        await server.StartAsync(ct);

        var ready = await server.WaitForReadyAsync(ct);
        if (!ready)
        {
            await server.DisposeAsync();
            throw new TimeoutException($"Server {serverIndex} did not become ready");
        }

        lock (_lock)
        {
            _servers.Add(server);
        }

        _logCallback?.Invoke($"Server {serverIndex} ready at {server.BaseUrl}");

        return server;
    }

    /// <summary>
    /// Creates a server with a specific farm type.
    /// Convenience method for farm type tests.
    /// </summary>
    public Task<ServerContainer> CreateServerForFarmTypeAsync(
        int farmType,
        int startingCabins = 1,
        CancellationToken ct = default)
    {
        var options = ServerContainerOptions.ForFarmType(farmType);
        options.StartingCabins = startingCabins;
        options.GameDataVolume = _defaultOptions.GameDataVolume;
        options.SteamSessionVolume = _defaultOptions.SteamSessionVolume;
        options.ImageTag = _defaultOptions.ImageTag;
        return CreateServerAsync(options, ct);
    }

    /// <summary>
    /// Removes a server from management and disposes it.
    /// </summary>
    public async Task RemoveServerAsync(ServerContainer server)
    {
        lock (_lock)
        {
            _servers.Remove(server);
        }
        await server.DisposeAsync();
    }

    /// <summary>
    /// Disposes all server containers.
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        List<ServerContainer> serversToDispose;
        lock (_lock)
        {
            serversToDispose = new List<ServerContainer>(_servers);
            _servers.Clear();
        }

        _logCallback?.Invoke($"Disposing {serversToDispose.Count} server(s)...");

        // Dispose in parallel for speed
        var disposeTasks = serversToDispose.Select(async server =>
        {
            try
            {
                await server.DisposeAsync();
            }
            catch (Exception ex)
            {
                _logCallback?.Invoke($"Error disposing server {server.ServerIndex}: {ex.Message}");
            }
        });

        await Task.WhenAll(disposeTasks);

        _logCallback?.Invoke("All servers disposed");
    }

    /// <summary>
    /// Load environment variables from .env file.
    /// </summary>
    private static Dictionary<string, string> LoadEnvFile()
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        // Search for .env file
        var searchPaths = new[]
        {
            Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", ".env"),
            Path.Combine(Directory.GetCurrentDirectory(), ".env"),
        };

        string? envPath = null;
        foreach (var path in searchPaths)
        {
            var fullPath = Path.GetFullPath(path);
            if (File.Exists(fullPath))
            {
                envPath = fullPath;
                break;
            }
        }

        if (envPath == null) return result;

        foreach (var line in File.ReadAllLines(envPath))
        {
            var trimmed = line.Trim();
            if (string.IsNullOrEmpty(trimmed) || trimmed.StartsWith('#'))
                continue;

            var eqIndex = trimmed.IndexOf('=');
            if (eqIndex <= 0) continue;

            var key = trimmed[..eqIndex].Trim();
            var value = trimmed[(eqIndex + 1)..].Trim();

            // Remove surrounding quotes
            if ((value.StartsWith('"') && value.EndsWith('"')) ||
                (value.StartsWith('\'') && value.EndsWith('\'')))
            {
                value = value[1..^1];
            }

            result[key] = value;
        }

        return result;
    }
}
