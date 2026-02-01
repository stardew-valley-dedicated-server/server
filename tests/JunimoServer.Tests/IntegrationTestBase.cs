using JunimoServer.Tests.Clients;
using JunimoServer.Tests.Fixtures;
using JunimoServer.Tests.Helpers;
using Spectre.Console;
using System.Runtime.CompilerServices;
using Xunit;
using Xunit.Abstractions;

namespace JunimoServer.Tests;

/// <summary>
/// Base class for integration tests that provides:
/// - Connection helpers with retry logic
/// - Exception monitoring with early abort
/// - Unified logging (both game and server logs)
/// - Clear test output formatting
/// - Automatic cleanup
///
/// Usage:
/// [Collection("Integration")]
/// public class MyTests : IntegrationTestBase
/// {
///     public MyTests(IntegrationTestFixture fixture, ITestOutputHelper output)
///         : base(fixture, output) { }
///
///     [Fact]
///     public async Task MyTest()
///     {
///         // Connect with retry
///         var result = await Connection.ConnectToServerAsync(ServerStatus!.InviteCode);
///         Assert.True(result.Success);
///
///         // Check for exceptions at key points
///         await AssertNoExceptionsAsync("after connecting");
///     }
/// }
/// </summary>
public abstract class IntegrationTestBase : IAsyncLifetime, IDisposable
{
    protected readonly IntegrationTestFixture Fixture;
    protected readonly ITestOutputHelper Output;
    protected readonly GameTestClient GameClient;
    protected readonly ServerApiClient ServerApi;
    protected readonly ConnectionHelper Connection;
    protected readonly ExceptionMonitor Exceptions;

    private readonly string _testClassName;
    private string? _currentTestName;
    private readonly DateTime _testStartTime;

    // Test output formatting configuration - mirrors fixture settings
    private static readonly bool UseIcons = !string.Equals(
        Environment.GetEnvironmentVariable("SDVD_TEST_ICONS"), "false", StringComparison.OrdinalIgnoreCase);

    // Unicode status icons
    private static readonly string IconSuccess = UseIcons ? "✓" : "[OK]";
    private static readonly string IconError = UseIcons ? "✗" : "[ERROR]";
    private static readonly string IconWarning = UseIcons ? "!" : "[WARN]";
    private static readonly string IconDetail = UseIcons ? "→" : "->";

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

    protected IntegrationTestBase(
        IntegrationTestFixture fixture,
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

        GameClient = new GameTestClient(fixture.GameClientBaseUrl);
        ServerApi = new ServerApiClient(fixture.ServerBaseUrl);

        Connection = new ConnectionHelper(
            GameClient,
            _connectionOptions,
            output);

        Exceptions = new ExceptionMonitor(
            GameClient,
            fixture.ServerContainer,
            _exceptionMonitorOptions,
            msg => Log(msg));
    }

