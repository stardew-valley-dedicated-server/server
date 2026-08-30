using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using JunimoServer.Tests.Schema.Events;

namespace JunimoServer.Tests.Infrastructure;

/// <summary>
/// Coordinator-side view of the SSH host while the master looks wedged: the
/// kernel's state for every TCP connection to the host (retransmits, queued
/// bytes) and a fresh TCP connect to the SSH port. Together they separate "the
/// path to the host is stalled" from "the ssh mux is stalled on a live path".
/// Sampling is best-effort; a failure to sample is reported in the record's
/// <c>Error</c>, never dropped.
/// </summary>
internal static class CoordinatorTcpSnapshot
{
    private const int MaxOutputChars = 1500;
    private const int DefaultSshPort = 22;
    private static readonly TimeSpan ToolTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(3);

    /// <summary>Splits <c>user@host</c> / <c>host</c> into the host part.</summary>
    public static string HostOf(string sshDestination)
    {
        var at = sshDestination.LastIndexOf('@');
        return at >= 0 ? sshDestination[(at + 1)..] : sshDestination;
    }

    public static async Task<(TcpSnapshot Tcp, ReachabilityProbe Reachability)> CaptureAsync(
        string sshDestination,
        string sshPath,
        CancellationToken ct
    )
    {
        var host = HostOf(sshDestination);
        var tcpTask = SampleTcpAsync(host, ct);
        // Resolve the effective port while the TCP sample runs; the probe needs it.
        var port = await ResolvePortAsync(sshPath, sshDestination, ct);
        var probe = await ProbeAsync(host, port, ct);
        return (await tcpTask, probe);
    }

