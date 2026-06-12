using System.Diagnostics;
using System.Security.Cryptography;
using JunimoServer.Tests.Helpers;

namespace JunimoServer.Tests.Clients;

/// <summary>
/// DelegatingHandler that emits an <c>http_request</c> event for every outbound
/// request. Behavior tiers off <see cref="TestTracing.Level"/>:
///
/// <list type="bullet">
///   <item><b>None</b> — cheapest. Skip <c>X-Request-Id</c> generation, body
///   buffering, and response summarization. Emit method, path, status, duration,
///   request-byte size only.</item>
///   <item><b>Basic</b> — adds <c>X-Request-Id</c> for mutating verbs
///   (POST/PUT/PATCH/DELETE) so a debug session can correlate writes with the
///   mod-side timeline; reads stay header-free.</item>
///   <item><b>Full</b> — today's behavior. Body buffer + <c>ByteArrayContent</c>
///   rebuild, <c>respSummary</c> for diagnostic endpoints, request-id on every
///   verb.</item>
/// </list>
///
/// <para>
/// <c>clientKind</c> distinguishes calls to the server mod ("server")
/// from calls to the test-client mod ("test-client").
/// </para>
/// </summary>
internal sealed class TracingHandler : DelegatingHandler
{
    private const string RequestIdHeader = "X-Request-Id";
    private const string SnapshotAgeHeader = "X-Snapshot-Age-Ms";
    private const string PredicateChangedAtHeader = "X-Predicate-Changed-At-Ms-Ago";

    private readonly string _clientKind;
    private readonly TestTracingLevel _level;

    public TracingHandler(string clientKind)
    {
        _clientKind = clientKind;
        _level = TestTracing.Level;
    }

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken
    )
    {
        var method = request.Method.Method;
        var path = request.RequestUri?.AbsolutePath ?? "?";
        var pathAndQuery = request.RequestUri?.PathAndQuery ?? path;

        var ambient = CorrelationContext.Current;
        // Request-id generation tier:
        //   None:  no header, no scope.
        //   Basic: header on mutating verbs only (correlate writes with mod events).
        //   Full:  header on every verb (today's behavior).
        var isMutation = IsMutationMethod(method);
        var attachRequestId = _level switch
        {
            TestTracingLevel.None => false,
            TestTracingLevel.Basic => isMutation,
            _ => true, // Full
        };

        string? requestId = null;
        IDisposable? scope = null;
        if (attachRequestId)
        {
            requestId = ambient ?? NewRequestId();
            request.Headers.Remove(RequestIdHeader);
            request.Headers.Add(RequestIdHeader, requestId);
            if (ambient == null)
            {
                scope = CorrelationContext.BeginWithId(requestId);
            }
        }

        long? reqBytes = null;
        if (request.Content != null && isMutation)
        {
            // Cheap size read for known-bounded content types.
            if (request.Content.Headers.ContentLength is long cl)
            {
                reqBytes = cl;
            }
        }

        var sw = Stopwatch.StartNew();

        try
        {
            var response = await base.SendAsync(request, cancellationToken);
            sw.Stop();

            long? respBytes = null;
            long? snapshotAgeMs = null;
            long? predicateChangedMsAgo = null;

            // Capture response headers we care about.
            if (response.Headers.TryGetValues(SnapshotAgeHeader, out var ageValues))
            {
                var first = ageValues.FirstOrDefault();
                if (long.TryParse(first, out var parsed))
                {
                    snapshotAgeMs = parsed;
                }
            }
            if (response.Headers.TryGetValues(PredicateChangedAtHeader, out var predValues))
            {
                var first = predValues.FirstOrDefault();
                if (long.TryParse(first, out var parsed))
                {
                    predicateChangedMsAgo = parsed;
                }
            }

            // Publish to the ambient diagnostic slot so a polling helper can
            // read the winning response's predicate-transition time without
            // threading it through every WaitFor*Async signature. Always
            // write — including null on missing header — so the slot reflects
            // the most-recent observation rather than a stale earlier value.
            HttpResponseDiagnostics.LastPredicateChangedMsAgo = predicateChangedMsAgo;

            // respBytes is Full-only — at None / Basic we drop the field. At
            // Full, respBytes is read from the Content-Length response header.
            if (
                _level == TestTracingLevel.Full
                && response.Content?.Headers?.ContentLength is long bodyLen
            )
            {
                respBytes = bodyLen;
            }

            InfrastructureEventLog.Emit(
                "http_request",
                new
                {
                    clientKind = _clientKind,
                    method,
                    path = pathAndQuery,
                    status = (int)response.StatusCode,
                    durationMs = sw.ElapsedMilliseconds,
                    reqBytes,
                    respBytes,
                    snapshotAgeMs,
                    predicateChangedMsAgo,
                }
            );
            return response;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            sw.Stop();
            InfrastructureEventLog.Emit(
                "http_request",
                new
                {
                    clientKind = _clientKind,
                    method,
                    path = pathAndQuery,
                    error = $"{ex.GetType().Name}: {ex.Message}",
                    durationMs = sw.ElapsedMilliseconds,
                    reqBytes,
                }
            );
            throw;
        }
        finally
        {
            scope?.Dispose();
        }
    }

    private static bool IsMutationMethod(string method) =>
        method == "POST" || method == "PUT" || method == "PATCH" || method == "DELETE";

    private static string NewRequestId()
    {
        Span<byte> bytes = stackalloc byte[8];
        RandomNumberGenerator.Fill(bytes);
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }
}
