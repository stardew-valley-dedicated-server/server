using Xunit;

namespace JunimoServer.Tests.Fixtures;

/// <summary>
/// Integration test fixture with password protection DISABLED.
/// Use this for tests that verify behavior when no password is configured.
/// </summary>
public class NoPasswordFixture : IntegrationTestFixture
{
    /// <summary>
    /// Collection name for the unified test summary.
    /// </summary>
    protected override string CollectionName => "Integration-NoPassword";

    /// <summary>
    /// Returns null to disable password protection on the server.
    /// </summary>
    protected override string? GetServerPassword() => null;
}

/// <summary>
/// Collection definition for no-password integration tests.
/// Tests using this collection will run with password protection disabled.
/// </summary>
[CollectionDefinition("Integration-NoPassword")]
public class NoPasswordTestCollection : ICollectionFixture<NoPasswordFixture>
{
}
