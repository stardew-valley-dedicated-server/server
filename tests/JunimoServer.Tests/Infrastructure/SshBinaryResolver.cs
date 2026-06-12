using System.Diagnostics;
using System.Runtime.InteropServices;

namespace JunimoServer.Tests.Infrastructure;

/// <summary>
/// Resolves the path to an SSH binary that supports ControlMaster end-to-end
/// and rejects the Microsoft Windows OpenSSH port. The Microsoft port at
/// <c>C:\Windows\System32\OpenSSH\ssh.exe</c> can't carry the
/// <c>sendmsg()</c> ancillary data its multiplex layer needs (named-pipe
/// transport), so it accepts <c>-o ControlMaster=auto</c> but aborts the
/// child with <c>getsockname failed: Not a socket</c>. Use Git for Windows'
/// Cygwin-built ssh (<c>C:\Program Files\Git\usr\bin\ssh.exe</c>) on Windows;
/// upstream OpenSSH from PATH on Linux/macOS.
/// </summary>
public static class SshBinaryResolver
{
    private const string EnvVar = "SDVD_SSH_PATH";

    /// <summary>
    /// Resolves an SSH binary path and confirms its banner doesn't identify
    /// it as the rejected Microsoft Windows port. Throws if no usable binary
    /// is found or the candidate fails the banner check.
    /// </summary>
    public static async Task<string> ResolveAsync(CancellationToken ct = default)
    {
        var candidates = EnumerateCandidates().ToList();
        var failures = new List<string>();
        foreach (var candidate in candidates)
        {
            var (ok, message) = await ProbeAsync(candidate, ct);
            if (ok)
            {
                return candidate;
            }

            failures.Add($"  - {candidate}: {message}");
        }

        var configHint = Environment.GetEnvironmentVariable(EnvVar) is { Length: > 0 }
            ? $" (overridden by {EnvVar}={Environment.GetEnvironmentVariable(EnvVar)})"
            : $" (set {EnvVar} to override)";
        throw new InvalidOperationException(
            "No usable ssh binary found for ControlMaster reuse"
                + configHint
                + ":"
                + Environment.NewLine
                + string.Join(Environment.NewLine, failures)
        );
    }

    private static IEnumerable<string> EnumerateCandidates()
    {
        var overridePath = Environment.GetEnvironmentVariable(EnvVar);
        if (!string.IsNullOrWhiteSpace(overridePath))
        {
            yield return overridePath;
            yield break;
        }

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            yield return @"C:\Program Files\Git\usr\bin\ssh.exe";
            // Fall through to PATH (e.g. WSL ssh, or a custom install) only
            // after the Git for Windows pin so `where ssh` ordering on a dev
            // machine that has System32 first can't slip the Microsoft port
            // in. The banner check still rejects it if found.
            yield return "ssh";
        }
        else
        {
            yield return "ssh";
        }
    }

    private static async Task<(bool Ok, string Message)> ProbeAsync(
        string candidate,
        CancellationToken ct
    )
    {
        ProcessStartInfo psi;
        try
        {
            psi = new ProcessStartInfo(candidate)
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };
            psi.ArgumentList.Add("-V");
        }
        catch (Exception ex)
        {
            return (false, $"could not prepare process: {ex.Message}");
        }

        Process? process;
        try
        {
            process = Process.Start(psi);
        }
        catch (Exception ex)
        {
            return (false, $"failed to start: {ex.Message}");
        }
        if (process is null)
        {
            return (false, "Process.Start returned null");
        }

        try
        {
            var stderrTask = process.StandardError.ReadToEndAsync();
            var stdoutTask = process.StandardOutput.ReadToEndAsync();

            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
            deadline.CancelAfter(TimeSpan.FromSeconds(5));
            try
            {
                await process.WaitForExitAsync(deadline.Token);
            }
            catch (OperationCanceledException)
            {
                try
                {
                    if (!process.HasExited)
                    {
                        process.Kill(entireProcessTree: true);
                    }
                }
                catch { }
                return (false, "ssh -V did not exit within 5s");
            }

            var stderr = (await stderrTask).Trim();
            var stdout = (await stdoutTask).Trim();

            // Both OpenSSH ports write `-V` to stderr. Empty stderr means an
            // unexpected fork (e.g. a wrapper script) — reject rather than
            // assume it's safe.
            if (stderr.Length == 0)
            {
                return (
                    false,
                    $"ssh -V wrote nothing to stderr (stdout was: {Truncate(stdout, 200)})"
                );
            }

            if (stderr.Contains("OpenSSH_for_Windows", StringComparison.Ordinal))
            {
                return (
                    false,
                    "Microsoft Windows OpenSSH port does not support ControlMaster fd-passing "
                        + $"(banner: {Truncate(stderr, 200)}). Use Git for Windows' ssh instead."
                );
            }

            if (!stderr.Contains("OpenSSH", StringComparison.Ordinal))
            {
                return (false, $"banner does not identify OpenSSH: {Truncate(stderr, 200)}");
            }

            return (true, stderr);
        }
        finally
        {
            try
            {
                process.Dispose();
            }
            catch { }
        }
    }

    private static string Truncate(string s, int max)
    {
        if (string.IsNullOrEmpty(s))
        {
            return "";
        }

        return s.Length <= max ? s : s.Substring(0, max) + "…";
    }
}
