using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;
using JunimoServer.Tests.Helpers;

namespace JunimoServer.Tests.Containers;

/// <summary>
/// Provides a shared local Stardew Valley game client for E2E tests.
/// One process is shared across all fixtures via static state with reference counting.
/// Uses a dynamic port to avoid conflicts.
/// </summary>
public class LocalGameClientProvider : IGameClientProvider
{
    // Static shared state: one process for all fixtures
    private static readonly SemaphoreSlim _semaphore = new(1, 1);
    private static int _refCount;
    private static int _port;
    private static Process? _process;
    private static string? _logFile;
    private static CancellationTokenSource? _logTailCts;
    private static Task? _logTailTask;
    private static readonly StringBuilder _outputLog = new();
    private static Action<string>? _activeLogCallback;

    private static readonly string ServerRepoDir = Path.GetFullPath(
        Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..")
    );
    private static readonly string ProjectParentDir =
        Path.GetFullPath(Path.Combine(ServerRepoDir, "..")) + Path.DirectorySeparatorChar;
    private static readonly string TestClientDir = Path.GetFullPath(
        Path.Combine(ServerRepoDir, "tools", "test-client")
    );

    public string BaseUrl => $"http://localhost:{_port}";

    public async Task AcquireAsync(Action<string>? logCallback, CancellationToken ct)
    {
        await _semaphore.WaitAsync(ct);
        try
        {
            _refCount++;
            if (_refCount == 1)
            {
                // First acquirer: validate, allocate port, start process
                _activeLogCallback = logCallback;

                if (!IsSteamRunning())
                {
                    _refCount--;
                    throw new InvalidOperationException(
                        "Steam is not running!\n\n"
                            + "The E2E tests require Steam to be running and logged in for invite code connections.\n"
                            + "Please start Steam, log in, and run the tests again.\n\n"
                            + "Alternatively, unset SDVD_HOST_CLIENT to use containerized clients with LAN connections instead."
                    );
                }

                // Kill any existing game client processes so we start fresh.
                // Check by process name (not HTTP) because a stale client from a
                // previous run on a dynamic port won't be responding on 5123.
                if (IsGameClientProcessRunning())
                {
                    logCallback?.Invoke(
                        "Stale game client process found, killing for fresh start..."
                    );
                    await KillGameProcesses(logCallback);
                    await Task.Delay(TestTimings.ProcessExitDelay, ct);
                }

                _port = AllocateFreePort();
                logCallback?.Invoke($"Allocated dynamic port {_port} for game client");

                await StartProcess(logCallback, ct);
                await WaitForReady(logCallback, ct);
            }
            else
            {
                // Subsequent acquirers: just wait until ready
                await WaitForReady(logCallback, ct);
            }
        }
        catch
        {
            _refCount--;
            if (_refCount <= 0)
            {
                _refCount = 0;
                await CleanupProcess(logCallback);
            }
            throw;
        }
        finally
        {
            _semaphore.Release();
        }
    }

    public async Task ReleaseAsync()
    {
        await _semaphore.WaitAsync();
        try
        {
            _refCount--;
            if (_refCount <= 0)
            {
                _refCount = 0;
                await StopGameClient(_activeLogCallback);
                await CleanupProcess(_activeLogCallback);
                _activeLogCallback = null;
            }
        }
        finally
        {
            _semaphore.Release();
        }
    }

