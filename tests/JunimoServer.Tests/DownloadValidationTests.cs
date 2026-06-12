using DotNet.Testcontainers.Containers;
using JunimoServer.Tests.Fixtures;
using JunimoServer.Tests.Helpers;
using JunimoServer.Tests.Schema.Events;
using Xunit;

namespace JunimoServer.Tests;

/// <summary>
/// Tests for Steam download validation and repair functionality.
/// These tests verify that corrupted or missing game files are detected and repaired.
///
/// Prerequisites: Run 'make setup' first to download the game and save Steam session.
/// These tests use shared volumes to avoid session conflicts.
///
///
/// Uses its own DownloadValidationFixture so the standalone steam-auth container
/// here doesn't collide with the Steam lobby used by other integration tests.
/// </summary>
/// <remarks>
/// Does NOT inherit TestBase because these tests manage a standalone steam-auth container
/// via DownloadValidationFixture, with no server lease or game client. The logging helpers
/// (Log, LogSuccess, etc.) emit annotations directly through SetupEventBus, matching
/// TestBase's interface.
/// </remarks>
[Collection("DownloadValidation")]
public class DownloadValidationTests : IAsyncLifetime
{
    private readonly DownloadValidationFixture _fixture;

    private readonly string _testClassName;
    private string? _currentTestName;
    private DateTime _testStartTime;

    // Test file to corrupt/delete - a small, non-critical file that's easy to verify
    private const string TestFilePath = "/data/game/Content/Maps/mermaid_house_tiles.xnb";
    private const string BackupFilePath = "/data/game/Content/Maps/mermaid_house_tiles.xnb.backup";

    public DownloadValidationTests(DownloadValidationFixture fixture)
    {
        _fixture = fixture;
        _testClassName = GetType().Name;
    }

    public async ValueTask InitializeAsync()
    {
        _testStartTime = DateTime.UtcNow;

        // Try to extract test name from the xUnit output helper
        _currentTestName = ExtractTestName();

        // Track test count for summary
        _fixture.MarkDispatched(_testClassName, _currentTestName);

        // Check if test run was aborted by a previous test
        if (_fixture.IsTestRunAborted)
        {
            Log($"SKIPPED: Test run was aborted");
            Log($"Reason: {_fixture.AbortReason}");
            PrintTestFooter();
            _fixture.ThrowIfAborted();
        }

        // Ensure container is available
        if (_fixture.SteamAuthContainer == null)
        {
            throw new InvalidOperationException("Steam-auth container not initialized");
        }

        // Ensure game is downloaded before running tests
        await EnsureGameDownloaded();
    }

    public async ValueTask DisposeAsync()
    {
        // Restore backup if it exists (safety net for shared volumes)
        if (_fixture.SteamAuthContainer != null)
        {
            try
            {
                await ExecInContainer(
                    _fixture.SteamAuthContainer,
                    $"sh -c \"[ -f {BackupFilePath} ] && mv {BackupFilePath} {TestFilePath} || true\""
                );
            }
            catch
            { /* ignore */
            }
        }

        // Print test footer
        PrintTestFooter();
    }