    /// <summary>
    /// The effective SSH port for a destination, honoring <c>~/.ssh/config</c>: the master
    /// spawn passes no <c>-p</c>, so a non-default <c>Port</c> there is what the connection
    /// uses, and probing 22 unconditionally would mislabel a mux stall as unreachable.
    /// <c>ssh -G</c> prints the resolved config without connecting; falls back to 22 on any
    /// failure since the probe is best-effort diagnostics. Uses the same <paramref name="sshPath"/>
    /// as the master spawn so a non-PATH ssh resolves the same config.
    /// </summary>
    private static async Task<int> ResolvePortAsync(
        string sshPath,
        string sshDestination,
        CancellationToken ct
    )
    {
        var psi = new ProcessStartInfo(sshPath)
        {
            ArgumentList = { "-G", sshDestination },
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        try
        {
            using var process = Process.Start(psi);
            if (process is null)
            {
                return DefaultSshPort;
            }

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(ToolTimeout);
            var stdoutTask = process.StandardOutput.ReadToEndAsync(timeoutCts.Token);
            _ = process.StandardError.ReadToEndAsync(timeoutCts.Token);
            try
            {
                await process.WaitForExitAsync(timeoutCts.Token);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                process.Kill(entireProcessTree: true);
                return DefaultSshPort;
            }

            // `ssh -G` emits one lowercased `key value` per line; the port line is `port <n>`.
            foreach (var line in (await stdoutTask).Split('\n'))
            {
                var trimmed = line.Trim();
                if (
                    trimmed.StartsWith("port ", StringComparison.OrdinalIgnoreCase)
                    && int.TryParse(trimmed.AsSpan(5).Trim(), out var port)
                )
                {
                    return port;
                }
            }

            return DefaultSshPort;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return DefaultSshPort;
        }
    }

    private static async Task<TcpSnapshot> SampleTcpAsync(string host, CancellationToken ct)
    {
        var started = Stopwatch.GetTimestamp();
        IPAddress[] addresses;
        try
        {
            addresses = await Dns.GetHostAddressesAsync(host, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new TcpSnapshot(
                "dns",
                null,
                $"resolve {host}: {ex.GetType().Name}: {ex.Message}",
                Elapsed(started)
            );
        }

        if (addresses.Length == 0)
        {
            return new TcpSnapshot("dns", null, $"resolve {host}: no addresses", Elapsed(started));
        }

        ProcessStartInfo psi;
        string sampler;
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            sampler = "Get-NetTCPConnection";
            var list = string.Join(",", addresses.Select(a => $"'{a}'"));
            psi = new ProcessStartInfo("powershell")
            {
                ArgumentList =
                {
                    "-NoProfile",
                    "-NonInteractive",
                    "-Command",
                    $"Get-NetTCPConnection | Where-Object {{ $_.RemoteAddress -in @({list}) }} "
                        + "| Select-Object LocalPort,RemotePort,State,OwningProcess "
                        + "| Format-Table -AutoSize | Out-String -Width 160; "
                        + "netstat -s -p tcp | Select-String -Pattern 'Retransmitted|Segments Sent|Segments Received'",
                },
            };
        }
        else
        {
            sampler = "ss";
            psi = new ProcessStartInfo("ss") { ArgumentList = { "-tin" } };
            foreach (var (address, i) in addresses.Select((a, i) => (a, i)))
            {
                if (i > 0)
                {
                    psi.ArgumentList.Add("or");
                }

                psi.ArgumentList.Add("dst");
                psi.ArgumentList.Add(address.ToString());
            }
        }

        psi.UseShellExecute = false;
        psi.RedirectStandardOutput = true;
        psi.RedirectStandardError = true;
        psi.CreateNoWindow = true;

        try
        {
            using var process = Process.Start(psi);
            if (process is null)
            {
                return new TcpSnapshot(
                    sampler,
                    null,
                    "Process.Start returned null",
                    Elapsed(started)
                );
            }

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(ToolTimeout);
            var stdoutTask = process.StandardOutput.ReadToEndAsync(timeoutCts.Token);
            var stderrTask = process.StandardError.ReadToEndAsync(timeoutCts.Token);
            try
            {
                await process.WaitForExitAsync(timeoutCts.Token);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                process.Kill(entireProcessTree: true);
                return new TcpSnapshot(
                    sampler,
                    null,
                    $"timed out after {ToolTimeout.TotalSeconds:F0}s",
                    Elapsed(started)
                );
            }

            var stdout = Truncate((await stdoutTask).Trim());
            var stderr = Truncate((await stderrTask).Trim());
            return process.ExitCode == 0
                ? new TcpSnapshot(sampler, stdout, null, Elapsed(started))
                : new TcpSnapshot(
                    sampler,
                    stdout.Length > 0 ? stdout : null,
                    $"exit {process.ExitCode}: {stderr}",
                    Elapsed(started)
                );
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return new TcpSnapshot(
                sampler,
                null,
                $"{ex.GetType().Name}: {ex.Message}",
                Elapsed(started)
            );
        }
    }

    private static async Task<ReachabilityProbe> ProbeAsync(
        string host,
        int port,
        CancellationToken ct
    )
    {
        var started = Stopwatch.GetTimestamp();
        try
        {
            using var client = new TcpClient();
            using var connectCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            connectCts.CancelAfter(ConnectTimeout);
            await client.ConnectAsync(host, port, connectCts.Token);
            return new ReachabilityProbe(host, port, "connected", Elapsed(started), null);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            return new ReachabilityProbe(
                host,
                port,
                "timeout",
                Elapsed(started),
                $"no connect within {ConnectTimeout.TotalSeconds:F0}s"
            );
        }
        catch (Exception ex)
        {
            return new ReachabilityProbe(
                host,
                port,
                "failed",
                Elapsed(started),
                $"{ex.GetType().Name}: {ex.Message}"
            );
        }
    }

    private static string Truncate(string s) =>
        s.Length <= MaxOutputChars ? s : s[..MaxOutputChars] + "…";

    private static long Elapsed(long started) =>
        (long)Stopwatch.GetElapsedTime(started).TotalMilliseconds;
}
