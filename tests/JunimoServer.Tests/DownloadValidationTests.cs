using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using DotNet.Testcontainers.Images;
using DotNet.Testcontainers.Networks;
using JunimoServer.Tests.Fixtures;
using Microsoft.Extensions.Logging.Abstractions;
using Spectre.Console;
using Xunit;
using Xunit.Abstractions;

namespace JunimoServer.Tests;

/// <summary>
/// Tests for Steam download validation and repair functionality.
/// These tests verify that corrupted or missing game files are detected and repaired.
///
/// Prerequisites: Run 'make setup' first to download the game and save Steam session.
/// These tests use shared volumes to avoid session conflicts.
///
/// Uses IntegrationTestFixture to ensure images are built before tests run.
///
/// SKIPPED: Reusing the auth session for our only test account clears auth on other tests,
/// leading to cascading failures. These tests need a separate Steam account or isolated test run.
/// </summary>
[Collection("Integration")]
[Trait("Category", "Skip")]
public class DownloadValidationTests : IAsyncLifetime
{
    private readonly ITestOutputHelper _output;
    private readonly IntegrationTestFixture _fixture;

    private IContainer? _steamAuthContainer;
    private INetwork? _network;

    // Use shared volumes (game already downloaded) to avoid session conflicts
    private static readonly string VolumePrefix = Environment.GetEnvironmentVariable("SDVD_VOLUME_PREFIX") ?? "server";
    private string GameDataVolume => $"{VolumePrefix}_game-data";
    private string SteamSessionVolume => $"{VolumePrefix}_steam-session";

    // Test file to corrupt/delete - a small, non-critical file that's easy to verify
    private const string TestFilePath = "/data/game/Content/Maps/mermaid_house_tiles.xnb";
    private const string BackupFilePath = "/data/game/Content/Maps/mermaid_house_tiles.xnb.backup";

    // Image configuration
    private static readonly string ImageTag = Environment.GetEnvironmentVariable("SDVD_IMAGE_TAG") ?? "local";
    private static readonly bool UseLocalImages = ImageTag == "local";

    // Ports
    private const int SteamAuthPort = 3001;

    public DownloadValidationTests(IntegrationTestFixture fixture, ITestOutputHelper output)
    {
        _fixture = fixture;
        _output = output;
    }

    public async Task InitializeAsync()
    {
        // IntegrationTestFixture already built images, just set up our test-specific container
        Log("Setting up download validation test environment...");
        Log($"Using shared volumes: {GameDataVolume}, {SteamSessionVolume}");

        // Create isolated network for this test
        var networkName = $"sdvd-dltest-{Guid.NewGuid():N}";
        _network = new NetworkBuilder()
            .WithName(networkName)
            .Build();

        await _network.CreateAsync();

        // Build steam-auth container using shared volumes
        // Prerequisites: 'make setup' must have been run to download game and save session
        _steamAuthContainer = new ContainerBuilder()
            .WithLogger(NullLogger.Instance)
            .WithImage($"sdvd/steam-service:{ImageTag}")
            .WithImagePullPolicy(UseLocalImages ? PullPolicy.Never : PullPolicy.Missing)
            .WithName($"sdvd-steam-auth-dltest-{Guid.NewGuid():N}")
            .WithNetwork(_network)
            .WithNetworkAliases("steam-auth")
            .WithPortBinding(SteamAuthPort, true)
            // Use shared volumes - game already downloaded, session already saved
            // This avoids session conflicts between HTTP server and download exec
            .WithVolumeMount(SteamSessionVolume, "/data/steam-session")
            .WithVolumeMount(GameDataVolume, "/data/game")
            .WithEnvironment("PORT", SteamAuthPort.ToString())
            .WithEnvironment("GAME_DIR", "/data/game")
            .WithEnvironment("SESSION_DIR", "/data/steam-session")
            .WithWaitStrategy(Wait.ForUnixContainer()
                .UntilHttpRequestIsSucceeded(r => r
                    .ForPort(SteamAuthPort)
                    .ForPath("/health")
                    .ForStatusCode(System.Net.HttpStatusCode.OK)))
            .Build();

        Log("Starting steam-auth container...");
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        await _steamAuthContainer.StartAsync(cts.Token);
        Log("Steam-auth container started");

        // Ensure game is downloaded before running tests
        // This also validates the test file exists
        await EnsureGameDownloaded();
    }

