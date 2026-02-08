using Xunit;
using Xunit.Abstractions;

namespace JunimoServer.Tests.Fixtures;

/// <summary>
/// Custom test collection orderer that ensures DownloadValidation tests run LAST.
///
/// The DownloadValidation tests log into Steam with a separate container, which
/// invalidates the server's Steam session (LogonSessionReplaced). If these tests
/// run before Integration tests, the server's Steam lobby becomes unavailable.
///
/// Order:
/// 1. Integration (main tests with password protection)
/// 2. Integration-NoPassword (tests without password)
/// 3. DownloadValidation (Steam download tests - run last to avoid session conflicts)
/// </summary>
public class TestCollectionOrderer : ITestCollectionOrderer
{
    // Priority: lower = runs first
    private static int GetPriority(string collectionName) => collectionName switch
    {
        "Integration" => 0,
        "Integration-NoPassword" => 1,
        "DownloadValidation" => 100, // Run last
        _ => 50 // Unknown collections run in the middle
    };

    public IEnumerable<ITestCollection> OrderTestCollections(IEnumerable<ITestCollection> testCollections)
    {
        return testCollections.OrderBy(c => GetPriority(c.DisplayName));
    }
}
