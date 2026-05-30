namespace JunimoServer.Tests.Helpers;

/// <summary>
/// Per-host semaphore limiting how many video-extraction operations
/// (in-container ffmpeg TS→MP4 concat + Docker tar pull of the full
/// recording) can run simultaneously against a single Docker daemon.
/// Prevents daemon I/O / disk / SSH-tunnel saturation when multiple
/// containers finalize their recordings in parallel during run-end
/// cleanup.
///
/// One instance per <see cref="Infrastructure.DockerHost"/>; bounds are
/// independent across hosts because separate daemons share no resources.
/// Independent of <see cref="DockerStartLimiter"/> — extraction and
/// container start gate different daemon code paths.
///
/// Sized from each host's <c>concurrentExtractions</c> JSON field. When
/// omitted, <c>SDVD_MAX_CONCURRENT_EXTRACTIONS</c> wins if set; otherwise the
/// default is the host's own <c>serverSlots + clientSlots</c>.
///
/// <para>
/// <b>Poison semantics</b>: a host-scoped <see cref="CancellationTokenSource"/>
/// is linked into every <see cref="WaitAsync"/>. When <see cref="DockerHost.Poison"/>
/// fires, the source is cancelled and pending waiters return promptly with
/// <see cref="OperationCanceledException"/> instead of hanging until outer
/// cancellation.
/// </para>
/// </summary>
internal sealed class DockerExtractLimiter : IDisposable
{
    private readonly SemaphoreSlim _semaphore;
    private readonly CancellationTokenSource _poisonCts = new();
    private readonly string _hostId;
    private readonly int _maxConcurrent;

    public DockerExtractLimiter(string hostId, int maxConcurrent)
    {
        _hostId = hostId;
        _maxConcurrent = maxConcurrent;
        _semaphore = new SemaphoreSlim(maxConcurrent, maxConcurrent);
    }

    public int MaxConcurrent => _maxConcurrent;

    public Task WaitAsync(CancellationToken ct)
    {
        // Link caller's ct with the host-scoped poison CTS so Poison() unblocks
        // pending waiters. The linked source is disposed inside the wait body
        // so the outer caller's ct is never observed by a stale registration.
        var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _poisonCts.Token);
        return WaitTrace.RunAsync(
            WaitName.DockerExtractLimiter_ExtractSlot,
            async () =>
            {
                try
                {
                    await _semaphore.WaitAsync(linked.Token);
                }
                finally
                {
                    linked.Dispose();
                }
            },
            ct,
            snapshot: () => new { host_id = _hostId, maxConcurrent = _maxConcurrent });
    }

    public void Release() => _semaphore.Release();

    /// <summary>
    /// Cancels the host-scoped CTS, unblocking any pending <see cref="WaitAsync"/>
    /// callers. Idempotent. Called from <see cref="DockerHost.Poison"/>.
    /// </summary>
    public void CancelPending()
    {
        try { _poisonCts.Cancel(); } catch (ObjectDisposedException) { /* already disposed */ }
    }

    public void Dispose()
    {
        try { _poisonCts.Cancel(); } catch (ObjectDisposedException) { /* already disposed */ }
        _poisonCts.Dispose();
        _semaphore.Dispose();
    }

}