    private async Task EnsureGameDownloaded()
    {
        Assert.NotNull(_steamAuthContainer);

        // With shared volumes, game should already be downloaded from 'make setup'
        var exists = await FileExists(_steamAuthContainer, TestFilePath);
        if (exists)
        {
            Log("Game downloaded, test file exists");
            return;
        }

        // Game not downloaded - user needs to run setup first
        throw new Exception(
            $"Test file not found: {TestFilePath}\n" +
            $"Run 'make setup' first to download the game and save Steam session.");
    }

    public async Task DisposeAsync()
    {
        Log("Cleaning up test environment...");

        // Restore backup if it exists (safety net for shared volumes)
        if (_steamAuthContainer != null)
        {
            try
            {
                await ExecInContainer(_steamAuthContainer,
                    $"sh -c \"[ -f {BackupFilePath} ] && mv {BackupFilePath} {TestFilePath} || true\"");
            }
            catch { /* ignore */ }
        }

        // Dispose containers
        if (_steamAuthContainer != null)
        {
            try { await _steamAuthContainer.DisposeAsync(); }
            catch { /* ignore */ }
        }

        // Dispose network
        if (_network != null)
        {
            try { await _network.DisposeAsync(); }
            catch { /* ignore */ }
        }

        // Note: We don't clean up volumes - they're shared with other tests
        Log("Cleanup complete");
    }

    /// <summary>
    /// Verifies that a corrupted XNB file is detected and repaired during download validation.
    ///
    /// Test flow:
    /// 1. Backup the test file
    /// 2. Corrupt the file (overwrite with zeros)
    /// 3. Trigger download/validation via steam-auth API
    /// 4. Verify the file is repaired (valid XNB header)
    /// 5. Restore backup
    /// </summary>
    [Fact(Skip = "Reusing the auth session for our only test account clears auth on other tests, leading to cascading failures")]
    public async Task CorruptedFile_IsDetectedAndRepaired()
    {
        Assert.NotNull(_steamAuthContainer);

        Log($"Test file: {TestFilePath}");

        // Step 1: Verify the file exists and is valid
        var initialHeader = await GetFileHeader(_steamAuthContainer, TestFilePath);
        Assert.Equal("584e42", initialHeader); // "XNB" in hex
        Log("Initial file is valid (XNB header present)");

        // Step 2: Backup the file
        await ExecInContainer(_steamAuthContainer, $"cp {TestFilePath} {BackupFilePath}");
        Log("Backup created");

        // Step 3: Corrupt the file (overwrite first bytes with zeros, keeping size)
        var fileSize = await GetFileSize(_steamAuthContainer, TestFilePath);
        await ExecInContainer(_steamAuthContainer,
            $"dd if=/dev/zero of={TestFilePath} bs=1 count={fileSize} 2>/dev/null");
        Log($"File corrupted ({fileSize} bytes overwritten with zeros)");

        // Verify corruption
        var corruptedHeader = await GetFileHeader(_steamAuthContainer, TestFilePath);
        Assert.Equal("000000", corruptedHeader);
        Log("Verified: file is corrupted (header is zeros)");

        // Step 4: Trigger download/validation
        Log("Triggering download validation...");
        var (exitCode, stdout, stderr) = await TriggerDownloadWithLogs(_steamAuthContainer);

        // Step 5: Check logs for repair message
        var combinedOutput = stdout + stderr;
        Assert.Contains("chunks need repair", combinedOutput, StringComparison.OrdinalIgnoreCase);
        Log("Found 'chunks need repair' in logs - corruption was detected");

        // Step 6: Verify the file is now valid
        var repairedHeader = await GetFileHeader(_steamAuthContainer, TestFilePath);
        Assert.Equal("584e42", repairedHeader);
        Log("File repaired successfully (XNB header restored)");

        // Step 7: Clean up backup
        await ExecInContainer(_steamAuthContainer, $"rm -f {BackupFilePath}");
        Log("Backup removed");
    }

