using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Configurations;
using DotNet.Testcontainers.Containers;
using DotNet.Testcontainers.Images;
using DotNet.Testcontainers.Networks;
using JunimoServer.Tests.Clients;
using JunimoServer.Tests.Helpers;
using Microsoft.Extensions.Logging.Abstractions;

namespace JunimoServer.Tests.Containers;

/// <summary>
/// Manages a containerized game client instance for E2E testing.
/// Each container runs Stardew Valley with the JunimoTestClient mod.
/// </summary>
public class GameClientContainer : IAsyncDisposable
{
    private readonly IContainer _container;
    private readonly GameTestClient _apiClient;
    private readonly int _clientIndex;
    private readonly GameClientOptions _options;
    private readonly Action<string>? _logCallback;

    private CancellationTokenSource? _logStreamCts;
    private Task? _logStreamTask;
    private long _logPosition;

    /// <summary>
    /// Numeric index of this client (0, 1, 2...).
    /// </summary>
    public int ClientIndex => _clientIndex;

    /// <summary>
    /// The internal container port for the test client API.
    /// </summary>
    public const int ContainerApiPort = 5123;

    /// <summary>
    /// The mapped host port for the test client API.
    /// </summary>
    public int ApiPort { get; private set; }

    /// <summary>
    /// Base URL for the test client API (http://localhost:{ApiPort}).
    /// </summary>
    public string BaseUrl => $"http://localhost:{ApiPort}";

    /// <summary>
    /// HTTP client for controlling the game client.
    /// </summary>
    public GameTestClient Client => _apiClient;

    /// <summary>
    /// The underlying Testcontainers container.
    /// </summary>
    public IContainer Container => _container;

    private GameClientContainer(
        IContainer container,
        int clientIndex,
        GameClientOptions options,
        Action<string>? logCallback)
    {
        _container = container;
        _clientIndex = clientIndex;
        _options = options;
        _logCallback = logCallback;
        _apiClient = new GameTestClient(); // Will be reconfigured after start
    }

    /// <summary>
    /// Creates a new game client container.
    /// </summary>
    /// <param name="clientIndex">Numeric index for this client (0, 1, 2...).</param>
    /// <param name="options">Configuration options.</param>
    /// <param name="network">Optional Docker network to join.</param>
    /// <param name="logCallback">Optional callback for log output.</param>
    /// <param name="ct">Cancellation token.</param>
    public static async Task<GameClientContainer> CreateAsync(
        int clientIndex,
        GameClientOptions options,
        INetwork? network = null,
        Action<string>? logCallback = null,
        CancellationToken ct = default)
    {
        var containerName = $"sdvd-test-client-{clientIndex}-{Guid.NewGuid():N}";

        var builder = new ContainerBuilder()
            .WithLogger(NullLogger.Instance)
            .WithImage($"sdvd/test-client:{options.ImageTag}")
            .WithImagePullPolicy(options.ImageTag == "local" ? PullPolicy.Never : PullPolicy.Missing)
            .WithName(containerName)
            .WithPortBinding(ContainerApiPort, true) // Dynamic host port
            .WithVolumeMount(options.GameDataVolume, "/data/game", AccessMode.ReadOnly)
            .WithEnvironment("JUNIMO_TEST_PORT", ContainerApiPort.ToString())
            .WithWaitStrategy(Wait.ForUnixContainer()
                .UntilHttpRequestIsSucceeded(r => r
                    .ForPort(ContainerApiPort)
                    .ForPath("/ping")
                    .ForStatusCode(System.Net.HttpStatusCode.OK))
                .AddCustomWaitStrategy(new WaitUntilApiHealthy(ContainerApiPort)));

        if (network != null)
        {
            builder = builder
                .WithNetwork(network)
                .WithNetworkAliases($"test-client-{clientIndex}");
        }

        var container = builder.Build();

        return new GameClientContainer(container, clientIndex, options, logCallback);
    }

