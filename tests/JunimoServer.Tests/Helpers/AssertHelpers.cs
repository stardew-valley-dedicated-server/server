using JunimoServer.Tests.Clients;
using Xunit;

namespace JunimoServer.Tests.Helpers;

/// <summary>
/// Helper methods for common assertions with better error messages.
/// </summary>
public static class AssertHelpers
{
    /// <summary>
    /// Asserts that a WaitResult indicates success.
    /// </summary>
    /// <param name="result">The wait result to check.</param>
    /// <param name="context">Description of what was being waited for.</param>
    public static void AssertWaitSuccess(WaitResult? result, string context)
    {
        Assert.NotNull(result);
        Assert.True(result.Success, $"Wait failed for {context}: {result.Error}");
    }

    /// <summary>
    /// Asserts that a FarmhandOperationResponse indicates success.
    /// </summary>
    /// <param name="result">The response to check.</param>
    /// <param name="context">Description of the operation.</param>
    public static void AssertFarmhandOperationSuccess(FarmhandOperationResponse? result, string context)
    {
        Assert.NotNull(result);
        Assert.True(result.Success, $"{context} failed: {result.Error}");
    }

    /// <summary>
    /// Asserts that a ConnectionResult indicates success.
    /// </summary>
    /// <param name="result">The connection result to check.</param>
    /// <param name="context">Optional additional context.</param>
    public static void AssertConnectionSuccess(ConnectionResult result, string? context = null)
    {
        var message = context != null
            ? $"Connection failed after {result.AttemptsUsed} attempt(s) ({context}): {result.Error}"
            : $"Connection failed after {result.AttemptsUsed} attempt(s): {result.Error}";
        Assert.True(result.Success, message);
    }

    /// <summary>
    /// Asserts that a JoinWorldResult indicates success.
    /// </summary>
    /// <param name="result">The join world result to check.</param>
    /// <param name="context">Optional additional context.</param>
    public static void AssertJoinWorldSuccess(JoinWorldResult result, string? context = null)
    {
        var message = context != null
            ? $"Join world failed after {result.AttemptsUsed} attempt(s) ({context}): {result.Error}"
            : $"Join world failed after {result.AttemptsUsed} attempt(s): {result.Error}";
        Assert.True(result.Success, message);
    }

    /// <summary>
    /// Asserts that a server status response is valid and online.
    /// </summary>
    /// <param name="status">The server status to check.</param>
    /// <param name="requireInviteCode">Whether to also require a valid invite code.</param>
    public static void AssertServerOnline(ServerStatus? status, bool requireInviteCode = true)
    {
        Assert.NotNull(status);
        Assert.True(status.IsOnline, "Server should be online");
        if (requireInviteCode)
        {
            Assert.False(string.IsNullOrEmpty(status.InviteCode), "Server should have an invite code");
        }
    }

    /// <summary>
    /// Asserts that a delete farmhand response indicates success.
    /// </summary>
    /// <param name="result">The delete result to check.</param>
    /// <param name="farmerName">Name of the farmer being deleted.</param>
    public static void AssertDeleteSuccess(FarmhandOperationResponse? result, string farmerName)
    {
        Assert.NotNull(result);
        Assert.True(result.Success, $"Delete farmhand '{farmerName}' should succeed: {result.Error}");
    }

    /// <summary>
    /// Asserts that a farmhands response is valid and contains slots.
    /// </summary>
    /// <param name="farmhands">The farmhands response to check.</param>
    public static void AssertFarmhandsValid(FarmhandsResponse? farmhands)
    {
        Assert.NotNull(farmhands);
        Assert.True(farmhands.Success, $"Get farmhands failed: {farmhands.Error}");
        Assert.NotEmpty(farmhands.Slots);
    }

    /// <summary>
    /// Asserts that game state values from /status are within valid ranges.
    /// </summary>
    /// <param name="status">The server status to check.</param>
    public static void AssertValidGameState(ServerStatus status)
    {
        Assert.NotNull(status);
        Assert.InRange(status.Day, 1, 28);
        Assert.True(status.Year >= 1, $"Year should be >= 1, got {status.Year}");
        Assert.Contains(status.Season, new[] { "spring", "summer", "fall", "winter" });
        Assert.InRange(status.TimeOfDay, 600, 2600);
    }
}
