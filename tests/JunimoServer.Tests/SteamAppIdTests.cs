using JunimoServer.Tests.Helpers;
using JunimoServer.Tests.Infrastructure;
using Xunit;

namespace JunimoServer.Tests;

/// <summary>
/// Tests that verify Steam SDK is configured correctly for SDR connections.
///
/// The Steamworks SDK defaults to AppID 480 (Spacewar) if steam_appid.txt is missing
/// or contains the wrong value. This causes SDR connection failures because the client
/// (Stardew Valley, 413150) and server have mismatched AppIDs.
///
/// API-only. Never calls GetClientAsync().
/// </summary>
[TestServer(Isolation = IsolationMode.SharedAssembly, Clients = 0, Artifacts = false)]
public class SteamAppIdTests : TestBase
{
    /// <summary>
    /// Stardew Valley's Steam App ID.
    /// </summary>
    private const string ExpectedAppId = "413150";

    /// <summary>
    /// Spacewar's Steam App ID (SDK default, indicates misconfiguration).
    /// </summary>
    private const string SpacewarAppId = "480";

    public SteamAppIdTests() { }

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
        // Get container logs. Use a timeout since GetLogsAsync can hang on disposed containers.
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        cts.CancelAfter(TimeSpan.FromSeconds(30));
        var logs = await Server.Container.GetLogsAsync(ct: cts.Token);
        var combinedLogs = (logs.Stdout ?? "") + (logs.Stderr ?? "");

        Log("Searching for Steam AppID in server logs...");

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

                // Extract the AppID number
                var parts = line.Split('=');
                if (parts.Length >= 2)
                    detectedAppId = parts[^1].Trim();
                break;
            }
        }

        // Verify we found the log line
        Assert.NotNull(appIdLine);
        Log("Found breakpad AppID log line:");
        LogDetail($"  {appIdLine}");

        // Verify the AppID is correct
        Assert.NotNull(detectedAppId);
        Assert.NotEqual(SpacewarAppId, detectedAppId);
        Assert.Equal(ExpectedAppId, detectedAppId);

        LogSuccess($"Steam AppID: {detectedAppId} (expected {ExpectedAppId})");
    }

    /// <summary>
    /// Verifies that SDR relay initialization was attempted.
    ///
    /// After Steam GameServer initialization, the server logs an SDR relay status ONCE.
    /// The mod does not re-log transitions, so we check whatever status was logged.
    ///
    /// Valid states (prove Steam SDK initialized correctly):
    /// - k_ESteamNetworkingAvailability_Current: Ready for connections
    /// - k_ESteamNetworkingAvailability_Attempting: Connecting to relay network
    /// - k_ESteamNetworkingAvailability_Waiting: SDK initialized, relay pending
    ///   (common in Docker containers without internet to Valve relay servers)
    ///
    /// Failed states (indicate a real problem):
    /// - k_ESteamNetworkingAvailability_Failed
    /// - k_ESteamNetworkingAvailability_Unknown
    /// </summary>
    [Fact]
    public async Task Server_HasSdrRelayInitialized()
    {
        Log("Searching for SDR relay status in server logs...");

        // The mod logs SDR status once during SteamServersConnected callback.
        // Poll until the log line appears (server may still be booting).
        string? sdrStatusLine = null;
        var found = await PollingHelper.WaitUntilAsync(
            WaitName.Polling_SteamAppId_SdrStatusLine,
            async () =>
            {
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
                cts.CancelAfter(TimeSpan.FromSeconds(10));
                var logs = await Server.Container.GetLogsAsync(ct: cts.Token);
                var combinedLogs = (logs.Stdout ?? "") + (logs.Stderr ?? "");

                foreach (var line in combinedLogs.Split('\n'))
                {
                    if (line.Contains("SDR relay status:"))
                    {
                        sdrStatusLine = line.Trim();
                        return true;
                    }
                }
                return false;
            }, TimeSpan.FromSeconds(30), cancellationToken: TestContext.Current.CancellationToken);

        Assert.True(found, "SDR relay status line should appear in server logs");
        Log("Found SDR relay log line:");
        LogDetail($"  {sdrStatusLine}");

        // Verify SDR is not in a failed state
        Assert.DoesNotContain("Failed", sdrStatusLine);
        Assert.DoesNotContain("Unknown", sdrStatusLine);

        // Extract the status value from "SDR relay status: <value>"
        var statusValue = sdrStatusLine!.Split("SDR relay status:") is [_, var tail]
            ? tail.Trim()
            : sdrStatusLine;

        // Accept Waiting, Attempting, or Current; all prove the SDK initialized.
        // In Docker test containers, "Waiting" is expected because there's no
        // internet route to Valve's relay servers.
        var isValid = sdrStatusLine.Contains("Current")
            || sdrStatusLine.Contains("Attempting")
            || sdrStatusLine.Contains("Waiting");
        Assert.True(isValid, $"SDR status should be Current, Attempting, or Waiting, got: {sdrStatusLine}");

        LogSuccess($"SDR relay status: {statusValue}");
    }
}
