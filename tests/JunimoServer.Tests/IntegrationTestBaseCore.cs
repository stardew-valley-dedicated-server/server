using JunimoServer.Tests.Clients;
using JunimoServer.Tests.Fixtures;
using JunimoServer.Tests.Helpers;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using Xunit;
using Xunit.Abstractions;
using Xunit.Extensions.AssemblyFixture;

namespace JunimoServer.Tests;

/// <summary>
/// Screenshot capture mode for tests.
/// Controlled by SDVD_TEST_SCREENSHOTS environment variable.
/// </summary>
public enum ScreenshotMode
{
    /// <summary>No screenshots captured.</summary>
    None,
    /// <summary>Capture screenshot only on test failure.</summary>
    Failure,
    /// <summary>Capture screenshots at checkpoints and on failure.</summary>
    Checkpoints,
    /// <summary>Capture all screenshots (checkpoints, failure, and any explicit captures).</summary>
    All
}

/// <summary>
/// Generic base class for integration tests that provides:
/// - Connection helpers with retry logic
/// - Exception monitoring with early abort
/// - Unified logging (both game and server logs)
/// - Clear test output formatting
/// - Automatic cleanup
///
/// This is the shared implementation used by both IntegrationTestBase (password-enabled)
/// and NoPasswordTestBase (password-disabled).
/// </summary>
/// <typeparam name="TFixture">The fixture type (IntegrationTestFixture or NoPasswordFixture)</typeparam>
public abstract class IntegrationTestBaseCore<TFixture> : IAsyncLifetime, IDisposable, IAssemblyFixture<TestSummaryFixture>
    where TFixture : IntegrationTestFixture
{
    protected readonly TFixture Fixture;
    protected readonly ITestOutputHelper Output;
    protected readonly GameTestClient GameClient;
    protected readonly ServerApiClient ServerApi;
    protected readonly ConnectionHelper Connection;
    protected readonly ExceptionMonitor Exceptions;

    private readonly string _testClassName;
    private string? _currentTestName;
    private readonly DateTime _testStartTime;
    private bool _didConnect;
    private bool _testFailed;
    private int _checkpointCounter;
    private readonly TestLogger _logger;
    private JsonlReporter? _jsonlReporter;

    /// <summary>
    /// Screenshot capture mode. Defaults to Failure if SDVD_TEST_SCREENSHOTS is not set.
    /// </summary>
    protected static ScreenshotMode ScreenshotMode { get; } = ParseScreenshotMode();

    private static ScreenshotMode ParseScreenshotMode()
    {
        var value = Environment.GetEnvironmentVariable("SDVD_TEST_SCREENSHOTS");
        return value?.ToLowerInvariant() switch
        {
            "none" or "off" or "false" or "0" => ScreenshotMode.None,
            "failure" or "fail" => ScreenshotMode.Failure,
            "checkpoints" or "checkpoint" => ScreenshotMode.Checkpoints,
            "all" or "true" or "1" => ScreenshotMode.All,
            _ => ScreenshotMode.Failure // Default to capturing on failure
        };
    }

    /// <summary>
    /// Returns true if verbose logging is enabled via SDVD_TEST_VERBOSE environment variable.
    /// </summary>
    protected static bool VerboseLogging => TestLogger.VerboseLogging;

    /// <summary>
    /// Gets the current server status. Populated during InitializeAsync.
    /// </summary>
    protected ServerStatus? ServerStatus { get; private set; }

    /// <summary>
    /// Gets the server invite code. Convenience property.
    /// </summary>
    protected string InviteCode => ServerStatus?.InviteCode ?? Fixture.InviteCode ?? "";

    /// <summary>
    /// Farmers created during this test, tracked for cleanup.
    /// </summary>
    protected readonly List<string> CreatedFarmers = new();

    /// <summary>
    /// If true, exception monitoring will abort tests on detected exceptions.
    /// Override in constructor or InitializeAsync to disable.
    /// Default is true.
    /// </summary>
    protected bool AbortOnException
    {
        get => _exceptionMonitorOptions.AbortOnException;
        set => _exceptionMonitorOptions.AbortOnException = value;
    }

    private readonly ExceptionMonitorOptions _exceptionMonitorOptions;
    private readonly ConnectionRetryOptions _connectionOptions;

    protected IntegrationTestBaseCore(
        TFixture fixture,
        ITestOutputHelper output,
        ConnectionRetryOptions? connectionOptions = null,
        ExceptionMonitorOptions? exceptionOptions = null)
    {
        Fixture = fixture;
        Output = output;
        _testStartTime = DateTime.UtcNow;
        _testClassName = GetType().Name;

        _connectionOptions = connectionOptions ?? ConnectionRetryOptions.Default;
        _exceptionMonitorOptions = exceptionOptions ?? ExceptionMonitorOptions.Default;

        // Set server password for auto-login if not already set
        if (_connectionOptions.ServerPassword == null)
        {
            _connectionOptions.ServerPassword = fixture.ServerPassword;
        }

        GameClient = new GameTestClient(fixture.GameClientBaseUrl);
        ServerApi = new ServerApiClient(fixture.ServerBaseUrl);

        Connection = new ConnectionHelper(
            GameClient,
            _connectionOptions,
            output,
            ServerApi);

        Exceptions = new ExceptionMonitor(
            GameClient,
            fixture.ServerContainer,
            _exceptionMonitorOptions,
            msg => Log(msg));

        _logger = new TestLogger("[Test]", output);
    }

    /// <summary>
    /// Called before each test. Gets server status and clears exception monitor.
    /// Override to add additional setup, but call base.InitializeAsync().
    /// </summary>
    public virtual async Task InitializeAsync()
    {
        // Try to extract test name from the xUnit output helper
        _currentTestName = ExtractTestName();

        // Initialize JSONL reporter if enabled
        if (JsonlReporter.IsEnabled)
        {
            _jsonlReporter = new JsonlReporter(_currentTestName ?? $"{_testClassName}.unknown");
            _jsonlReporter.Info("Test started");
        }

        // Track test count for summary
        Fixture.RegisterTest(_testClassName, _currentTestName);

        // Print test header
        PrintTestHeader();

        // Check if test run was aborted by a previous test
        if (Fixture.IsTestRunAborted)
        {
            Log($"SKIPPED: Test run was aborted");
            Log($"Reason: {Fixture.AbortReason}");
            PrintTestFooter();
            Fixture.ThrowIfAborted();
        }

        // Clear any errors from previous tests and set up error cancellation
        Exceptions.Clear();
        Fixture.ClearServerErrors();
        GameClient.SetErrorCancellationToken(Fixture.GetErrorCancellationToken());
        try { await GameClient.ClearErrors(); } catch { }

        // Get current server status
        ServerStatus = await ServerApi.GetStatus();

        LogDetail($"Server: {(ServerStatus?.IsOnline == true ? "Online" : "Offline")}, InviteCode: {InviteCode}");
    }

    /// <summary>
    /// Called after each test. Cleans up created farmers and checks for exceptions.
    /// Override to add additional cleanup, but call base.DisposeAsync().
    /// </summary>
    public virtual async Task DisposeAsync()
    {
        _jsonlReporter?.SetPhase("cleanup");

        // Capture failure screenshot if test failed and screenshots are enabled
        if (_testFailed && ScreenshotMode != ScreenshotMode.None)
        {
            var screenshotPath = await CaptureScreenshotAsync("failure");
            _jsonlReporter?.Error("Test failed", screenshotPath: screenshotPath);
        }

        // Only do game client cleanup if we actually connected during this test
        if (_didConnect)
        {
            // Return to title screen and ensure fully disconnected
            try
            {
                await GameClient.Navigate("title");
                await GameClient.Wait.ForDisconnected(TestTimings.DisconnectedTimeout);

                // Wait for server to process the disconnect (so next test sees 0 players)
                await PollingHelper.WaitUntilAsync(async () =>
                {
                    var players = await ServerApi.GetPlayers();
                    return players?.Players?.Count == 0;
                }, TestTimings.FarmerDeleteTimeout);
            }
            catch (Exception ex)
            {
                LogWarning($"Failed to return to title [{ex.GetType().Name}]: {ex.Message}");
                _jsonlReporter?.Warning($"Failed to return to title: {ex.Message}");
            }
        }

        // Clean up any farmers created during tests (with retry logic)
        foreach (var farmerName in CreatedFarmers)
        {
            await DeleteFarmerAsync(farmerName);
        }

        // Final exception check (will log but not throw since test is ending)
        try
        {
            await Exceptions.CheckGameClientErrorsAsync();
            var exceptions = Exceptions.GetExceptions();
            if (exceptions.Count > 0)
            {
                WriteLine("");
                LogWarning($"{exceptions.Count} exception(s) occurred during test:");
                foreach (var ex in exceptions)
                {
                    LogDetail(ex.ToString());
                    _jsonlReporter?.Error($"Exception during test: {ex}");
                }
            }
        }
        catch { }

        // Log test completion
        var duration = DateTime.UtcNow - _testStartTime;
        _jsonlReporter?.Info(_testFailed ? "Test completed with failure" : "Test completed successfully",
            new { DurationMs = (long)duration.TotalMilliseconds, Failed = _testFailed });
        _jsonlReporter?.Dispose();

        // Print test footer
        PrintTestFooter();
    }

    /// <summary>
    /// Marks the test as failed. Called automatically by assertion helpers.
    /// Can also be called manually to trigger failure screenshot capture.
    /// </summary>
    protected void MarkTestFailed() => _testFailed = true;

    /// <summary>
    /// Records a test failure with details for the failure summary JSON.
    /// </summary>
    private void RecordFailure(string error, string? phase = null, string? screenshotPath = null)
    {
        _testFailed = true;
        Fixture.RecordFailure(_testClassName, _currentTestName, error, phase, screenshotPath);
        _jsonlReporter?.Error(error, new { Phase = phase }, screenshotPath);
    }

    /// <summary>
    /// Captures a screenshot from the server container's display.
    /// Screenshots are saved to TestResults/Screenshots/{TestClass}/{TestMethod}/{label}.png
    /// Works with both Testcontainers-managed containers and externally-started containers.
    /// </summary>
    /// <param name="label">Label for the screenshot file (e.g., "failure", "01_after_connect")</param>
    /// <returns>The path to the saved screenshot, or null if capture failed</returns>
    protected async Task<string?> CaptureScreenshotAsync(string label)
    {
        try
        {
            var image = await VncScreenshotHelper.CaptureScreenshot(Fixture.ServerContainer);
            var testMethod = ExtractMethodName(_currentTestName) ?? "unknown";
            await VncScreenshotHelper.SaveScreenshot(image, _testClassName, testMethod, label);
            image.Dispose();

            var dir = TestArtifacts.GetScreenshotDir(_testClassName, testMethod);
            var path = Path.Combine(dir, $"{label}.png");
            return path;
        }
        catch (Exception ex)
        {
            LogWarning($"Failed to capture screenshot [{ex.GetType().Name}]: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Extracts just the method name from the full test display name.
    /// e.g., "JunimoServer.Tests.PasswordProtectionTests.Login_WithCorrectPassword" -> "Login_WithCorrectPassword"
    /// </summary>
    private static string? ExtractMethodName(string? fullName)
    {
        if (string.IsNullOrEmpty(fullName)) return null;
        var lastDot = fullName.LastIndexOf('.');
        return lastDot >= 0 ? fullName[(lastDot + 1)..] : fullName;
    }

    /// <summary>
    /// Deletes a farmer, polling if the server hasn't processed the disconnect yet.
    /// Tries immediately (no upfront delay) and retries with tight polling on
    /// "currently online" errors until FarmerDeleteTimeout.
    /// </summary>
    private async Task DeleteFarmerAsync(string farmerName)
    {
        var deadline = DateTime.UtcNow + TestTimings.FarmerDeleteTimeout;

        while (true)
        {
            try
            {
                var result = await ServerApi.DeleteFarmhand(farmerName);
                if (result?.Success == true)
                {
                    LogDetail($"Deleted: {farmerName}");
                    return;
                }

                if (result?.Error?.Contains("not found", StringComparison.OrdinalIgnoreCase) == true)
                {
                    LogDetail($"Not found (ok): {farmerName}");
                    return;
                }

                // Retryable errors: farmer still online, or save in progress
                var isRetryable = result?.Error?.Contains("online", StringComparison.OrdinalIgnoreCase) == true
                    || result?.Error?.Contains("save", StringComparison.OrdinalIgnoreCase) == true;

                if (isRetryable && DateTime.UtcNow < deadline)
                {
                    await Task.Delay(TestTimings.FastPollInterval);
                    continue;
                }

                LogWarning($"Failed to delete {farmerName}: {result?.Error ?? "unknown error"}");
                return;
            }
            catch (Exception ex)
            {
                LogWarning($"Error deleting {farmerName} [{ex.GetType().Name}]: {ex.Message}");
                return;
            }
        }
    }

    /// <summary>
    /// Disposes of clients.
    /// </summary>
    public void Dispose()
    {
        GameClient.Dispose();
        ServerApi.Dispose();
        Exceptions.Dispose();
    }

    #region Output Formatting

    private string? ExtractTestName()
    {
        // xUnit doesn't directly expose test name to ITestOutputHelper,
        // but we can try to get it from the type
        try
        {
            var field = Output.GetType().GetField("test", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            if (field?.GetValue(Output) is Xunit.Abstractions.ITest test)
            {
                return test.DisplayName;
            }
        }
        catch { }
        return null;
    }

    private void PrintTestHeader()
    {
        var testDisplayName = _currentTestName ?? $"{_testClassName}.???";
        // Extract just ClassName.MethodName from full namespace
        var parts = testDisplayName.Split('.');
        var shortName = parts.Length >= 2
            ? $"{parts[^2]}.{parts[^1]}"
            : testDisplayName;

        // Write to console (shown in terminal with detailed verbosity)
        IntegrationTestFixture.LogTestPhase("Test", shortName);
    }

    private void PrintTestFooter()
    {
        var duration = DateTime.UtcNow - _testStartTime;

        // Record duration in unified summary
        Fixture.CompleteTest(_testClassName, _currentTestName, duration);

        // Write to console with color treatment matching fixture style
        LogSuccess($"Done ({duration.TotalSeconds:F2}s)");
        IntegrationTestFixture.LogTestMessage("");
    }

    /// <summary>
    /// Write a line to console output.
    /// </summary>
    private void WriteLine(string message)
    {
        Console.Out.WriteLine(message);
        Console.Out.Flush();
    }

    /// <summary>
    /// Log a message to console with [Test] prefix.
    /// </summary>
    protected void Log(string message)
    {
        _logger.Log(message);
        _jsonlReporter?.Info(message);
    }

    /// <summary>
    /// Log a success message with icon.
    /// </summary>
    protected void LogSuccess(string message)
    {
        _logger.LogSuccess(message);
        _jsonlReporter?.Success(message);
    }

    /// <summary>
    /// Log a warning message with icon.
    /// </summary>
    protected void LogWarning(string message)
    {
        _logger.LogWarning(message);
        _jsonlReporter?.Warning(message);
    }

    /// <summary>
    /// Log an error message with icon.
    /// </summary>
    protected void LogError(string message)
    {
        _logger.LogError(message);
        _jsonlReporter?.Error(message);
    }

    /// <summary>
    /// Log a detail message with icon.
    /// </summary>
    protected void LogDetail(string message)
    {
        _logger.LogDetail(message);
        _jsonlReporter?.Info(message);  // Detail maps to info in JSONL
    }

    /// <summary>
    /// Log a section header.
    /// </summary>
    protected void LogSection(string title)
    {
        _logger.LogSection(title);
        _jsonlReporter?.Info(title);  // Section maps to info in JSONL
    }

    #endregion

    #region Helper Methods

    /// <summary>
    /// Checks if an OperationCanceledException was caused by a server error.
    /// If so, throws an ExceptionMonitorException with the server error details.
    /// Otherwise re-throws the original exception.
    /// </summary>
    protected void ThrowIfServerError(OperationCanceledException ex, string? context = null)
    {
        var serverErrors = Fixture.GetServerErrors();
        if (serverErrors.Count > 0)
        {
            var errorList = string.Join("\n", serverErrors);
            var reason = context != null
                ? $"Server error during: {context}"
                : "Server error detected";
            var message = $"{reason}\n\n{errorList}";
            RecordFailure(message, context);
            Log($"SERVER ERROR DETECTED - aborting test run");
            foreach (var error in serverErrors)
            {
                Log($"  {error}");
            }
            Fixture.AbortTestRun(message);
            throw new ExceptionMonitorException(message, Array.Empty<CapturedException>());
        }
        // Not a server error cancellation - re-throw
        RecordFailure(ex.Message, context);
        throw ex;
    }

    /// <summary>
    /// Checks for exceptions and throws if AbortOnException is enabled.
    /// Also aborts the entire test run so remaining tests are skipped.
    /// Call this at key points during a test to fail fast on errors.
    /// </summary>
    /// <param name="context">Description of what was being done when checking.</param>
    protected async Task AssertNoExceptionsAsync(string? context = null)
    {
        // Check for server errors detected by the fixture's log streaming
        var serverErrors = Fixture.GetServerErrors();
        if (serverErrors.Count > 0)
        {
            var errorList = string.Join("\n", serverErrors);
            var reason = context != null
                ? $"Server error during: {context}"
                : "Server error detected";
            var message = $"{reason}\n\n{errorList}";
            RecordFailure(message, context);
            Fixture.AbortTestRun(message);
            throw new ExceptionMonitorException(message, Array.Empty<CapturedException>());
        }

        try
        {
            await Exceptions.AssertNoExceptionsAsync(context);
        }
        catch (ExceptionMonitorException ex)
        {
            // Abort the entire test run
            var reason = context != null
                ? $"Exception during: {context}"
                : "Exception detected";
            RecordFailure(ex.Message, context);
            Fixture.AbortTestRun($"{reason}\n{ex.Message}");
            throw;
        }
    }

    /// <summary>
    /// Creates a scope that will check for exceptions when disposed.
    /// Also aborts the entire test run if exceptions are detected.
    /// If ScreenshotMode is Checkpoints or All, captures a screenshot after the checkpoint.
    /// Usage: await using (await CheckpointAsync("joining server")) { ... }
    /// </summary>
    /// <param name="context">Description of this checkpoint (used in logs and screenshot filename)</param>
    /// <param name="captureScreenshot">Override screenshot capture. If null, uses ScreenshotMode setting.</param>
    protected async Task<TestCheckpoint> CheckpointAsync(string context, bool? captureScreenshot = null)
    {
        // Check for exceptions at start of checkpoint
        await AssertNoExceptionsAsync($"before {context}");
        _checkpointCounter++;
        _jsonlReporter?.SetPhase(context.Replace(" ", "_").ToLowerInvariant());
        _jsonlReporter?.Info($"Checkpoint: {context}", new { CheckpointNumber = _checkpointCounter });
        return new TestCheckpoint(this, context, _checkpointCounter, captureScreenshot, _jsonlReporter);
    }

    /// <summary>
    /// Checkpoint scope that checks for exceptions and aborts the test run when disposed.
    /// Optionally captures a screenshot after the checkpoint completes.
    /// </summary>
    protected class TestCheckpoint : IAsyncDisposable
    {
        private readonly IntegrationTestBaseCore<TFixture> _test;
        private readonly string _context;
        private readonly int _checkpointNumber;
        private readonly bool? _captureScreenshotOverride;
        private readonly JsonlReporter? _jsonlReporter;

        public TestCheckpoint(IntegrationTestBaseCore<TFixture> test, string context, int checkpointNumber, bool? captureScreenshot, JsonlReporter? jsonlReporter)
        {
            _test = test;
            _context = context;
            _checkpointNumber = checkpointNumber;
            _captureScreenshotOverride = captureScreenshot;
            _jsonlReporter = jsonlReporter;
        }

        public async ValueTask DisposeAsync()
        {
            await _test.AssertNoExceptionsAsync($"during {_context}");

            // Determine if we should capture a screenshot
            var shouldCapture = _captureScreenshotOverride ??
                (ScreenshotMode == ScreenshotMode.Checkpoints || ScreenshotMode == ScreenshotMode.All);

            string? screenshotPath = null;
            if (shouldCapture)
            {
                // Create a safe filename from the context
                var safeContext = _context.Replace(" ", "_").Replace("/", "-");
                var label = $"{_checkpointNumber:D2}_{safeContext}";
                screenshotPath = await _test.CaptureScreenshotAsync(label);
            }

            _jsonlReporter?.Success($"Checkpoint complete: {_context}",
                new { CheckpointNumber = _checkpointNumber },
                screenshotPath);
        }
    }

    /// <summary>
    /// Temporarily disables exception abort for expected error scenarios.
    /// Usage: using (SuppressExceptionAbort()) { ... code that may throw ... }
    /// </summary>
    protected ExceptionMonitor.ExceptionSuppressionScope SuppressExceptionAbort() =>
        Exceptions.SuppressAbort();

    /// <summary>
    /// Tracks a farmer name for cleanup after the test.
    /// </summary>
    protected void TrackFarmer(string name) => CreatedFarmers.Add(name);

    /// <summary>
    /// Generates a unique farmer name for testing.
    /// </summary>
    protected string GenerateFarmerName(string prefix = "Test") =>
        $"{prefix}{DateTime.UtcNow.Ticks % 10000}";

    /// <summary>
    /// Ensures the game client is disconnected and at the title screen.
    /// Throws ExceptionMonitorException if a server error is detected.
    /// </summary>
    protected async Task<bool> EnsureDisconnectedAsync(TimeSpan? timeout = null)
    {
        try
        {
            return await Connection.EnsureDisconnectedAsync(timeout ?? TestTimings.DisconnectedTimeout);
        }
        catch (OperationCanceledException ex)
        {
            ThrowIfServerError(ex, "ensuring disconnected");
            throw; // Not a server error
        }
    }

    /// <summary>
    /// Disconnects from the server by exiting and returning to title screen.
    /// Includes assertions to verify success.
    /// </summary>
    protected async Task DisconnectAsync()
    {
        var exitResult = await GameClient.Exit();
        Assert.True(exitResult?.Success, $"Exit failed: {exitResult?.Error}");

        await GameClient.Wait.ForTitle(TestTimings.TitleScreenTimeout);
        await GameClient.Wait.ForDisconnected(TestTimings.DisconnectedTimeout);
        Log("Disconnected from server");
    }

    /// <summary>
    /// Connects to the server with automatic retry on "stuck connecting" issues.
    /// Throws ExceptionMonitorException if a server error is detected.
    /// </summary>
    protected async Task<ConnectionResult> ConnectWithRetryAsync(CancellationToken ct = default)
    {
        _jsonlReporter?.SetPhase("connect");
        try
        {
            var result = await Connection.ConnectToServerAsync(InviteCode, ct);
            if (result.Success)
            {
                _didConnect = true;
                _jsonlReporter?.Success("Connected to server",
                    new { InviteCode, AttemptsUsed = result.AttemptsUsed });
            }
            else
            {
                _jsonlReporter?.Error("Connection failed",
                    new { InviteCode, AttemptsUsed = result.AttemptsUsed, Error = result.Error });
            }
            return result;
        }
        catch (OperationCanceledException ex)
        {
            ThrowIfServerError(ex, "connecting to server");
            throw; // Not a server error
        }
    }

    /// <summary>
    /// Connects to server and joins the game world with automatic retry.
    /// If password protection is enabled, automatically authenticates.
    /// Throws ExceptionMonitorException if a server error is detected.
    /// </summary>
    protected async Task<JoinWorldResult> JoinWorldWithRetryAsync(
        string farmerName,
        string favoriteThing = "Testing",
        bool preferExistingFarmer = true,
        CancellationToken ct = default)
    {
        _jsonlReporter?.SetPhase("connect");
        try
        {
            var result = await Connection.JoinWorldAsync(InviteCode, farmerName, favoriteThing, preferExistingFarmer, skipAutoLogin: false, ct);
            if (result.Success)
            {
                _didConnect = true;
                _jsonlReporter?.Success("Joined world",
                    new { FarmerName = farmerName, InviteCode, AttemptsUsed = result.AttemptsUsed });
            }
            else
            {
                _jsonlReporter?.Error("Join world failed",
                    new { FarmerName = farmerName, InviteCode, AttemptsUsed = result.AttemptsUsed, Error = result.Error });
            }
            return result;
        }
        catch (OperationCanceledException ex)
        {
            ThrowIfServerError(ex, "joining world");
            throw; // Not a server error
        }
    }

    /// <summary>
    /// Asserts that a connection result was successful.
    /// Marks test as failed if assertion fails (for screenshot capture).
    /// </summary>
    protected void AssertConnectionSuccess(ConnectionResult result)
    {
        if (!result.Success)
        {
            RecordFailure($"Connection failed after {result.AttemptsUsed} attempt(s): {result.Error}", "connect");
        }
        Assert.True(result.Success,
            $"Connection failed after {result.AttemptsUsed} attempt(s): {result.Error}");
    }

    /// <summary>
    /// Asserts that a join world result was successful.
    /// Marks test as failed if assertion fails (for screenshot capture).
    /// </summary>
    protected void AssertJoinSuccess(JoinWorldResult result)
    {
        if (!result.Success)
        {
            RecordFailure($"Join world failed after {result.AttemptsUsed} attempt(s): {result.Error}", "connect");
        }
        Assert.True(result.Success,
            $"Join world failed after {result.AttemptsUsed} attempt(s): {result.Error}");
    }

    /// <summary>
    /// Marks that the test connected to the server (for cleanup tracking).
    /// </summary>
    protected void MarkConnected() => _didConnect = true;

    #endregion
}
