using JunimoServer.Tests.Fixtures;
using JunimoServer.Tests.Helpers;
using Xunit.Abstractions;

namespace JunimoServer.Tests;

/// <summary>
/// Base class for integration tests with password protection ENABLED.
/// Provides all standard integration test features plus password-specific methods.
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
///         // Connect with retry (auto-authenticates if password is set)
///         var result = await JoinWorldWithRetryAsync("TestFarmer");
///         AssertJoinSuccess(result);
///
///         // Or join without auth to test lobby behavior
///         var result = await JoinWorldWithoutAuthAsync("TestFarmer");
///         AssertJoinSuccess(result);
///     }
/// }
/// </summary>
public abstract class IntegrationTestBase : IntegrationTestBaseCore<IntegrationTestFixture>
{
    protected IntegrationTestBase(
        IntegrationTestFixture fixture,
        ITestOutputHelper output,
        ConnectionRetryOptions? connectionOptions = null,
        ExceptionMonitorOptions? exceptionOptions = null)
        : base(fixture, output, connectionOptions, exceptionOptions)
    {
    }

    /// <summary>
    /// Connects to server and joins the game world WITHOUT automatic authentication.
    /// Use this for tests that need to test unauthenticated/lobby behavior.
    /// Throws ExceptionMonitorException if a server error is detected.
    /// </summary>
    protected async Task<JoinWorldResult> JoinWorldWithoutAuthAsync(
        string farmerName,
        string favoriteThing = "Testing",
        bool preferExistingFarmer = true,
        CancellationToken ct = default)
    {
        try
        {
            var result = await Connection.JoinWorldAsync(InviteCode, farmerName, favoriteThing, preferExistingFarmer, skipAutoLogin: true, ct);
            if (result.Success) MarkConnected();
            return result;
        }
        catch (OperationCanceledException ex)
        {
            ThrowIfServerError(ex, "joining world");
            throw; // Not a server error
        }
    }
}
