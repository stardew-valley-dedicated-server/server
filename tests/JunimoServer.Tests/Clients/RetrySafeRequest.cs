namespace JunimoServer.Tests.Clients;

/// <summary>
/// Per-request declaration that a transport retry may re-send it. <see cref="ForwardHealingHandler"/>
/// retries only marked requests: a forward can drop after the server has fully processed a
/// request, so an unmarked retry would run a non-idempotent action twice (a second
/// <c>POST /newgame</c> while the first load is in flight). Every GET is marked at its call
/// site; a mutating request is marked only where the server handler makes a repeat harmless.
/// </summary>
internal static class RetrySafeRequest
{
    private static readonly HttpRequestOptionsKey<bool> Key = new("JunimoServer.Tests.RetrySafe");

    public static HttpRequestMessage Mark(this HttpRequestMessage request)
    {
        request.Options.Set(Key, true);
        return request;
    }

    public static bool IsMarked(HttpRequestMessage request) =>
        request.Options.TryGetValue(Key, out var safe) && safe;
}
