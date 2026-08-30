using System.Diagnostics;
using JunimoServer.Tests.Helpers;
using JunimoServer.Tests.Infrastructure;
using JunimoServer.Tests.Schema.Events;

namespace JunimoServer.Tests.Clients;

/// <summary>
/// DelegatingHandler that makes a <see cref="ServerApiClient"/> transparently survive a
/// transient SSH forward drop. One shared <c>ssh -M</c> master carries every <c>-L</c>
/// forward, so a master keepalive blip tears them all down at once while the host + daemon
/// stay alive (reproduced 2026-06-26: Docker stats never stopped). Without healing, every
/// in-flight request over the dropped forward fails (RST/ConnectionRefused) and the test
/// dies even though the server is fine and the forward is re-openable.
///
/// <para>Two jobs, both transparent to the ~20 call sites above it:</para>
/// <list type="number">
///   <item><b>Live base address.</b> A forward heal re-opens on a NEW loopback port (the
///     old one is dead), so the client must dial the CURRENT port. This handler rewrites
///     each request's scheme/host/port from <see cref="_liveBaseUrl"/> every send, rather
///     than trusting <c>HttpClient.BaseAddress</c> (frozen at construction).</item>
///   <item><b>Heal + retry.</b> On a forward-scoped transport fault it invokes
///     <see cref="_healAsync"/> (which corroborates the master is alive and re-opens the
///     forward) and retries the SAME request against the freshly-healed port, bounded by
///     <see cref="_healRetryBudget"/> — but only for requests the call site marked via
///     <see cref="RetrySafeRequest"/>; an unmarked request propagates the fault.
///     Non-transport faults and success pass straight through.</item>
/// </list>
/// </summary>
internal sealed class ForwardHealingHandler : DelegatingHandler
{
    private readonly Func<string> _liveBaseUrl;
    private readonly Func<CancellationToken, Task<bool>> _healAsync;
    private readonly TimeSpan _healRetryBudget;
    private readonly TimeSpan _retryDelay;

