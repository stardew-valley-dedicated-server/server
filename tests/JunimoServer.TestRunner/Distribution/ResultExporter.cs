using System.Diagnostics;

namespace JunimoServer.TestRunner.Distribution;

/// <summary>
/// Exports the aggregated run directory to a persistent location. The export
/// target is selected by the <c>SDVD_ARTIFACT_EXPORT</c> env var:
///   - unset / empty: <see cref="LocalExporter"/> (no-op; results stay local)
///   - <c>gh://</c>: <see cref="GitHubActionsExporter"/> (gh CLI artifact upload)
///
/// Future targets (s3://, scp://) implement <see cref="IResultExporter"/> and
/// register here.
/// </summary>
public interface IResultExporter
{
    Task ExportAsync(string runDir, CancellationToken ct = default);
}

public static class ResultExporters
{
    private const string ArtifactExportEnv = "SDVD_ARTIFACT_EXPORT";

    public static IResultExporter SelectFromEnv()
    {
        var target = Environment.GetEnvironmentVariable(ArtifactExportEnv);
        if (string.IsNullOrEmpty(target)) return new LocalExporter();
        if (target.StartsWith("gh://", StringComparison.OrdinalIgnoreCase)) return new GitHubActionsExporter();
        throw new NotSupportedException(
            $"{ArtifactExportEnv}='{target}' is not a supported export target. " +
            "Supported: '' (default, local), 'gh://' (GitHub Actions artifact).");
    }
}

public sealed class LocalExporter : IResultExporter
{
    public Task ExportAsync(string runDir, CancellationToken ct = default) => Task.CompletedTask;
}

/// <summary>
/// Uploads the run directory as a single GitHub Actions artifact via the
/// <c>gh</c> CLI. Requires <c>gh</c> to be available on PATH and GITHUB_TOKEN
/// (or equivalent auth) to be set in the workflow environment.
/// </summary>
public sealed class GitHubActionsExporter : IResultExporter
{
    public async Task ExportAsync(string runDir, CancellationToken ct = default)
    {
        if (!Directory.Exists(runDir))
            throw new InvalidOperationException($"Run directory does not exist: {runDir}");

        var artifactName = $"e2e-results-{Path.GetFileName(runDir)}";
        var psi = new ProcessStartInfo("gh")
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        // gh run upload-artifact <name> <path>
        psi.ArgumentList.Add("run");
        psi.ArgumentList.Add("upload-artifact");
        psi.ArgumentList.Add(artifactName);
        psi.ArgumentList.Add(runDir);

        using var proc = Process.Start(psi)
                         ?? throw new InvalidOperationException("Failed to start gh CLI.");
        await proc.WaitForExitAsync(ct);
        if (proc.ExitCode != 0)
        {
            var err = await proc.StandardError.ReadToEndAsync(CancellationToken.None);
            throw new InvalidOperationException($"gh artifact upload failed (exit {proc.ExitCode}): {err}");
        }
    }
}
