using System.IO.Pipes;
using JunimoServer.TestRunner.Rendering;
using JunimoServer.Tests.Helpers;

namespace JunimoServer.TestRunner.IPC;

/// <summary>
/// Named pipe server that receives setup events (JSONL) from the xUnit child process
/// and dispatches them to the parent's renderer.
///
/// The child process connects via <c>SDVD_SETUP_PIPE</c> env var and writes one
/// JSON object per line. This server parses each line and calls the appropriate
/// <see cref="ITestRenderer"/> method (OnSetupPhaseStarted, OnSetupStep, OnSetupPhaseCompleted).
/// </summary>
public sealed class SetupPipeServer : IAsyncDisposable
{
    private readonly ITestRenderer _renderer;
    private readonly NamedPipeServerStream _pipe;
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _readTask;

    public string PipeName { get; }

    public SetupPipeServer(ITestRenderer renderer)
    {
        _renderer = renderer;
        PipeName = $"sdvd-setup-{Guid.NewGuid():N}";

        _pipe = new NamedPipeServerStream(
            PipeName,
            PipeDirection.In,
            maxNumberOfServerInstances: 1,
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous
        );

        using (ExecutionContext.SuppressFlow())
        {
            _readTask = Task.Run(() => ReadLoop(_cts.Token));
        }
    }

    // Hard caps that guard against a child process dumping a partial
    // stacktrace or hanging mid-line into the pipe. Without these, a runaway
    // child could wedge the parent UI indefinitely.
    private const int MaxLineBytes = 4096;
    private static readonly TimeSpan ReadDeadline = TimeSpan.FromSeconds(30);

    private async Task ReadLoop(CancellationToken ct)
    {
        try
        {
            await _pipe.WaitForConnectionAsync(ct);

            using var reader = new StreamReader(_pipe, leaveOpen: true);

            while (!ct.IsCancellationRequested)
            {
                // Per-read deadline: if the child stalls mid-line for > 30s
                // we abandon the line rather than let the UI hang. The
                // linked CTS cancels only the read; the outer ct keeps the
                // loop running so subsequent reads can still succeed.
                using var readCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                readCts.CancelAfter(ReadDeadline);

                string? line;
                try
                {
                    line = await reader.ReadLineAsync(readCts.Token);
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                {
                    // Deadline expired without the outer shutdown firing.
                    // Emit a diagnostic and keep looping — next line may recover.
                    JunimoServer.Tests.Helpers.InfrastructureEventLog.Emit(
                        "setup_ipc_read_deadline",
                        new { deadlineMs = (long)ReadDeadline.TotalMilliseconds }
                    );
                    continue;
                }

                if (line == null)
                {
                    break; // Client disconnected
                }

                // Oversize lines indicate the child wrote something weird
                // (raw stacktrace, corrupted framing). Drop with a diagnostic;
                // do NOT attempt to dispatch — JSON parsing of garbage could
                // itself trip deeper paths.
                if (line.Length > MaxLineBytes)
                {
                    JunimoServer.Tests.Helpers.InfrastructureEventLog.Emit(
                        "setup_ipc_oversized_line",
                        new { bytes = line.Length, limit = MaxLineBytes }
                    );
                    continue;
                }

                DispatchEvent(line);
            }
        }
        catch (OperationCanceledException)
        {
            // Expected on shutdown
        }
        catch (IOException)
        {
            // Broken pipe: child process exited
        }
        catch (Exception ex)
        {
            // Never crash the parent process due to pipe errors
            Console.Error.WriteLine($"[SetupPipeServer] Unexpected error in read loop: {ex}");
        }
    }

    private void DispatchEvent(string json)
    {
        // Catch renderer/parse exceptions here so a single bad line doesn't kill the
        // pipe read loop; EventDispatcher itself logs the first instance per type.
        try
        {
            EventDispatcher.Dispatch(json, _renderer);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(
                $"[SetupPipeServer] dispatch threw ({ex.GetType().Name}): {ex.Message}"
            );
        }
    }

    /// <summary>
    /// Wait for the read loop to drain naturally — i.e. for the child process to close
    /// the write end of the pipe (EOF). Bounded by <paramref name="timeout"/>; on
    /// timeout, returns false without cancelling so the caller can decide whether to
    /// force-dispose. Idempotent: calling after the loop has already exited returns true
    /// immediately.
    ///
    /// Use this on the graceful shutdown path so messages in the kernel pipe buffer
    /// (e.g. flaky_tests, emitted from TestSummaryFixture.DisposeAsync just before the
    /// child exits) are dispatched into the renderer before downstream code reads state.
    /// </summary>
    public async Task<bool> DrainAsync(TimeSpan timeout)
    {
        var completed = await Task.WhenAny(_readTask, Task.Delay(timeout)) == _readTask;
        if (completed)
        {
            try
            {
                await _readTask;
            }
            catch
            { /* swallow; ReadLoop handles its own errors */
            }
        }
        return completed;
    }

    public async ValueTask DisposeAsync()
    {
        await _cts.CancelAsync();
        try
        {
            await _readTask;
        }
        catch
        { /* swallow; ReadLoop handles its own errors */
        }
        await _pipe.DisposeAsync();
        _cts.Dispose();
    }
}
