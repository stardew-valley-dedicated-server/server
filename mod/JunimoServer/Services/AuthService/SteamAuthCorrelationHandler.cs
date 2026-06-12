using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using JunimoServer.Services.Diagnostics;

namespace JunimoServer.Services.Auth
{
    /// <summary>
    /// Forwards the ambient <see cref="ModRequestContext.RequestId"/> to the
    /// steam-auth sidecar as an <c>X-Request-Id</c> header. When a request
    /// is made outside any handler scope (null ambient id), no header is
    /// added and the sidecar treats the request as orphan.
    /// </summary>
    internal sealed class SteamAuthCorrelationHandler : DelegatingHandler
    {
        private const string HeaderName = "X-Request-Id";

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken
        )
        {
            var requestId = ModRequestContext.RequestId;
            if (!string.IsNullOrEmpty(requestId))
            {
                request.Headers.Remove(HeaderName);
                request.Headers.Add(HeaderName, requestId);
            }
            return base.SendAsync(request, cancellationToken);
        }
    }
}
