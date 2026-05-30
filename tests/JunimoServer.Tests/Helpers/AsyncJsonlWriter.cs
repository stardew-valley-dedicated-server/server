using System.Text;
using System.Threading.Channels;

namespace JunimoServer.Tests.Helpers;

/// <summary>
/// Channel-based async JSONL writer. Producers serialize on their own thread
/// (preserving emit-time AsyncLocal context) and post the rendered JSON line
/// to a single-reader channel. A dedicated long-running consumer thread drains
/// the channel and writes to the underlying <see cref="StreamWriter"/>, batching
/// flushes once per drain pass.
///
/// <para>
/// <b>Why a channel, not a lock-and-flush per emit:</b> with thousands of
/// events per run and many parallel test slots, the per-emit
/// <c>WriteLine + Flush</c> contended on a single lock and forced a fsync per
/// event. The channel batches drain-pass flushes while still preserving arrival
/// order (single reader). Producers never block on disk I/O.
/// </para>
///
/// <para>
/// <b>Hard fault on writer failure:</b> a consumer fault is treated as run-fatal.
/// Diagnostics are not best-effort — losing failure context is exactly the
/// scenario the log exists for. On consumer fault, the next <see cref="Enqueue"/>
/// emits a single stderr line, signals <see cref="ShutdownCoordinator"/>, and
/// emits a <c>run_aborted</c>-style writer_fault marker via the parent process.
/// </para>
///
/// <para>
/// <b>Pre-flight disk check:</b> <see cref="Initialize"/> writes a 64-byte
/// canary and flushes it to surface disk-full / permission-denied errors at
/// startup, where the failure mode is loud (the run does not start) rather
/// than mid-run silent recording corruption.
/// </para>
/// </summary>
internal sealed class AsyncJsonlWriter : IAsyncDisposable
{
    private readonly StreamWriter _writer;
    private readonly Channel<object> _channel;
    private readonly Task _consumerTask;
    private readonly string _label;
    private readonly Action<Exception>? _onFault;
    private readonly bool _silentFault;

    private volatile bool _consumerFailed;
    private volatile Exception? _consumerFault;
    private int _faultReportedTimes;
    private int _completed;

