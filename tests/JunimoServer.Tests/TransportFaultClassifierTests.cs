using System.Net;
using System.Net.Sockets;
using JunimoServer.Tests.Clients;
using JunimoServer.Tests.Infrastructure;
using JunimoServer.Tests.Schema.Json;
using Xunit;

namespace JunimoServer.Tests;

/// <summary>
/// Pins the typed-signal tables of <see cref="TransportFaultClassifier"/>: every
/// <see cref="HttpRequestError"/> and <see cref="SocketError"/> value has a documented
/// verdict, <c>ResponseEnded</c> flips on the runner's owned-action window, and
/// <see cref="ForwardHealingHandler"/> retries only requests marked via
/// <see cref="RetrySafeRequest"/>. No containers; runs in-process like
/// <see cref="ExclusiveGateOwnershipTests"/>.
/// </summary>
public class TransportFaultClassifierTests
{
    private static readonly Func<bool> OutsideWindow = () => false;
    private static readonly Func<bool> InsideWindow = () => true;

    private static readonly HashSet<SocketError> ForwardScopedSocketErrors =
    [
        SocketError.ConnectionRefused,
        SocketError.ConnectionReset,
        SocketError.ConnectionAborted,
        SocketError.TimedOut,
    ];

    private static readonly HashSet<SocketError> HostScopedSocketErrors =
    [
        SocketError.HostUnreachable,
        SocketError.NetworkUnreachable,
        SocketError.NotConnected,
        SocketError.Shutdown,
    ];

    public static TheoryData<HttpRequestError, string> HttpErrorVerdicts =>
        new()
        {
            { HttpRequestError.Unknown, nameof(TransportFaultKind.Unclassified) },
            { HttpRequestError.NameResolutionError, nameof(TransportFaultKind.HostScoped) },
            { HttpRequestError.ConnectionError, nameof(TransportFaultKind.ForwardScoped) },
            { HttpRequestError.SecureConnectionError, nameof(TransportFaultKind.HostScoped) },
            { HttpRequestError.HttpProtocolError, nameof(TransportFaultKind.None) },
            { HttpRequestError.ExtendedConnectNotSupported, nameof(TransportFaultKind.None) },
            { HttpRequestError.VersionNegotiationError, nameof(TransportFaultKind.None) },
            { HttpRequestError.UserAuthenticationError, nameof(TransportFaultKind.None) },
            { HttpRequestError.ProxyTunnelError, nameof(TransportFaultKind.None) },
            { HttpRequestError.InvalidResponse, nameof(TransportFaultKind.None) },
            // Outside the owned-action window; the window case is tested separately.
            { HttpRequestError.ResponseEnded, nameof(TransportFaultKind.None) },
            { HttpRequestError.ConfigurationLimitExceeded, nameof(TransportFaultKind.None) },
        };

    [Fact]
    public void HttpErrorTable_CoversEveryHttpRequestError()
    {
        var covered = HttpErrorVerdicts.Select(row => row.Data.Item1).ToHashSet();
        Assert.Equal(Enum.GetValues<HttpRequestError>().ToHashSet(), covered);
    }

    [Theory]
    [MemberData(nameof(HttpErrorVerdicts))]
    public void HttpRequestException_ClassifiesByHttpRequestError(
        HttpRequestError error,
        string expectedKind
    )
    {
        var verdict = TransportFaultClassifier.Classify(
            new HttpRequestException(error, "message text is irrelevant"),
            OutsideWindow
        );

        Assert.Equal(Enum.Parse<TransportFaultKind>(expectedKind), verdict.Kind);
        AssertReasonShape(verdict);
    }

    [Theory]
    [MemberData(nameof(HttpErrorVerdicts))]
    public void HttpIOException_ClassifiesByHttpRequestError(
        HttpRequestError error,
        string expectedKind
    )
    {
        var verdict = TransportFaultClassifier.Classify(
            new HttpIOException(error, "message text is irrelevant"),
            OutsideWindow
        );

        Assert.Equal(Enum.Parse<TransportFaultKind>(expectedKind), verdict.Kind);
        AssertReasonShape(verdict);
    }

    public static TheoryData<SocketError> AllSocketErrors =>
        new(Enum.GetValues<SocketError>().Distinct());

    [Theory]
    [MemberData(nameof(AllSocketErrors))]
    public void SocketException_ClassifiesBySocketErrorCode(SocketError code)
    {
        var expected =
            ForwardScopedSocketErrors.Contains(code) ? TransportFaultKind.ForwardScoped
            : HostScopedSocketErrors.Contains(code) ? TransportFaultKind.HostScoped
            : TransportFaultKind.Unclassified;

        var verdict = TransportFaultClassifier.Classify(
            new SocketException((int)code),
            OutsideWindow
        );

        Assert.Equal(expected, verdict.Kind);
        AssertReasonShape(verdict);
        Assert.Contains(code.ToString(), verdict.Reason);
    }

    [Fact]
    public void ResponseEnded_IsForwardScopedOnlyInsideOwnedActionWindow()
    {
        var ended = new HttpIOException(
            HttpRequestError.ResponseEnded,
            "The response ended prematurely."
        );

        var inside = TransportFaultClassifier.Classify(ended, InsideWindow);
        var outside = TransportFaultClassifier.Classify(ended, OutsideWindow);

        Assert.Equal(TransportFaultKind.ForwardScoped, inside.Kind);
        Assert.Equal(TransportFaultKind.None, outside.Kind);
    }

    [Fact]
    public void WindowIsConsultedLazily_NotForOtherCodes()
    {
        var consulted = false;
        Func<bool> window = () => consulted = true;

        TransportFaultClassifier.Classify(
            new HttpRequestException(HttpRequestError.ConnectionError, "x"),
            window
        );

        Assert.False(consulted);
    }

