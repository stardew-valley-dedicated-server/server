using JunimoServer.Tests.Helpers;
using JunimoServer.Tests.Infrastructure;
using Xunit;

namespace JunimoServer.Tests;

/// <summary>
/// Regression guard for attach-cli (the operator console reached via
/// `docker compose exec server attach-cli`): the baseimage rewrites /etc/passwd on every boot
/// with /sbin/nologin as every account's shell; that path resolves on usr-merged images
/// (Debian 13+), so without attach-cli's default-shell pin tmux adopts it — panes run
/// `nologin -c ...` and die instantly, the session collapses, and attach-cli prints
/// "no server running" / "no sessions".
///
/// Only an /init-booted container rewrites passwd, so the bug needs a broker-leased server;
/// `docker run --entrypoint bash` would mask it.
///
/// API-only. Never calls GetClientAsync().
/// </summary>
[TestServer(Isolation = IsolationMode.SharedAssembly, Clients = 0, Artifacts = false)]
public class AttachCliTests : TestBase
{
    /// <summary>
    /// Whole scenario — launch, wait, inspect, cleanup — in ONE exec (docker exec degrades
    /// badly under parallel load; see .claude/rules/minimize-exec-count-and-cut-unconsumed-diagnostic-execs.md).
    /// The fake TTY (util-linux `script`) makes the final `tmux attach-session` block instead of
    /// fail, keeping the session alive for inspection; `tmux kill-session` later unblocks it and
    /// the tmux server exits with its last session, so the reused server stays pristine. That
    /// kill (plus the final sweep) is the ONLY cleanup — bash drops attach-cli's EXIT trap at
    /// its `exec`. Verdicts ride sentinel-prefixed stdout lines and the
    /// script always exits 0, so failures self-identify. `timeout 60` is the backstop reaper;
    /// TERM is set because docker exec provides none.
    /// </summary>
    private const string InContainerScenario = """
        set -u
        LOG=/tmp/attach-cli-test.$$.log
        echo "PASSWD_SHELL:$(getent passwd "$(id -u)" | cut -d: -f7)"
        TERM=xterm timeout 60 script -q -e -c /opt/base/bin/attach-cli /dev/null >"$LOG" 2>&1 &
        wrapper=$!
        session=
        i=0
        # Wait for the session AND its second pane (split-window trails new-session)
        while [ $i -lt 150 ]; do
            session=$(tmux list-sessions -F '#{session_name}' 2>/dev/null | grep '^stardew-server-' | head -n 1)
            if [ -n "$session" ]; then
                panes=$(tmux list-panes -t "$session" 2>/dev/null | wc -l)
                [ "$panes" -ge 2 ] && break
            fi
            sleep 0.1
            i=$((i + 1))
        done
        if [ -z "$session" ]; then
            echo "VERDICT:NO_SESSION"
            kill "$wrapper" 2>/dev/null
        else
            # Settle: broken-shell panes die within milliseconds (tmux destroys them,
            # collapsing the session) — the delay makes that observable instead of racing setup
            sleep 1
            echo "SESSION:${session}"
            echo "PANES_DEAD:$(tmux list-panes -t "$session" -F '#{pane_dead}' 2>/dev/null | tr '\n' ' ' | sed 's/ *$//')"
            echo "DEFAULT_SHELL:$(tmux show-options -g default-shell 2>/dev/null)"
            tmux kill-session -t "$session" 2>/dev/null
        fi
        wait "$wrapper" 2>/dev/null
        # Sweep any session created after the poll gave up — nothing else removes one (the EXIT
        # trap does not survive attach-cli's exec) and this test is the only tmux producer
        tmux kill-server 2>/dev/null
        # Grep the FULL log in-container; only a capped excerpt is printed so the C# Log()
        # annotation stays under SetupPipeServer's 4096-byte IPC line cap (oversized = dropped)
        symptoms=$(grep -c -e 'no server running' -e 'no sessions' "$LOG" 2>/dev/null)
        echo "SYMPTOM_LINES:${symptoms:-LOG_MISSING}"
        echo "LOG_BEGIN"
        head -c 1500 "$LOG" 2>/dev/null
        echo ""
        echo "LOG_END"
        rm -f "$LOG"
        exit 0
        """;