    /// <summary>
    /// Called before each test. Gets server status and clears exception monitor.
    /// Override to add additional setup, but call base.InitializeAsync().
    /// </summary>
    public virtual async Task InitializeAsync()
    {
        // Try to extract test name from the xUnit output helper
        _currentTestName = ExtractTestName();

        // Track test count for summary
        Fixture.RegisterTest(_testClassName);

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
        LogDetail("Cleaning up...");

        // Return to title screen and ensure fully disconnected
        try
        {
            await GameClient.Navigate("title");
            await GameClient.Wait.ForDisconnected(TestTimings.DisconnectedTimeout);
            // Wait for server to process the disconnect before attempting farmer deletion
            await Task.Delay(TestTimings.DisconnectProcessingDelay);
        }
        catch (Exception ex)
        {
            LogWarning($"Failed to return to title: {ex.Message}");
        }

        // Clean up any farmers created during tests (with retry logic)
        if (CreatedFarmers.Count > 0)
        {
            Log($"Cleaning up {CreatedFarmers.Count} farmer(s)...");
            foreach (var farmerName in CreatedFarmers)
            {
                await DeleteFarmerWithRetry(farmerName);
            }
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
                }
            }
        }
        catch { }

        // Print test footer
        PrintTestFooter();
    }

    /// <summary>
    /// Attempts to delete a farmer with retry logic for "currently online" errors.
    /// </summary>
    private async Task DeleteFarmerWithRetry(string farmerName)
    {
        for (var attempt = 1; attempt <= TestTimings.FarmerDeleteMaxAttempts; attempt++)
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

                // Check if farmer is still online - retry after delay
                if (result?.Error?.Contains("online", StringComparison.OrdinalIgnoreCase) == true)
                {
                    if (attempt < TestTimings.FarmerDeleteMaxAttempts)
                    {
                        LogDetail($"Farmer '{farmerName}' still online, retrying in {TestTimings.FarmerDeleteRetryDelay.TotalSeconds}s...");
                        await Task.Delay(TestTimings.FarmerDeleteRetryDelay);
                        continue;
                    }
                }

                LogWarning($"Failed to delete {farmerName}: {result?.Error ?? "unknown error"}");
                return;
            }
            catch (Exception ex)
            {
                LogWarning($"Error deleting {farmerName}: {ex.Message}");
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

    private const string TestPrefix = "[Test]";

    /// <summary>
    /// Log a message to console with [Test] prefix.
    /// </summary>
    protected void Log(string message)
    {
        AnsiConsole.MarkupLine($"{Markup.Escape(TestPrefix)} {Markup.Escape(message)}");
    }

    /// <summary>
    /// Log a success message with icon.
    /// </summary>
    protected void LogSuccess(string message)
    {
        AnsiConsole.MarkupLine($"{Markup.Escape(TestPrefix)} [green]{IconSuccess} {Markup.Escape(message)}[/]");
    }

    /// <summary>
    /// Log a warning message with icon.
    /// </summary>
    protected void LogWarning(string message)
    {
        AnsiConsole.MarkupLine($"{Markup.Escape(TestPrefix)} [yellow]{IconWarning} {Markup.Escape(message)}[/]");
    }

    /// <summary>
    /// Log an error message with icon.
    /// </summary>
    protected void LogError(string message)
    {
        AnsiConsole.MarkupLine($"{Markup.Escape(TestPrefix)} [red]{IconError} {Markup.Escape(message)}[/]");
    }

    /// <summary>
    /// Log a detail message with icon.
    /// </summary>
    protected void LogDetail(string message)
    {
        AnsiConsole.MarkupLine($"{Markup.Escape(TestPrefix)} [grey]{IconDetail} {Markup.Escape(message)}[/]");
    }

    /// <summary>
    /// Log a section header.
    /// </summary>
    protected void LogSection(string title)
    {
        AnsiConsole.MarkupLine($"{Markup.Escape(TestPrefix)} [bold]{Markup.Escape(title)}[/]");
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
            Log($"SERVER ERROR DETECTED - aborting test run");
            foreach (var error in serverErrors)
            {
                Log($"  {error}");
            }
            Fixture.AbortTestRun(message);
            throw new ExceptionMonitorException(message, Array.Empty<CapturedException>());
        }
        // Not a server error cancellation - re-throw
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
            Fixture.AbortTestRun($"{reason}\n{ex.Message}");
            throw;
        }
    }

    /// <summary>
    /// Creates a scope that will check for exceptions when disposed.
    /// Also aborts the entire test run if exceptions are detected.
    /// Usage: await using (await CheckpointAsync("joining server")) { ... }
    /// </summary>
    protected async Task<TestCheckpoint> CheckpointAsync(string context)
    {
        // Check for exceptions at start of checkpoint
        await AssertNoExceptionsAsync($"before {context}");
        return new TestCheckpoint(this, context);
    }

    /// <summary>
    /// Checkpoint scope that checks for exceptions and aborts the test run when disposed.
    /// </summary>
    protected class TestCheckpoint : IAsyncDisposable
    {
        private readonly IntegrationTestBase _test;
        private readonly string _context;

        public TestCheckpoint(IntegrationTestBase test, string context)
        {
            _test = test;
            _context = context;
        }

        public async ValueTask DisposeAsync()
        {
            await _test.AssertNoExceptionsAsync($"during {_context}");
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
        try
        {
            return await Connection.ConnectToServerAsync(InviteCode, ct);
        }
        catch (OperationCanceledException ex)
        {
            ThrowIfServerError(ex, "connecting to server");
            throw; // Not a server error
        }
    }

    /// <summary>
    /// Connects to server and joins the game world with automatic retry.
    /// Throws ExceptionMonitorException if a server error is detected.
    /// </summary>
    protected async Task<JoinWorldResult> JoinWorldWithRetryAsync(
        string farmerName,
        string favoriteThing = "Testing",
        bool preferExistingFarmer = true,
        CancellationToken ct = default)
    {
        try
        {
            return await Connection.JoinWorldAsync(InviteCode, farmerName, favoriteThing, preferExistingFarmer, ct);
        }
        catch (OperationCanceledException ex)
        {
            ThrowIfServerError(ex, "joining world");
            throw; // Not a server error
        }
    }

    /// <summary>
    /// Asserts that a connection result was successful.
    /// </summary>
    protected void AssertConnectionSuccess(ConnectionResult result)
    {
        Assert.True(result.Success,
            $"Connection failed after {result.AttemptsUsed} attempt(s): {result.Error}");
    }

    /// <summary>
    /// Asserts that a join world result was successful.
    /// </summary>
    protected void AssertJoinSuccess(JoinWorldResult result)
    {
        Assert.True(result.Success,
            $"Join world failed after {result.AttemptsUsed} attempt(s): {result.Error}");
    }

    #endregion
}
