using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using JunimoServer.Services.Diagnostics;

namespace JunimoServer.Services.Auth;

/// <summary>
/// Forwards the ambient <see cref="ModRequestContext.RequestId"/> and
/// <see cref="ModRequestContext.TestId"/> to the steam-auth sidecar as
/// <c>X-Request-Id</c> / <c>X-Test-Id</c> headers. When a request is made
/// outside any handler scope (null ambient ids), no header is added and the
/// sidecar treats the request as orphan.
/// </summary>
internal sealed class SteamAuthCorrelationHandler : DelegatingHandler
{
    private const string RequestIdHeader = "X-Request-Id";
    private const string TestIdHeader = "X-Test-Id";

    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken
    )
    {
        Forward(request, RequestIdHeader, ModRequestContext.RequestId);
        Forward(request, TestIdHeader, ModRequestContext.TestId);
        return base.SendAsync(request, cancellationToken);
    }

    private static void Forward(HttpRequestMessage request, string header, string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return;
        }

        request.Headers.Remove(header);
        request.Headers.TryAddWithoutValidation(header, value);
    }
}
