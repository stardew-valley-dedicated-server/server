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
///   <item><b>Full</b> — request-id on every verb, plus response-body capture:
///   the body is buffered, rewrapped so downstream deserialization still works,
///   truncated to <see cref="RespBodyMaxChars"/>, and emitted as <c>respBody</c>
///   so artifacts record exactly what a read returned.</item>
/// </list>
///
/// <para>
/// <c>X-Test-Id</c> (the originating test's display name) is attached to every
/// request at <b>every</b> level — independent of tracing — so the server's
/// <c>http_served</c> (and every other server event during the request) is
/// attributable to its test via <c>ModRequestContext.TestId</c>, even for reads
/// that carry no <c>X-Request-Id</c>.
/// </para>
///
/// <para>
/// <c>clientKind</c> distinguishes calls to the server mod ("server")
/// from calls to the test-client mod ("test-client").
/// </para>
///
/// <para>
/// Captured <c>respBody</c> is redacted by the runner's in-place
/// <c>ReportRedactor</c> scrub over <c>infrastructure.jsonl</c>
/// (<c>ScrubRunFilesInPlace</c>, run on every run), the same as every other
/// diagnostic in that stream.
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

        // Attach the test-id regardless of tracing level (unlike X-Request-Id):
        // the server binds it as an AsyncLocal for the request duration so every
        // server event — including reads that carry no request-id — is attributable
        // to its originating test. Sourced per-async-flow from the ambient test
        // identity, so it must be set here, not on HttpClient.DefaultRequestHeaders.
        // Normalize to printable ASCII (the header contract; see ToPrintableAscii) — the same
        // normalizer that produced test.displayName, so the server's testId matches it exactly.
        var testId = TestIdentityContext.Current?.DisplayName;
        if (!string.IsNullOrEmpty(testId))
        {
            request.Headers.Remove(TestIdHeader);
            request.Headers.TryAddWithoutValidation(
                TestIdHeader,
                TestIdentityContext.ToPrintableAscii(testId)
            );
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

            // respBytes/respBody are Full-only — at None / Basic we drop both.
            // At Full, buffer the response body so we can record what the read
            // returned, then rewrap it in a fresh ByteArrayContent (copying the
            // original content headers) so downstream ServerApiClient
            // deserialization still reads the same bytes. respBytes comes from the
            // actual buffered length (accurate even when Content-Length is absent).
            // Never let a body-capture failure break the request.
            if (_level == TestTracingLevel.Full && response.Content != null)
            {
                try
                {
                    var bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken);
                    respBytes = bytes.LongLength;

                    var rewrapped = new ByteArrayContent(bytes);
                    foreach (var header in response.Content.Headers)
                    {
                        rewrapped.Headers.TryAddWithoutValidation(header.Key, header.Value);
                    }
                    response.Content = rewrapped;

                    // Truncated body text. Redaction of any sensitive value is applied
                    // by the runner's in-place ReportRedactor scrub over
                    // infrastructure.jsonl (ScrubRunFilesInPlace, on every run), the
                    // same as every other diagnostic that lands in that stream.
                    var text = System.Text.Encoding.UTF8.GetString(bytes);
                    respBody =
                        text.Length > RespBodyMaxChars
                            ? text.Substring(0, RespBodyMaxChars) + "…[truncated]"
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
