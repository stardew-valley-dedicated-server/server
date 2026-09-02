using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using StardewModdingAPI;

namespace JunimoServer.Services.Api;

/// <summary>
/// Takes the API port over from the startup script's phase responder (docker/rootfs/startapp.sh),
/// which answers /status until the mod binds. Stopping it right before the bind keeps the port
/// answered at every moment. Only a live LISTEN socket can block the bind (HttpListener sets
/// SO_REUSEADDR, so the responder's TIME_WAIT entries never do), so that is what is asserted after
/// the stop, once, with no bind retry.
/// </summary>
internal static class ApiPortHandoff
{
    private const int Sigterm = 15;
    private const int Esrch = 3;
    private const string ProcNetListenState = "0A";
    private const int ProcNetLocalAddressColumn = 1;
    private const int ProcNetStateColumn = 3;
    private static readonly TimeSpan TeardownTimeout = TimeSpan.FromSeconds(5);

    [DllImport("libc", SetLastError = true)]
    private static extern int kill(int pid, int sig);

    /// <summary>
    /// A null or missing <paramref name="pidFile"/> means no responder is running (API disabled,
    /// or the mod runs outside the container).
    /// </summary>
    public static void TakeOver(string? pidFile, int port, IMonitor monitor)
    {
        if (string.IsNullOrEmpty(pidFile) || !File.Exists(pidFile))
        {
            return;
        }

        if (!int.TryParse(File.ReadAllText(pidFile).Trim(), out var pid))
        {
            throw new InvalidOperationException(
                $"Phase responder pid file {pidFile} does not contain a pid; refusing to bind port {port}"
            );
        }

        // Never SIGKILL: the loop's TERM trap is what stops its nc child; KILL would orphan it
        // with the port still bound.
        if (kill(pid, Sigterm) != 0)
        {
            var errno = Marshal.GetLastWin32Error();
            if (errno != Esrch)
            {
                throw new InvalidOperationException(
                    $"Could not signal the phase responder (pid {pid}, errno {errno}); refusing to bind port {port}"
                );
            }
        }

        var sw = Stopwatch.StartNew();
        WaitUntil(() => !IsProcessAlive(pid), sw);
        WaitUntil(() => !IsPortListening(port), sw);

        if (IsPortListening(port))
        {
            throw new InvalidOperationException(
                $"Port {port} still has a LISTEN socket {sw.ElapsedMilliseconds}ms after stopping the phase responder (pid {pid}); refusing to bind"
            );
        }

        // The container healthcheck starts probing /health once this file is gone.
        File.Delete(pidFile);
        monitor.Log(
            $"Took over API port {port} from the phase responder in {sw.ElapsedMilliseconds}ms",
            LogLevel.Info
        );
    }

    private static void WaitUntil(Func<bool> condition, Stopwatch sw)
    {
        while (!condition() && sw.Elapsed < TeardownTimeout)
        {
            Thread.Sleep(20);
        }
    }

    private static bool IsProcessAlive(int pid)
    {
        string stat;
        try
        {
            stat = File.ReadAllText($"/proc/{pid}/stat");
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }

        // The command name before the state may contain spaces, hence the scan from its ')'.
        var stateIndex = stat.LastIndexOf(')') + 2;
        var isZombie = stateIndex < stat.Length && stat[stateIndex] == 'Z';
        return !isZombie;
    }

    private static bool IsPortListening(int port)
    {
        var portSuffix = ":" + port.ToString("X4");
        foreach (var table in new[] { "/proc/net/tcp", "/proc/net/tcp6" })
        {
            if (!File.Exists(table))
            {
                continue;
            }

            foreach (var line in File.ReadLines(table))
            {
                var fields = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (
                    fields.Length > ProcNetStateColumn
                    && fields[ProcNetStateColumn] == ProcNetListenState
                    && fields[ProcNetLocalAddressColumn]
                        .EndsWith(portSuffix, StringComparison.Ordinal)
                )
                {
                    return true;
                }
            }
        }
        return false;
    }
}