    private static int AllocateFreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private static bool IsSteamRunning()
    {
        try
        {
            return Process.GetProcessesByName("steam").Length > 0;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Check if any game client process (StardewModdingAPI or Stardew Valley) is running,
    /// regardless of what port it's listening on.
    /// </summary>
    private static bool IsGameClientProcessRunning()
    {
        try
        {
            var names = new[] { "StardewModdingAPI", "Stardew Valley" };
            foreach (var name in names)
            {
                if (Process.GetProcessesByName(name).Length > 0)
                    return true;
            }
            return false;
        }
        catch
        {
            return false;
        }
    }

    private static async Task<bool> IsGameClientResponding()
    {
        try
        {
            using var client = new HttpClient { Timeout = TestTimings.HttpHealthCheckTimeout };
            var response = await client.GetAsync($"http://localhost:{_port}/ping");
            return response.IsSuccessStatusCode;
        }
        catch
        {
            return false;
        }
    }

    private static async Task StartProcess(Action<string>? logCallback, CancellationToken ct)
    {
        // Use file-based logging to avoid pipe buffer deadlocks when debugging
        _logFile = Path.Combine(Path.GetTempPath(), $"sdvd-test-client-{Guid.NewGuid():N}.log");

        var startInfo = new ProcessStartInfo
        {
            FileName = "cmd.exe",
            Arguments = $"/c make run-bg > \"{_logFile}\" 2>&1",
            WorkingDirectory = TestClientDir,
            UseShellExecute = false,
            RedirectStandardOutput = false,
            RedirectStandardError = false,
            CreateNoWindow = true,
        };

        // Enable fail-fast mode and set dynamic port
        startInfo.Environment["TEST_FAIL_FAST"] = "true";
        startInfo.Environment["SDVD_ENV"] = "test";
        startInfo.Environment["JUNIMO_TEST_PORT"] = _port.ToString();
        // Default to 60 fps for local-host runs — developers using SDVD_HOST_CLIENT
        // are watching the game, so we don't inherit the containerized "0 = disabled"
        // default. .env.test's CLIENT_FPS still wins when set.
        startInfo.Environment["CLIENT_FPS"] = TestEnvLoader.Get("CLIENT_FPS") ?? "60";

        _process = new Process { StartInfo = startInfo };
        _process.Start();

        // Register emergency cleanup so the game client is killed even if DisposeAsync never runs
        EmergencyCleanup.Register("GameClient", KillGameProcessesSync);

        // Start background task to tail the log file. Suppress execution-context
        // flow because the provider is a static-shared singleton (line 20:
        // _refCount); without this, the first test that wakes it would poison
        // every subsequent log line's TestContext.Current. See
        // .claude/rules/asynclocal-pitfalls.md "Long-lived background Task.Run
        // must suppress flow".
        _logTailCts = new CancellationTokenSource();
        _logTailTask = BackgroundTaskRunner.RunLongLived(
            ct => Task.Run(() => TailLogFile(_logFile, ct, logCallback), ct),
            label: "local-game-client-log-tail",
            _logTailCts.Token
        );
    }

    private static async Task WaitForReady(Action<string>? logCallback, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow + TestTimings.GameReadyTimeout;
        var pollAttempt = 0;

        while (DateTime.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();

            pollAttempt++;
            if (await IsGameClientResponding())
            {
                await Task.Delay(TestTimings.GameClientPollDelay, ct);
                return;
            }

            if (pollAttempt % 10 == 0)
            {
                var remaining = deadline - DateTime.UtcNow;
                logCallback?.Invoke(
                    $"Waiting for game client on port {_port}... attempt {pollAttempt}, {remaining.TotalSeconds:0}s remaining"
                );
            }

            await Task.Delay(TestTimings.GameClientStartupPollDelay, ct);
        }

        throw new TimeoutException(
            $"Game client did not become ready within {TestTimings.GameReadyTimeout.TotalSeconds} seconds on port {_port}.\n"
                + $"Output:\n{_outputLog}"
        );
    }

    private static async Task StopGameClient(Action<string>? logCallback)
    {
        logCallback?.Invoke("Stopping game client...");

        // Stop the log tail task first
        if (_logTailCts != null)
        {
            try
            {
                _logTailCts.Cancel();
                if (_logTailTask != null)
                {
                    await _logTailTask.WaitAsync(TestTimings.TaskCleanupTimeout);
                }
            }
            catch (TimeoutException) { }
            catch (Exception) { }
            finally
            {
                _logTailCts.Dispose();
                _logTailCts = null;
            }
        }

        // Strategy 1: Try 'make stop' with timeout
        var stopped = await TryMakeStop(logCallback);

        // Strategy 2: If make stop failed, kill processes directly
        if (!stopped)
        {
            logCallback?.Invoke("make stop failed, killing processes directly...");
            await KillGameProcesses(logCallback);
        }

        // Strategy 3: Kill the launcher process
        if (_process != null)
        {
            try
            {
                if (!_process.HasExited)
                {
                    _process.Kill(entireProcessTree: true);
                    await _process.WaitForExitAsync();
                }
            }
            catch { }
            finally
            {
                _process.Dispose();
                _process = null;
            }
        }

        // Strategy 4: Verify the game is actually stopped
        await Task.Delay(TestTimings.KillRetryDelay);
        if (await IsGameClientResponding())
        {
            logCallback?.Invoke("Game still responding after stop, force killing...");
            await KillGameProcesses(logCallback);
        }

        EmergencyCleanup.Unregister("GameClient");
        logCallback?.Invoke("Game client stopped");
    }

    private static async Task CleanupProcess(Action<string>? logCallback)
    {
        // Clean up log file
        if (!string.IsNullOrEmpty(_logFile))
        {
            for (var attempt = 0; attempt < 3; attempt++)
            {
                try
                {
                    if (File.Exists(_logFile))
                    {
                        File.Delete(_logFile);
                    }
                    break;
                }
                catch (IOException) when (attempt < 2)
                {
                    await Task.Delay(TestTimings.KillRetryDelay);
                }
                catch
                {
                    break;
                }
            }
            _logFile = null;
        }

        _outputLog.Clear();
    }

    private static async Task<bool> TryMakeStop(Action<string>? logCallback)
    {
        try
        {
            var stopInfo = new ProcessStartInfo
            {
                FileName = "make",
                Arguments = "stop",
                WorkingDirectory = TestClientDir,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };

            using var stopProcess = Process.Start(stopInfo);
            if (stopProcess == null)
                return false;

            using var cts = new CancellationTokenSource(TestTimings.ContainerStopTimeout);
            try
            {
                await stopProcess.WaitForExitAsync(cts.Token);
                return stopProcess.ExitCode == 0;
            }
            catch (OperationCanceledException)
            {
                logCallback?.Invoke("make stop timed out");
                try
                {
                    stopProcess.Kill();
                }
                catch { }
                return false;
            }
        }
        catch
        {
            return false;
        }
    }

    private static async Task KillGameProcesses(Action<string>? logCallback)
    {
        var processNames = new[] { "StardewModdingAPI", "Stardew Valley" };

        foreach (var name in processNames)
        {
            try
            {
                var processes = Process.GetProcessesByName(name);
                foreach (var proc in processes)
                {
                    try
                    {
                        logCallback?.Invoke(
                            $"Killing process: {proc.ProcessName} (PID: {proc.Id})"
                        );
                        proc.Kill(entireProcessTree: true);
                        await proc.WaitForExitAsync();
                    }
                    catch (Exception ex)
                    {
                        logCallback?.Invoke($"Failed to kill process {proc.Id}: {ex.Message}");
                    }
                    finally
                    {
                        proc.Dispose();
                    }
                }
            }
            catch { }
        }
    }

    /// <summary>
    /// Synchronous version of KillGameProcesses for use in AppDomain.ProcessExit handler.
    /// ProcessExit has a ~2s time budget, so we use synchronous waits with short timeouts.
    /// </summary>
    private static void KillGameProcessesSync()
    {
        var processNames = new[] { "StardewModdingAPI", "Stardew Valley" };

        foreach (var name in processNames)
        {
            try
            {
                var processes = Process.GetProcessesByName(name);
                foreach (var proc in processes)
                {
                    try
                    {
                        proc.Kill(entireProcessTree: true);
                        proc.WaitForExit(1000);
                    }
                    catch { }
                    finally
                    {
                        proc.Dispose();
                    }
                }
            }
            catch { }
        }
    }

    private static void TailLogFile(
        string filePath,
        CancellationToken ct,
        Action<string>? logCallback
    )
    {
        try
        {
            while (!File.Exists(filePath) && !ct.IsCancellationRequested)
            {
                Thread.Sleep(100);
            }

            if (ct.IsCancellationRequested)
                return;

            using var fs = new FileStream(
                filePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite
            );
            using var sr = new StreamReader(fs);

            while (!ct.IsCancellationRequested)
            {
                var line = sr.ReadLine();
                if (line != null)
                {
                    var cleanedLine = CleanLine(line);
                    if (cleanedLine.Length > 0)
                    {
                        _outputLog.AppendLine(cleanedLine);
                    }
                }
                else
                {
                    Thread.Sleep(50);
                }
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logCallback?.Invoke($"Log tail error: {ex}");
        }
    }

    private static string CleanLine(string? input)
    {
        if (string.IsNullOrEmpty(input))
            return "";

        var result = Regex.Replace(input, @"\x1B\[[0-9;]*[a-zA-Z]", "");
        result = result.Replace("\r", "").Replace("\n", "");
        result = new string(result.Where(c => !char.IsControl(c)).ToArray());
        result = Regex.Replace(result, @"\d{2}:\d{2}:\d{2}(\.\d+)?\s*", "");
        result = Regex.Replace(
            result,
            @"\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}(\.\d+)?(Z|[+-]\d{2}:?\d{2})?\s*",
            ""
        );
        result = Regex.Replace(result, @"\[(\S+)\s+\]", "[$1]");
        result = result.Replace(
            @"D:\GitlabRunner\builds\Gq5qA5P4\0\ConcernedApe\stardewvalley",
            "StardewValley"
        );
        result = result.Replace(ProjectParentDir, "");

        return result.TrimEnd();
    }
}
