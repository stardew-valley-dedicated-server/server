using JunimoServer.Tests.Fixtures;
using JunimoServer.Tests.Helpers;
using Xunit.Abstractions;

namespace JunimoServer.Tests;

/// <summary>
/// Base class for integration tests with password protection DISABLED.
/// Uses the NoPasswordFixture which starts the server without SERVER_PASSWORD.
///
/// Usage:
/// [Collection("Integration-NoPassword")]
/// public class MyTests : NoPasswordTestBase
/// {
///     public MyTests(NoPasswordFixture fixture, ITestOutputHelper output)
///         : base(fixture, output) { }
///
///     [Fact]
///     public async Task MyTest()
///     {
///         // Connect with retry (no authentication needed)
///         var result = await JoinWorldWithRetryAsync("TestFarmer");
///         AssertJoinSuccess(result);
///     }
/// }
/// </summary>
public abstract class NoPasswordTestBase : IntegrationTestBaseCore<NoPasswordFixture>
{
    protected NoPasswordTestBase(
        NoPasswordFixture fixture,
        ITestOutputHelper output,
        ConnectionRetryOptions? connectionOptions = null,
        ExceptionMonitorOptions? exceptionOptions = null)
        : base(fixture, output, connectionOptions, exceptionOptions)
    {
    }
}