    public ForwardHealingHandler(
        Func<string> liveBaseUrl,
        Func<CancellationToken, Task<bool>> healAsync,
        TimeSpan? healRetryBudget = null,
        TimeSpan? retryDelay = null
    )
    {
        _liveBaseUrl = liveBaseUrl;
        _healAsync = healAsync;
        // Long enough to outlast a master keepalive blip + heal (watchdog cadence ~5s,
        // -O check retries ~8s, re-open ~1s); short enough not to mask a real outage.
        _healRetryBudget = healRetryBudget ?? TimeSpan.FromSeconds(45);
        _retryDelay = retryDelay ?? TimeSpan.FromSeconds(2);
    }

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken
    )
    {
        long? firstFault = null;
        var attempt = 0;
        while (true)
        {
            RebaseToLiveUrl(request);
            try
            {
                return await base.SendAsync(request, cancellationToken);
            }
            catch (Exception ex)
                when (!cancellationToken.IsCancellationRequested
                    && TransportFaultClassifier.Classify(ex) is { ForwardScoped: true } verdict
                )
            {
                // Forward-scoped fault: the -L forward dropped. Heal it and retry the same
                // request against the new port, within budget. When the budget is exhausted or
                // the master is genuinely dead, the fault is UNRECOVERABLE transport — i.e.
                // infrastructure, not a product bug. Throwing it raw makes the test FAIL (and
                // trip StopOnFail → cascade), so instead convert it to an InfrastructureSkipException:
                // the test is reported skipped, not failed. A real assertion/crash never reaches
                // here (Classify returns ForwardScoped=false for it), so genuine bugs still fail.
                attempt++;
                firstFault ??= Stopwatch.GetTimestamp();
                var port = request.RequestUri?.Port;

                // The forward can drop AFTER the server fully processed the request, so a
                // re-send repeats the action. Only call sites that declared the request safe
                // to repeat (RetrySafeRequest) get a retry; the rest propagate the raw fault.
                if (!RetrySafeRequest.IsMarked(request))
                {
                    EmitAttempt(
                        attempt,
                        port,
                        ex,
                        verdict.Reason,
                        healMs: null,
                        "not_retry_safe",
                        healError: null,
                        healStackTrace: null
                    );
                    throw;
                }

                if (Stopwatch.GetElapsedTime(firstFault.Value) >= _healRetryBudget)
                {
                    EmitAttempt(
                        attempt,
                        port,
                        ex,
                        verdict.Reason,
                        healMs: null,
                        "budget_exhausted",
                        healError: null,
                        healStackTrace: null
                    );
                    throw new InfrastructureSkipException(
                        $"Unrecoverable forward-scoped transport fault after {_healRetryBudget.TotalSeconds:F0}s "
                            + $"heal budget ({ex.GetType().Name}: {ex.Message}). Skipped — not a test defect."
                    );
                }

                // `heal_failed` is the heal's own verdict (master dead); `heal_threw` means the
                // heal itself threw, so the attempt carries its stack trace.
                bool healed;
                string? healError = null;
                string? healStackTrace = null;
                var healStarted = Stopwatch.GetTimestamp();
                try
                {
                    healed = await _healAsync(cancellationToken);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception healEx)
                {
                    healed = false;
                    healError = TransportEventFormat.Chain(healEx);
                    healStackTrace = TransportEventFormat.StackTrace(healEx);
                }

                var healMs = (long)Stopwatch.GetElapsedTime(healStarted).TotalMilliseconds;

                EmitAttempt(
                    attempt,
                    port,
                    ex,
                    verdict.Reason,
                    healMs,
                    healed ? "healed"
                        : healError is null ? "heal_failed"
                        : "heal_threw",
                    healError,
                    healStackTrace
                );

                if (!healed)
                {
                    var why = healError is null ? "ssh master is down" : $"heal threw {healError}";
                    throw new InfrastructureSkipException(
                        $"Forward-scoped transport fault ({ex.GetType().Name}: {ex.Message}) and "
                            + $"the {why}. Skipped — not a test defect."
                    );
                }

                await Task.Delay(_retryDelay, cancellationToken);

                // A used HttpRequestMessage can't be re-sent — clone it for the retry.
                request = await CloneRequestAsync(request);
            }
        }
    }

    /// <summary>
    /// One <c>forward_heal_attempt</c> per heal cycle, so a run's artifact shows
    /// how many times which port was re-opened for which fault — the only way to
    /// tell a single blip from two handlers re-opening the same forward in turns.
    /// </summary>
    private static void EmitAttempt(
        int attempt,
        int? port,
        Exception fault,
        string? classification,
        long? healMs,
        string outcome,
        string? healError,
        string? healStackTrace
    )
    {
        InfrastructureEventLog.Emit(
            TransportEventNames.ForwardHealAttempt,
            new ForwardHealAttemptEvent(
                attempt,
                port,
                fault.GetType().Name,
                fault.Message,
                TransportEventFormat.Chain(fault),
                classification,
                healMs,
                outcome,
                healError,
                healStackTrace
            )
        );
    }

    /// <summary>
    /// Rewrites the request URI's scheme/host/port to the current live base URL, preserving
    /// path + query. Lets the client follow the forward to its new port after a heal without
    /// touching <c>HttpClient.BaseAddress</c> (which is immutable once a request has been sent).
    /// </summary>
    private void RebaseToLiveUrl(HttpRequestMessage request)
    {
        var baseUrl = _liveBaseUrl();
        if (string.IsNullOrEmpty(baseUrl) || request.RequestUri is null)
        {
            return;
        }

        var baseUri = new Uri(baseUrl);
        var current = request.RequestUri;
        var pathAndQuery = current.IsAbsoluteUri ? current.PathAndQuery : current.OriginalString;
        request.RequestUri = new Uri(
            new Uri($"{baseUri.Scheme}://{baseUri.Authority}"),
            pathAndQuery
        );
    }

    private static async Task<HttpRequestMessage> CloneRequestAsync(HttpRequestMessage original)
    {
        var clone = new HttpRequestMessage(original.Method, original.RequestUri)
        {
            Version = original.Version,
        };

        foreach (var header in original.Headers)
        {
            clone.Headers.TryAddWithoutValidation(header.Key, header.Value);
        }

        // Options carry the retry-safe mark; without them the second retry would be refused.
        foreach (var option in original.Options)
        {
            ((IDictionary<string, object?>)clone.Options)[option.Key] = option.Value;
        }

        if (original.Content != null)
        {
            // Buffer the content so it can be re-read on the retry.
            var bytes = await original.Content.ReadAsByteArrayAsync();
            clone.Content = new ByteArrayContent(bytes);
            foreach (var header in original.Content.Headers)
            {
                clone.Content.Headers.TryAddWithoutValidation(header.Key, header.Value);
            }
        }

        return clone;
    }
}