    /// <summary>
    /// Verifies that a deleted (non-filtered) file is detected and re-downloaded.
    ///
    /// Test flow:
    /// 1. Backup the test file
    /// 2. Delete the file
    /// 3. Trigger download/validation via steam-auth API
    /// 4. Verify the file is re-downloaded (exists and valid)
    /// 5. Restore backup (cleanup)
    /// </summary>
    [Fact(Skip = "Reusing the auth session for our only test account clears auth on other tests, leading to cascading failures")]
    public async Task DeletedFile_IsDetectedAndRedownloaded()
    {
        Assert.NotNull(_steamAuthContainer);

        Log($"Test file: {TestFilePath}");

        // Step 1: Verify the file exists
        var exists = await FileExists(_steamAuthContainer, TestFilePath);
        Assert.True(exists, $"Test file should exist: {TestFilePath}");
        Log("Initial file exists");

        // Step 2: Backup the file
        await ExecInContainer(_steamAuthContainer, $"cp {TestFilePath} {BackupFilePath}");
        Log("Backup created");

        // Step 3: Delete the file
        await ExecInContainer(_steamAuthContainer, $"rm -f {TestFilePath}");
        Log("File deleted");

        // Verify deletion
        exists = await FileExists(_steamAuthContainer, TestFilePath);
        Assert.False(exists, "File should be deleted");
        Log("Verified: file is deleted");

        // Step 4: Trigger download/validation
        Log("Triggering download validation...");
        var (exitCode, stdout, stderr) = await TriggerDownloadWithLogs(_steamAuthContainer);

        // Log download output for debugging
        if (!string.IsNullOrWhiteSpace(stdout))
            Log($"Download stdout: {stdout}");
        if (!string.IsNullOrWhiteSpace(stderr))
            Log($"Download stderr: {stderr}");

        // Step 5: Verify the file is re-downloaded and valid
        exists = await FileExists(_steamAuthContainer, TestFilePath);
        Assert.True(exists, "File should be re-downloaded");
        Log("File re-downloaded");

        var header = await GetFileHeader(_steamAuthContainer, TestFilePath);
        Assert.Equal("584e42", header);
        Log("File is valid (XNB header present)");

        // Step 6: Clean up backup
        await ExecInContainer(_steamAuthContainer, $"rm -f {BackupFilePath}");
        Log("Backup removed");
    }

    #region Helper Methods

    private async Task<string> ExecInContainer(IContainer container, string command)
    {
        var result = await container.ExecAsync(new[] { "sh", "-c", command });
        if (result.ExitCode != 0)
        {
            throw new Exception($"Command failed with exit code {result.ExitCode}: {command}\nStderr: {result.Stderr}");
        }
        return result.Stdout.Trim();
    }

    private async Task<string> GetFileHeader(IContainer container, string filePath)
    {
        // Get first 3 bytes as hex
        var result = await container.ExecAsync(new[] { "sh", "-c",
            $"head -c3 {filePath} | od -A n -t x1 | tr -d ' \\n'" });
        return result.Stdout.Trim();
    }

    private async Task<long> GetFileSize(IContainer container, string filePath)
    {
        var result = await ExecInContainer(container, $"stat -c%s {filePath}");
        return long.Parse(result);
    }

    private async Task<bool> FileExists(IContainer container, string filePath)
    {
        var result = await container.ExecAsync(new[] { "sh", "-c", $"[ -f {filePath} ] && echo 1 || echo 0" });
        return result.Stdout.Trim() == "1";
    }

    private async Task<(int exitCode, string stdout, string stderr)> TriggerDownloadWithLogs(IContainer container)
    {
        // Run the download command using the saved session
        // The container auto-logs in on startup and saves session to /data/steam-session
        // The download command will use this saved session (no credentials needed in exec)
        var result = await container.ExecAsync(new[] { "sh", "-c",
            "dotnet SteamService.dll download"
        });

        Log($"Download exit code: {result.ExitCode}");

        // Also get container logs for additional context
        var logs = await container.GetLogsAsync();

        // Combine exec output with container logs
        var combinedStdout = result.Stdout + "\n" + logs.Stdout;
        var combinedStderr = result.Stderr + "\n" + logs.Stderr;

        return ((int)result.ExitCode, combinedStdout, combinedStderr);
    }

    private void Log(string message)
    {
        var formatted = $"[DLTest] {message}";
        _output.WriteLine(formatted);
        AnsiConsole.MarkupLine($"[grey]{Markup.Escape(formatted)}[/]");
    }

    #endregion
}
