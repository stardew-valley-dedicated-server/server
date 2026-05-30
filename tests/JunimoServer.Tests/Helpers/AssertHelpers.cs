using JunimoServer.Tests.Clients;
using Xunit;

namespace JunimoServer.Tests.Helpers;

/// <summary>
/// Helper methods for common assertions with better error messages.
/// </summary>
public static class AssertHelpers
{
    /// <summary>
    /// Asserts that game state values from /status are within valid ranges.
    /// </summary>
    public static void AssertValidGameState(ServerStatus status)
    {
        Assert.NotNull(status);
        Assert.InRange(status.Day, 1, 28);
        Assert.True(status.Year >= 1, $"Year should be >= 1, got {status.Year}");
        Assert.Contains(status.Season, new[] { "spring", "summer", "fall", "winter" });
        Assert.InRange(status.TimeOfDay, 600, 2600);
    }
}
