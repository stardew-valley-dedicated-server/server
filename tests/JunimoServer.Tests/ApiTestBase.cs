using JunimoServer.Tests.Clients;
using JunimoServer.Tests.Fixtures;
using JunimoServer.Tests.Helpers;
using Xunit;
using Xunit.Abstractions;
using Xunit.Extensions.AssemblyFixture;

namespace JunimoServer.Tests;

/// <summary>
/// Lightweight base class for tests that only need HTTP API access.
/// No game client connection/disconnection overhead.
/// Use IntegrationTestBase if you need to connect a player via the game client.
/// </summary>
public abstract class ApiTestBase : IAsyncLifetime, IDisposable, IAssemblyFixture<TestSummaryFixture>
{
    protected readonly IntegrationTestFixture Fixture;
    protected readonly ITestOutputHelper Output;
    protected readonly ServerApiClient ServerApi;

    private readonly string _testClassName;
    private string? _currentTestName;
    private readonly DateTime _testStartTime;
    private readonly TestLogger _logger;

    /// <summary>
    /// Returns true if verbose logging is enabled via SDVD_TEST_VERBOSE environment variable.
    /// </summary>
    protected static bool VerboseLogging => TestLogger.VerboseLogging;

    protected ApiTestBase(IntegrationTestFixture fixture, ITestOutputHelper output)
    {
        Fixture = fixture;
        Output = output;
        _testStartTime = DateTime.UtcNow;
        _testClassName = GetType().Name;

        ServerApi = new ServerApiClient(fixture.ServerBaseUrl);
        _logger = new TestLogger("[Test]", output);
    }

    public virtual async Task InitializeAsync()
    {
        _currentTestName = ExtractTestName();

        Fixture.RegisterTest(_testClassName, _currentTestName);

        PrintTestHeader();

        if (Fixture.IsTestRunAborted)
        {
            Log($"SKIPPED: Test run was aborted");
            Log($"Reason: {Fixture.AbortReason}");
            PrintTestFooter();
            Fixture.ThrowIfAborted();
        }

        Fixture.ClearServerErrors();

        await Task.CompletedTask;
    }

    public virtual Task DisposeAsync()
    {
        PrintTestFooter();
        return Task.CompletedTask;
    }

    public void Dispose() => ServerApi.Dispose();

    #region Output Formatting

    private string? ExtractTestName()
    {
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
        var parts = testDisplayName.Split('.');
        var shortName = parts.Length >= 2
            ? $"{parts[^2]}.{parts[^1]}"
            : testDisplayName;

        IntegrationTestFixture.LogTestPhase("Test", shortName);
    }

    private void PrintTestFooter()
    {
        var duration = DateTime.UtcNow - _testStartTime;

        Fixture.CompleteTest(_testClassName, _currentTestName, duration);

        LogSuccess($"Done ({duration.TotalSeconds:F2}s)");
        IntegrationTestFixture.LogTestMessage("");
    }

    protected void Log(string message) => _logger.Log(message);
    protected void LogSuccess(string message) => _logger.LogSuccess(message);
    protected void LogWarning(string message) => _logger.LogWarning(message);
    protected void LogDetail(string message) => _logger.LogDetail(message);
    protected void LogSection(string title) => _logger.LogSection(title);

    #endregion
}