    public AttachCliTests() { }

    /// <summary>
    /// attach-cli must bring up a live two-pane tmux session (default-shell pinned to /bin/sh)
    /// in a booted container whose passwd carries the /sbin/nologin shells.
    /// </summary>
    [Fact]
    public async Task AttachCli_StartsSessionWithLivePanes()
    {
        // .WaitAsync because Testcontainers' ExecAsync does not poll the CT mid-exec; the
        // in-container timeout bounds the scenario itself.
        var result = await Server
            .Container.ExecAsync(new[] { "sh", "-c", InContainerScenario }, TestCt)
            .WaitAsync(TestCt);

        Log($"attach-cli scenario output:\n{result.Stdout}");
        if (!string.IsNullOrWhiteSpace(result.Stderr))
        {
            Log($"attach-cli scenario stderr:\n{result.Stderr}");
        }

        Assert.True(
            result.ExitCode == DockerExitCodes.Success,
            $"Scenario script must exit 0 (verdicts ride stdout sentinels); got {result.ExitCode}: {result.Stderr}"
        );

        var passwdShell = ExtractLine(result.Stdout, "PASSWD_SHELL:");
        Assert.True(
            passwdShell == "/sbin/nologin",
            $"Scenario precondition: exec user's passwd shell must be /sbin/nologin (the baseimage-written value the regression needs); got '{passwdShell}' — if the baseimage changed its passwd rewrite, re-evaluate this test"
        );

        var attachLog = ExtractLog(result.Stdout);

        // Only this class produces stardew-server-* sessions, so a SharedAssembly server
        // can't show us another test's session. The scenario's prefix match and kill-server
        // sweep both assume ONE concurrent attach-cli run — a second method in this class
        // would race it (xUnit runs class methods concurrently) and needs serialization.
        var session = ExtractLine(result.Stdout, "SESSION:");
        Assert.True(
            session != null && session.StartsWith("stardew-server-"),
            $"attach-cli must create a stardew-server-* tmux session that survives until inspection; got none (session died or never started). attach-cli output (excerpt): {attachLog}"
        );

        var panesDead = ExtractLine(result.Stdout, "PANES_DEAD:");
        Assert.True(
            panesDead == "0 0",
            $"Session must still have both panes ('0 0' pane_dead flags) after settle; got '{panesDead}' — tmux destroys a pane whose process dies, so a missing pane means it spawned with a broken shell. attach-cli output (excerpt): {attachLog}"
        );

        var defaultShell = ExtractLine(result.Stdout, "DEFAULT_SHELL:");
        Assert.True(
            defaultShell == "default-shell /bin/sh",
            $"tmux default-shell must be pinned to /bin/sh; got '{defaultShell}'"
        );

        // Counted in-container over the full log (the excerpt above is capped for IPC).
        var symptomLines = ExtractLine(result.Stdout, "SYMPTOM_LINES:");
        Assert.True(
            symptomLines == "0",
            $"attach-cli output must contain neither 'no server running' nor 'no sessions' (the reported regression symptoms); got {symptomLines} matching line(s). attach-cli output (excerpt): {attachLog}"
        );
    }

    /// <summary>Returns the value after the first line starting with <paramref name="prefix"/>.</summary>
    private static string? ExtractLine(string output, string prefix)
    {
        foreach (var line in output.Split('\n'))
        {
            var trimmed = line.TrimEnd('\r');
            if (trimmed.StartsWith(prefix, StringComparison.Ordinal))
            {
                return trimmed[prefix.Length..].Trim();
            }
        }
        return null;
    }

    /// <summary>Returns the capped attach-cli output excerpt between the LOG_BEGIN/LOG_END sentinels.</summary>
    private static string ExtractLog(string output)
    {
        var start = output.IndexOf("LOG_BEGIN", StringComparison.Ordinal);
        var end = output.IndexOf("LOG_END", StringComparison.Ordinal);
        if (start < 0 || end <= start)
        {
            return "<not captured>";
        }
        start += "LOG_BEGIN".Length;
        return output[start..end].Trim();
    }
}
