using System.Diagnostics;
using System.Security.Cryptography;
using JunimoServer.Tests.Helpers;

namespace JunimoServer.Tests.Clients;

/// <summary>
/// DelegatingHandler that emits an <c>http_request</c> event for every outbound
/// request. Behavior tiers off <see cref="TestTracing.Level"/>:
///
/// <list type="bullet">
///   <item><b>None</b>: no <c>X-Request-Id</c>, no response-body capture.</item>
///   <item><b>Basic</b>: <c>X-Request-Id</c> on mutating verbs
///   (POST/PUT/PATCH/DELETE) so writes correlate with the mod-side timeline;
///   reads stay header-free.</item>
///   <item><b>Full</b>: <c>X-Request-Id</c> on every verb, plus response-body
///   capture: the body is buffered, rewrapped so downstream deserialization
///   still works, truncated to <see cref="RespBodyMaxChars"/>, and emitted as
///   <c>respBody</c>.</item>
/// </list>
///
/// <para>
/// <c>X-Test-Id</c> (the originating test's display name) is attached at every
/// level, so every server event during the request is attributable to its test
/// via <c>RequestContext.TestId</c>, including reads that carry no
/// <c>X-Request-Id</c>.
/// </para>
///
/// <para>
/// <c>clientKind</c> distinguishes calls to the server mod ("server")
/// from calls to the test-client mod ("test-client").
/// </para>
///
/// <para>
/// Captured <c>respBody</c> is redacted by the runner's <c>ReportRedactor</c>
/// scrub over <c>infrastructure.jsonl</c> (<c>ScrubRunFilesInPlace</c>), like
/// every other diagnostic in that stream.
/// </para>
/// </summary>
internal sealed class TracingHandler : DelegatingHandler
{
    private const string RequestIdHeader = "X-Request-Id";
    private const string TestIdHeader = "X-Test-Id";
    private const string SnapshotAgeHeader = "X-Snapshot-Age-Ms";
    private const string PredicateChangedAtHeader = "X-Predicate-Changed-At-Ms-Ago";

    /// <summary>
    /// Truncation cap for the captured <c>respBody</c>. Bounds the growth of
    /// <c>infrastructure.jsonl</c> at <c>Full</c> (one body per HTTP call,
    /// including every poll) against its soft size limit
    /// (<see cref="InfrastructureEventLog.SoftSizeLimitBytes"/>) while keeping
    /// a full snapshot response readable in the artifact.
    /// </summary>
    private const int RespBodyMaxChars = 2000;
    private const string TruncatedMarker = "…[truncated]";

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

        // Attached at every level. Read from the ambient test identity per request,
        // not set on HttpClient.DefaultRequestHeaders, because the client is shared
        // across tests. DisplayName is already header-safe (ToHeaderSafeId), so the
        // server's testId equals the emitted test.displayName.
        var testId = TestIdentityContext.Current?.DisplayName;
        if (!string.IsNullOrEmpty(testId))
        {
            request.Headers.Remove(TestIdHeader);
            request.Headers.TryAddWithoutValidation(TestIdHeader, testId);
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
            string? respBody = null;
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

            // Full only: buffer the body, then rewrap it (copying content headers)
            // so downstream deserialization reads the same bytes. respBytes is the
            // buffered length, accurate even without Content-Length. A capture
            // failure must never break the request.
            if (_level == TestTracingLevel.Full && response.Content != null)
            {
                try
                {
                    var original = response.Content;
                    var bytes = await original.ReadAsByteArrayAsync(cancellationToken);
                    respBytes = bytes.LongLength;

                    var rewrapped = new ByteArrayContent(bytes);
                    foreach (var header in original.Headers)
                    {
                        rewrapped.Headers.TryAddWithoutValidation(header.Key, header.Value);
                    }
                    response.Content = rewrapped;
                    original.Dispose();

                    // Decode only a bounded prefix instead of the whole body: a char
                    // is at most 4 UTF-8 bytes, so RespBodyMaxChars*4 bytes always
                    // covers the cap. The marker counts against the cap.
                    var decodeBytes = Math.Min(bytes.Length, RespBodyMaxChars * 4);
                    var text = System.Text.Encoding.UTF8.GetString(bytes, 0, decodeBytes);
                    var truncated = decodeBytes < bytes.Length || text.Length > RespBodyMaxChars;
                    respBody = truncated
                        ? string.Concat(
                            text.AsSpan(0, RespBodyMaxChars - TruncatedMarker.Length),
                            TruncatedMarker
                        )
                        : text;
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    respBody = $"[capture failed: {ex.GetType().Name}]";
                }
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
                    respBody,
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