    private async Task EnsureGameDownloaded()
    {
        Assert.NotNull(_fixture.SteamAuthContainer);

        // With shared volumes, game should already be downloaded from 'make setup'
        var exists = await FileExists(_fixture.SteamAuthContainer, TestFilePath);
        if (exists)
        {
            LogDetail("Game downloaded, test file exists");
            return;
        }

        // Game not downloaded - user needs to run setup first
        throw new Exception(
            $"Test file not found: {TestFilePath}\n"
                + $"Run 'make setup' first to download the game and save Steam session."
        );
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
    [Fact(Skip = "Requires 'make setup' with valid Steam credentials")]
    public async Task CorruptedFile_IsDetectedAndRepaired()
    {
        var container = _fixture.SteamAuthContainer!;

        LogDetail($"Test file: {TestFilePath}");

        // Step 1: Verify the file exists and is valid
        var initialHeader = await GetFileHeader(container, TestFilePath);
        Assert.Equal("584e42", initialHeader); // "XNB" in hex
        LogDetail("Initial file is valid (XNB header present)");

        // Step 2: Backup the file
        await ExecInContainer(container, $"cp {TestFilePath} {BackupFilePath}");
        LogDetail("Backup created");

        // Step 3: Corrupt the file (overwrite first bytes with zeros, keeping size)
        var fileSize = await GetFileSize(container, TestFilePath);
        await ExecInContainer(
            container,
            $"dd if=/dev/zero of={TestFilePath} bs=1 count={fileSize} 2>/dev/null"
        );
        LogDetail($"File corrupted ({fileSize} bytes overwritten with zeros)");

        // Verify corruption
        var corruptedHeader = await GetFileHeader(container, TestFilePath);
        Assert.Equal("000000", corruptedHeader);
        LogDetail("Verified: file is corrupted (header is zeros)");

        // Step 4: Trigger download/validation
        LogSection("Triggering download validation");
        var (exitCode, stdout, stderr) = await TriggerDownloadWithLogs(container);

        // Step 5: Check logs for repair message
        var combinedOutput = stdout + stderr;
        Assert.Contains("chunks need repair", combinedOutput, StringComparison.OrdinalIgnoreCase);
        LogDetail("Found 'chunks need repair' in logs - corruption was detected");

        // Step 6: Verify the file is now valid
        var repairedHeader = await GetFileHeader(container, TestFilePath);
        Assert.Equal("584e42", repairedHeader);
        LogSuccess("File repaired successfully (XNB header restored)");

        // Step 7: Clean up backup
        await ExecInContainer(container, $"rm -f {BackupFilePath}");
        LogDetail("Backup removed");
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
    [Fact(Skip = "Requires 'make setup' with valid Steam credentials")]
    public async Task DeletedFile_IsDetectedAndRedownloaded()
    {
        var container = _fixture.SteamAuthContainer!;

        LogDetail($"Test file: {TestFilePath}");

        // Step 1: Verify the file exists
        var exists = await FileExists(container, TestFilePath);
        Assert.True(exists, $"Test file should exist: {TestFilePath}");
        LogDetail("Initial file exists");

        // Step 2: Backup the file
        await ExecInContainer(container, $"cp {TestFilePath} {BackupFilePath}");
        LogDetail("Backup created");

        // Step 3: Delete the file
        await ExecInContainer(container, $"rm -f {TestFilePath}");
        LogDetail("File deleted");

        // Verify deletion
        exists = await FileExists(container, TestFilePath);
        Assert.False(exists, "File should be deleted");
        LogDetail("Verified: file is deleted");

        // Step 4: Trigger download/validation
        LogSection("Triggering download validation");
        var (exitCode, stdout, stderr) = await TriggerDownloadWithLogs(container);

        // Log download output for debugging
        if (!string.IsNullOrWhiteSpace(stdout))
            LogDetail($"Download stdout: {stdout}");
        if (!string.IsNullOrWhiteSpace(stderr))
            LogDetail($"Download stderr: {stderr}");

        // Step 5: Verify the file is re-downloaded and valid
        exists = await FileExists(container, TestFilePath);
        Assert.True(exists, "File should be re-downloaded");
        LogDetail("File re-downloaded");

        var header = await GetFileHeader(container, TestFilePath);
        Assert.Equal("584e42", header);
        LogSuccess("File is valid (XNB header present)");

        // Step 6: Clean up backup
        await ExecInContainer(container, $"rm -f {BackupFilePath}");
        LogDetail("Backup removed");
    }

    #region Output Formatting

    private string? ExtractTestName()
    {
        // In xUnit v3, we can use TestContext to get the test name
        try
        {
            return TestContext.Current?.Test?.TestDisplayName;
        }
        catch { }
        return null;
    }

    private void PrintTestFooter()
    {
        var duration = DateTime.UtcNow - _testStartTime;

        // Record duration in unified summary
        _fixture.MarkCompleted(_testClassName, _currentTestName, duration);

        LogSuccess(FormattableString.Invariant($"Done ({duration.TotalSeconds:F2}s)"));
    }

    private string DisplayName => _currentTestName ?? $"{_testClassName}.???";

    private void Log(string message) =>
        SetupEventBus.EmitTestAnnotation(
            DisplayName,
            AnnotationLevel.Info,
            AnnotationSource.Body,
            message
        );

    private void LogSuccess(string message) =>
        SetupEventBus.EmitTestAnnotation(
            DisplayName,
            AnnotationLevel.Success,
            AnnotationSource.Body,
            message
        );

    private void LogWarning(string message) =>
        SetupEventBus.EmitTestAnnotation(
            DisplayName,
            AnnotationLevel.Warning,
            AnnotationSource.Body,
            message
        );

    private void LogDetail(string message) =>
        SetupEventBus.EmitTestAnnotation(
            DisplayName,
            AnnotationLevel.Detail,
            AnnotationSource.Body,
            message
        );

    private void LogSection(string title) =>
        SetupEventBus.EmitTestAnnotation(
            DisplayName,
            AnnotationLevel.Section,
            AnnotationSource.Body,
            title
        );

    #endregion

    #region Helper Methods

    private static async Task<string> ExecInContainer(IContainer container, string command)
    {
        var result = await container.ExecAsync(new[] { "sh", "-c", command });
        if (result.ExitCode != DockerExitCodes.Success)
        {
            throw new Exception(
                $"Command failed with exit code {result.ExitCode}: {command}\nStderr: {result.Stderr}"
            );
        }
        return result.Stdout.Trim();
    }

    private static async Task<string> GetFileHeader(IContainer container, string filePath)
    {
        // Get first 3 bytes as hex
        var result = await container.ExecAsync(
            new[] { "sh", "-c", $"head -c3 {filePath} | od -A n -t x1 | tr -d ' \\n'" }
        );
        return result.Stdout.Trim();
    }

    private static async Task<long> GetFileSize(IContainer container, string filePath)
    {
        var result = await ExecInContainer(container, $"stat -c%s {filePath}");
        return long.Parse(result);
    }

    private static async Task<bool> FileExists(IContainer container, string filePath)
    {
        var result = await container.ExecAsync(
            new[] { "sh", "-c", $"[ -f {filePath} ] && echo 1 || echo 0" }
        );
        return result.Stdout.Trim() == "1";
    }

    private async Task<(int exitCode, string stdout, string stderr)> TriggerDownloadWithLogs(
        IContainer container
    )
    {
        // Run the download command using the saved session
        var result = await container.ExecAsync(
            new[] { "sh", "-c", "dotnet SteamService.dll download" }
        );

        LogDetail($"Download exit code: {result.ExitCode}");

        // Also get container logs for additional context
        var logs = await container.GetLogsAsync();

        // Combine exec output with container logs
        var combinedStdout = result.Stdout + "\n" + logs.Stdout;
        var combinedStderr = result.Stderr + "\n" + logs.Stderr;

        // A null ExitCode means the daemon reported no exit status; treat that as a
        // non-zero failure so callers don't read it as success.
        return ((int)(result.ExitCode ?? DockerExitCodes.Unknown), combinedStdout, combinedStderr);
    }

    #endregion
}
