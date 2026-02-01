using JunimoServer.Tests.Fixtures;
using Xunit;
using Xunit.Abstractions;

namespace JunimoServer.Tests;

/// <summary>
/// Tests that verify Steam SDK is configured correctly for SDR connections.
///
/// The Steamworks SDK defaults to AppID 480 (Spacewar) if steam_appid.txt is missing
/// or contains the wrong value. This causes SDR connection failures because the client
/// (Stardew Valley, 413150) and server have mismatched AppIDs.
/// </summary>
[Collection("Integration")]
public class SteamAppIdTests : IDisposable
{
    private readonly IntegrationTestFixture _fixture;
    private readonly ITestOutputHelper _output;

    /// <summary>
    /// Stardew Valley's Steam App ID.
    /// </summary>
    private const string ExpectedAppId = "413150";

    /// <summary>
    /// Spacewar's Steam App ID (SDK default, indicates misconfiguration).
    /// </summary>
    private const string SpacewarAppId = "480";

    public SteamAppIdTests(IntegrationTestFixture fixture, ITestOutputHelper output)
    {
        _fixture = fixture;
        _output = output;
        _fixture.RegisterTest(nameof(SteamAppIdTests));
    }

    /// <summary>
    /// Verifies that the server container has the correct Steam AppID configured.
    ///
    /// The Steam SDK logs "Setting breakpad minidump AppID = X" during initialization.
    /// This test checks that X is 413150 (Stardew Valley) and not 480 (Spacewar).
    ///
    /// If this test fails, Steam SDR connections will not work because clients
    /// connecting as Stardew Valley cannot reach a server running as Spacewar.
    /// </summary>
    [Fact]
    public async Task Server_HasCorrectSteamAppId()
    {
        // Get container logs
        Assert.NotNull(_fixture.ServerContainer);
        var logs = await _fixture.ServerContainer.GetLogsAsync();
        var combinedLogs = (logs.Stdout ?? "") + (logs.Stderr ?? "");

        _output.WriteLine("Searching for Steam AppID in server logs...");

        // Look for the breakpad minidump line which shows the AppID
        // Format: "Setting breakpad minidump AppID = 413150"
        var lines = combinedLogs.Split('\n');
        string? appIdLine = null;
        string? detectedAppId = null;

        foreach (var line in lines)
        {
            if (line.Contains("Setting breakpad minidump AppID"))
            {
                appIdLine = line.Trim();
                _output.WriteLine($"Found: {appIdLine}");

                // Extract the AppID number
                var parts = line.Split('=');
                if (parts.Length >= 2)
                {
                    detectedAppId = parts[^1].Trim();
                }
                break;
            }
        }

        // Verify we found the log line
        Assert.NotNull(appIdLine);

        // Verify the AppID is correct
        Assert.NotNull(detectedAppId);
        Assert.NotEqual(SpacewarAppId, detectedAppId);
        Assert.Equal(ExpectedAppId, detectedAppId);

        _output.WriteLine($"Steam AppID correctly configured: {detectedAppId}");
    }

    /// <summary>
    /// Verifies that SDR relay initialization was attempted.
    ///
    /// After Steam GameServer initialization, the server should report an SDR relay status.
    /// Valid states are:
    /// - k_ESteamNetworkingAvailability_Current: Ready for connections
    /// - k_ESteamNetworkingAvailability_Attempting: Connecting to relay network
    ///
    /// Failed states (which would indicate a problem):
    /// - k_ESteamNetworkingAvailability_Failed
    /// - k_ESteamNetworkingAvailability_Unknown
    /// </summary>
    [Fact]
    public async Task Server_HasSdrRelayInitialized()
    {
        Assert.NotNull(_fixture.ServerContainer);
        var logs = await _fixture.ServerContainer.GetLogsAsync();
        var combinedLogs = (logs.Stdout ?? "") + (logs.Stderr ?? "");

        _output.WriteLine("Searching for SDR relay status in server logs...");

        var lines = combinedLogs.Split('\n');
        string? sdrStatusLine = null;

        foreach (var line in lines)
        {
            if (line.Contains("SDR relay status:"))
            {
                sdrStatusLine = line.Trim();
                _output.WriteLine($"Found: {sdrStatusLine}");
                break;
            }
        }

        // Verify SDR status was logged
        Assert.NotNull(sdrStatusLine);

        // Verify SDR is not in a failed state
        Assert.DoesNotContain("Failed", sdrStatusLine);
        Assert.DoesNotContain("Unknown", sdrStatusLine);

        // Verify SDR is either ready or attempting to connect
        var isValid = sdrStatusLine.Contains("Current") || sdrStatusLine.Contains("Attempting");
        Assert.True(isValid, $"SDR status should be Current or Attempting, got: {sdrStatusLine}");

        _output.WriteLine("SDR relay initialization confirmed");
    }

    public void Dispose()
    {
        // No resources to dispose
    }
}