    /// <summary>
    /// Starts the container and waits for the API to be ready.
    /// </summary>
    public async Task StartAsync(CancellationToken ct = default)
    {
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(_options.StartupTimeout);

        try
        {
            await _container.StartAsync(timeoutCts.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            var logs = await _container.GetLogsAsync();
            throw new TimeoutException(
                $"Game client {_clientIndex} failed to start within {_options.StartupTimeout.TotalSeconds}s.\n" +
                $"Logs:\n{logs.Stdout}\n\nErrors:\n{logs.Stderr}");
        }

        // Get the dynamically mapped port
        ApiPort = _container.GetMappedPublicPort(ContainerApiPort);

        // Reconfigure the API client with the correct URL
        _apiClient.Dispose();
        var newClient = new GameTestClient(BaseUrl);
        // Copy the client reference (GameTestClient is a wrapper, we need to update the internal HttpClient)
        typeof(GameTestClient)
            .GetField("_httpClient", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)
            ?.SetValue(_apiClient, typeof(GameTestClient)
                .GetField("_httpClient", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)
                ?.GetValue(newClient));

        // Start log streaming
        _logStreamCts = new CancellationTokenSource();
        _logStreamTask = Task.Run(() => StreamLogsAsync(_logStreamCts.Token));
    }

    /// <summary>
    /// Connect to the server via invite code (Steam/Galaxy).
    /// </summary>
    public async Task<ConnectionResult> ConnectViaInviteCodeAsync(
        string inviteCode,
        CancellationToken ct = default)
    {
        var helper = new ConnectionHelper(_apiClient, new ConnectionOptions
        {
            MaxAttempts = 3,
            FarmhandMenuTimeout = _options.ConnectionTimeout
        });

        return await helper.ConnectToServerAsync(inviteCode, ct);
    }

    /// <summary>
    /// Connect to the server via LAN/IP address.
    /// </summary>
    /// <param name="address">Server address (hostname or IP).</param>
    /// <param name="port">Server game port (default: 24642).</param>
    /// <param name="ct">Cancellation token.</param>
    public async Task<ConnectionResult> ConnectViaLanAsync(
        string address,
        int port = 24642,
        CancellationToken ct = default)
    {
        var helper = new ConnectionHelper(_apiClient, new ConnectionOptions
        {
            MaxAttempts = 3,
            FarmhandMenuTimeout = _options.ConnectionTimeout
        });

        return await helper.ConnectViaLanAsync(address, port, ct);
    }

    /// <summary>
    /// Disconnect from the server and return to title screen.
    /// </summary>
    public async Task DisconnectAsync(CancellationToken ct = default)
    {
        await _apiClient.Exit();
        await _apiClient.Wait.ForTitle(TimeSpan.FromSeconds(10));
    }

    private async Task StreamLogsAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var logs = await _container.GetLogsAsync(timestampsEnabled: false, ct: ct);
                var combinedOutput = (logs.Stdout ?? "") + (logs.Stderr ?? "");
                var allLines = combinedOutput.Split('\n');

                for (var i = (int)_logPosition; i < allLines.Length; i++)
                {
                    var line = allLines[i];
                    if (!string.IsNullOrWhiteSpace(line))
                    {
                        _logCallback?.Invoke($"[Client {_clientIndex}] {line.Trim()}");
                    }
                }

                _logPosition = allLines.Length;
                await Task.Delay(500, ct);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch
            {
                if (!ct.IsCancellationRequested)
                {
                    await Task.Delay(1000, ct);
                }
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        // Stop log streaming
        if (_logStreamCts != null)
        {
            _logStreamCts.Cancel();
            if (_logStreamTask != null)
            {
                try
                {
                    await _logStreamTask.WaitAsync(TimeSpan.FromSeconds(5));
                }
                catch { }
            }
            _logStreamCts.Dispose();
        }

        _apiClient.Dispose();

        try
        {
            await _container.DisposeAsync();
        }
        catch
        {
            // Fallback: force remove via Docker CLI
            try
            {
                var proc = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = "docker",
                    Arguments = $"rm -f {_container.Name}",
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                });
                if (proc != null)
                {
                    await proc.WaitForExitAsync();
                }
            }
            catch { }
        }
    }

    /// <summary>
    /// Custom wait strategy that verifies the API is truly healthy.
    /// </summary>
    private class WaitUntilApiHealthy : IWaitUntil
    {
        private readonly int _port;

        public WaitUntilApiHealthy(int port)
        {
            _port = port;
        }

        public async Task<bool> UntilAsync(IContainer container)
        {
            try
            {
                var mappedPort = container.GetMappedPublicPort(_port);
                using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
                var response = await client.GetAsync($"http://localhost:{mappedPort}/health");
                return response.IsSuccessStatusCode;
            }
            catch
            {
                return false;
            }
        }
    }
}