    private AsyncJsonlWriter(StreamWriter writer, string label, Action<Exception>? onFault, bool silentFault)
    {
        _writer = writer;
        _label = label;
        _onFault = onFault;
        _silentFault = silentFault;
        // Unbounded so producers never block on disk I/O. Single-reader, multi-writer
        // for ordered drains. SingleWriter is false because Emit can be called from
        // any thread; SingleReader is true because the consumer task is the only reader.
        _channel = Channel.CreateUnbounded<object>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false,
            AllowSynchronousContinuations = false,
        });

        _consumerTask = Task.Factory.StartNew(
            ConsumeLoop,
            CancellationToken.None,
            TaskCreationOptions.LongRunning | TaskCreationOptions.DenyChildAttach,
            TaskScheduler.Default).Unwrap();
    }

    /// <summary>
    /// Opens <paramref name="path"/> for write, performs a 64-byte canary
    /// preflight, and starts the consumer thread. Throws on disk-full /
    /// permission-denied to fail the run loudly at startup.
    /// </summary>
    public static AsyncJsonlWriter Open(string path, string label, Action<Exception>? onFault = null)
    {
        // FileShare.Read so concurrent readers can copy/tar the live log.
        var fs = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read);
        var writer = new StreamWriter(fs) { AutoFlush = false };

        // Pre-flight: 64 bytes + flush. If the disk is full or permissions are
        // wrong, surface here rather than mid-run.
        var canary = new string(' ', 63) + "\n"; // 64 bytes
        writer.Write(canary);
        writer.Flush();
        // Reset position so the canary doesn't appear in the log file. Keeps the
        // log shape unchanged for downstream tooling that doesn't expect a header.
        writer.BaseStream.SetLength(0);
        writer.BaseStream.Position = 0;

        return new AsyncJsonlWriter(writer, label, onFault, silentFault: false);
    }

    /// <summary>
    /// Wraps an already-opened <see cref="StreamWriter"/>. The caller owns the
    /// writer's lifetime up to <see cref="DisposeAsync"/>; after that, the
    /// underlying stream is disposed.
    ///
    /// <para>
    /// <paramref name="silentFault"/> is true for transports where a sink
    /// failure must not abort the run (e.g. an IPC pipe that the parent
    /// process tore down — losing telemetry is fine; killing the test process
    /// is not).
    /// </para>
    /// </summary>
    public static AsyncJsonlWriter Wrap(StreamWriter writer, string label, Action<Exception>? onFault = null, bool silentFault = false)
    {
        return new AsyncJsonlWriter(writer, label, onFault, silentFault);
    }

    /// <summary>True once the consumer has aborted with an unrecoverable fault.</summary>
    public bool IsFaulted => _consumerFailed;

    /// <summary>The fault, if <see cref="IsFaulted"/> is true.</summary>
    public Exception? Fault => _consumerFault;

    /// <summary>
    /// Non-blocking enqueue of a serialized JSON line. On consumer fault, the
    /// first call after the fault reports the fault to stderr and invokes the
    /// configured <c>onFault</c> hook (e.g. <see cref="ShutdownCoordinator.SignalShutdown"/>).
    /// </summary>
    public void Enqueue(string json)
    {
        if (_consumerFailed)
        {
            ReportFaultOnce();
            return;
        }
        // TryWrite always succeeds on an unbounded channel that hasn't been
        // completed; failure only happens after Complete() (drain shutdown).
        _channel.Writer.TryWrite(json);
    }

    /// <summary>
    /// Schedules a "flush marker" through the channel and awaits its completion.
    /// Used by callers that need to read their own log mid-run, or by a
    /// per-test artifact phase that wants its events on disk before announcing
    /// the test result.
    /// </summary>
    public Task FlushAsync()
    {
        if (_consumerFailed) return Task.CompletedTask;
        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _channel.Writer.TryWrite(new FlushMarker(tcs));
        return tcs.Task;
    }

    /// <summary>
    /// Signals end-of-stream, awaits the consumer's drain, and flushes the
    /// underlying writer. Idempotent — subsequent calls return immediately.
    /// </summary>
    public async Task DrainAsync(TimeSpan timeout)
    {
        if (Interlocked.Exchange(ref _completed, 1) == 1)
        {
            // Already draining; just wait.
            try { await _consumerTask.WaitAsync(timeout); } catch { /* best effort */ }
            return;
        }
        _channel.Writer.TryComplete();
        try
        {
            await _consumerTask.WaitAsync(timeout);
        }
        catch (TimeoutException)
        {
            // Consumer hung; the log file may be missing late events but we
            // can't block shutdown on it.
        }
        catch
        {
            // Consumer faulted; fault was already reported via ReportFaultOnce.
        }
    }

    public async ValueTask DisposeAsync()
    {
        await DrainAsync(TimeSpan.FromSeconds(5));
        try { _writer.Flush(); } catch { /* best effort */ }
        try { _writer.Dispose(); } catch { /* best effort */ }
    }

    private async Task ConsumeLoop()
    {
        // Suppress flow so the consumer thread doesn't inherit the
        // ExecutionContext of whichever test happened to first wake the
        // writer. Per asynclocal-pitfalls.md "Long-lived background Task.Run
        // must suppress flow".
        using var _ = ExecutionContext.SuppressFlow();

        var sb = new StringBuilder(capacity: 8192);
        try
        {
            while (await _channel.Reader.WaitToReadAsync().ConfigureAwait(false))
            {
                sb.Clear();
                List<TaskCompletionSource>? pendingFlushes = null;

                while (_channel.Reader.TryRead(out var item))
                {
                    if (item is string json)
                    {
                        sb.Append(json);
                        sb.Append('\n');
                    }
                    else if (item is FlushMarker marker)
                    {
                        (pendingFlushes ??= new List<TaskCompletionSource>()).Add(marker.Tcs);
                    }
                }

                if (sb.Length > 0)
                {
                    _writer.Write(sb);
                }
                _writer.Flush();

                if (pendingFlushes != null)
                {
                    foreach (var tcs in pendingFlushes)
                        tcs.TrySetResult();
                }
            }
        }
        catch (Exception ex)
        {
            _consumerFault = ex;
            _consumerFailed = true;
            // Fail any pending flush waiters so they don't hang.
            try
            {
                while (_channel.Reader.TryRead(out var item))
                {
                    if (item is FlushMarker marker)
                        marker.Tcs.TrySetException(ex);
                }
            }
            catch { /* best effort */ }
            ReportFaultOnce();
        }
    }

    private void ReportFaultOnce()
    {
        if (Interlocked.Increment(ref _faultReportedTimes) != 1) return;
        var ex = _consumerFault;
        if (!_silentFault)
        {
            try
            {
                Console.Error.WriteLine(
                    $"[AsyncJsonlWriter:{_label}] FATAL: writer consumer faulted " +
                    $"({ex?.GetType().Name}: {ex?.Message}). Subsequent emits dropped.");
            }
            catch { /* stderr unavailable */ }
        }
        try { _onFault?.Invoke(ex ?? new InvalidOperationException("unknown writer fault")); }
        catch { /* never let onFault throw out of the writer */ }
    }

    private sealed record FlushMarker(TaskCompletionSource Tcs);
}