    [Fact]
    public void EndOfStream_IsForwardScoped()
    {
        var verdict = TransportFaultClassifier.Classify(new EndOfStreamException(), OutsideWindow);
        Assert.Equal(TransportFaultKind.ForwardScoped, verdict.Kind);
    }

    [Fact]
    public void PlainIOException_IsUnclassified_NotGuessedFromMessage()
    {
        var verdict = TransportFaultClassifier.Classify(
            new IOException("Unable to write data to the transport connection: Broken pipe."),
            OutsideWindow
        );

        Assert.Equal(TransportFaultKind.Unclassified, verdict.Kind);
        Assert.Equal("unclassified: IOException", verdict.Reason);
        Assert.False(verdict.IsTransportFault);
        Assert.False(verdict.ForwardScoped);
    }

    [Fact]
    public void UnknownWrapper_DoesNotHideTypedInnerSignal()
    {
        var wrapped = new IOException(
            "wrapper",
            new SocketException((int)SocketError.ConnectionRefused)
        );

        var verdict = TransportFaultClassifier.Classify(wrapped, OutsideWindow);

        Assert.Equal(TransportFaultKind.ForwardScoped, verdict.Kind);
    }

    [Theory]
    [InlineData(typeof(TimeoutException))]
    [InlineData(typeof(InvalidOperationException))]
    [InlineData(typeof(Xunit.Sdk.XunitException))]
    public void ApplicationExceptions_AreNone(Type type)
    {
        var ex = (Exception)Activator.CreateInstance(type, "app-level")!;

        var verdict = TransportFaultClassifier.Classify(ex, OutsideWindow);

        Assert.Equal(TransportFaultVerdict.None, verdict);
    }

    [Fact]
    public void NullException_IsNone()
    {
        Assert.Equal(
            TransportFaultVerdict.None,
            TransportFaultClassifier.Classify(null, OutsideWindow)
        );
    }

    [Fact]
    public void TransportActionWindow_ContainsFollowsRunnerState()
    {
        var started = new DateTime(2026, 8, 25, 3, 54, 45, DateTimeKind.Utc);
        var ended = started.AddSeconds(30);
        var inProgress = NewState(started, windowEndUtc: null);
        var finished = NewState(started, windowEndUtc: ended);

        Assert.False(TransportActionWindow.Contains(null, started));
        Assert.False(TransportActionWindow.Contains(inProgress, started.AddSeconds(-1)));
        Assert.True(TransportActionWindow.Contains(inProgress, started.AddMinutes(10)));
        Assert.True(TransportActionWindow.Contains(finished, ended));
        Assert.False(TransportActionWindow.Contains(finished, ended.AddMilliseconds(1)));
    }

    [Fact]
    public async Task ForwardHealingHandler_RetriesOnlyMarkedRequests()
    {
        var unmarkedInner = new FaultingHandler(faultsBeforeSuccess: 1);
        using var unmarked = NewHealingClient(unmarkedInner);
        await Assert.ThrowsAsync<HttpRequestException>(() =>
            unmarked.SendAsync(
                new HttpRequestMessage(HttpMethod.Post, "/newgame"),
                CancellationToken.None
            )
        );
        Assert.Equal(1, unmarkedInner.Sends);

        // Two faults in a row: the second retry is sent from the CLONED request, so this
        // also proves the retry-safe mark survives CloneRequestAsync.
        var markedInner = new FaultingHandler(faultsBeforeSuccess: 2);
        using var marked = NewHealingClient(markedInner);
        var response = await marked.SendAsync(
            new HttpRequestMessage(HttpMethod.Get, "/status").Mark(),
            CancellationToken.None
        );

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(3, markedInner.Sends);
    }

    private static HttpMessageInvoker NewHealingClient(FaultingHandler inner) =>
        new(
            new ForwardHealingHandler(
                liveBaseUrl: () => "http://127.0.0.1:1",
                healAsync: _ => Task.FromResult(true),
                retryDelay: TimeSpan.Zero
            )
            {
                InnerHandler = inner,
            }
        );

    private sealed class FaultingHandler(int faultsBeforeSuccess) : HttpMessageHandler
    {
        public int Sends { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken
        )
        {
            Sends++;
            if (Sends <= faultsBeforeSuccess)
            {
                throw new HttpRequestException(
                    HttpRequestError.ConnectionError,
                    "forward dropped",
                    new SocketException((int)SocketError.ConnectionRefused)
                );
            }
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
        }
    }

    private static TransportState NewState(DateTime startedUtc, DateTime? windowEndUtc) =>
        new()
        {
            IncidentId = "host-a-20260825T035445000",
            HostId = "host-a",
            Cause = "datapath_wedged",
            Action = "master_respawn",
            Termination = "killed",
            ExitCode = 0,
            ExitStderr = "",
            KillOutcome = "killed",
            ActionStartedAtUtc = startedUtc,
            TerminatedAtUtc = startedUtc.AddSeconds(1),
            ActionEndedAtUtc = windowEndUtc,
            Outcome = windowEndUtc is null ? "in_progress" : "respawned",
            WindowEndUtc = windowEndUtc,
        };

    private static void AssertReasonShape(TransportFaultVerdict verdict)
    {
        if (verdict.Kind == TransportFaultKind.None)
        {
            Assert.Null(verdict.Reason);
            return;
        }

        Assert.NotNull(verdict.Reason);
        Assert.Equal(
            verdict.Kind == TransportFaultKind.Unclassified,
            verdict.Reason.StartsWith("unclassified: ", StringComparison.Ordinal)
        );
    }
}
