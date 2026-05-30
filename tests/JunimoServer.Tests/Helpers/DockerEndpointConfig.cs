using System.Reflection;
using Docker.DotNet;
using DotNet.Testcontainers.Configurations;

namespace JunimoServer.Tests.Helpers;

/// <summary>
/// Wraps a Docker endpoint auth and overrides
/// <see cref="DockerClientConfiguration"/> timeouts:
/// - <c>namedPipeConnectTimeout</c>: 5s (vs Docker.DotNet default 100ms). Windows
///   named-pipe accept queue saturates under parallel container startup; the 100ms
///   default fails individual API calls when the daemon is busy.
/// - <c>defaultTimeout</c>: <see cref="Timeout.InfiniteTimeSpan"/>. Caller-provided
///   CancellationTokens are the authoritative deadline (<c>StartupTimeout</c>,
///   <c>_errorCancellation</c>); the 100s HTTP default is a redundant lower bound.
///
/// One instance per Docker host. Local hosts get the Testcontainers-resolved auth
/// (Unix socket on Linux/macOS, named pipe on Windows). Remote hosts get a
/// <see cref="DockerEndpointAuthenticationConfiguration"/> built from a
/// <c>ssh://user@machine</c> endpoint URI.
///
/// Wire via <c>builder.WithDockerEndpoint(host.EndpointConfig)</c> on every
/// ContainerBuilder/NetworkBuilder. For direct Docker.DotNet callers, use
/// <see cref="CreateDockerClient"/> off the per-host instance.
/// </summary>
public sealed class DockerEndpointConfig : IDockerEndpointAuthenticationConfiguration
{
    private static readonly TimeSpan NamedPipeConnectTimeout =
        TimeSpan.FromSeconds(ParseSeconds("SDVD_NAMED_PIPE_CONNECT_TIMEOUT_SEC", 5));

    private static readonly string TestcontainersUserAgent =
        "tc-dotnet/" + (typeof(TestcontainersSettings).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
            ?? typeof(TestcontainersSettings).Assembly.GetName().Version?.ToString()
            ?? "unknown");

    private readonly IDockerEndpointAuthenticationConfiguration _inner;

    private DockerEndpointConfig(IDockerEndpointAuthenticationConfiguration inner)
    {
        _inner = inner;
    }

    /// <summary>
    /// Default-local config — uses whatever Testcontainers resolved as the local
    /// daemon endpoint (named pipe on Windows, Unix socket otherwise). Used for
    /// host0 when no `ssh://` endpoint is configured.
    /// </summary>
    public static DockerEndpointConfig CreateLocal() =>
        new(TestcontainersSettings.OS.DockerEndpointAuthConfig);

    /// <summary>
    /// Convenience handle on the local-default config. Survives until <c>HostPool</c>
    /// owns per-host instances; callsites should migrate to <c>host.EndpointConfig</c>
    /// when the host pool is in place.
    /// </summary>
    public static readonly DockerEndpointConfig Instance = CreateLocal();

    /// <summary>
    /// Remote config — uses a `ssh://user@machine` endpoint. Docker.DotNet's
    /// transport handles the SSH dial. Local-host timeout overrides remain
    /// harmless for SSH transports.
    /// </summary>
    public static DockerEndpointConfig CreateRemote(Uri sshEndpoint) =>
        new(new DockerEndpointAuthenticationConfiguration(sshEndpoint));

    public Uri Endpoint => _inner.Endpoint;

    public Credentials Credentials => _inner.Credentials;

    public DockerClientConfiguration GetDockerClientConfiguration(Guid sessionId = default)
    {
        var headers = new Dictionary<string, string>
        {
            ["User-Agent"] = TestcontainersUserAgent,
            ["x-tc-sid"] = sessionId.ToString("D"),
        };

        return new DockerClientConfiguration(
            endpoint: Endpoint,
            credentials: Credentials,
            defaultTimeout: Timeout.InfiniteTimeSpan,
            namedPipeConnectTimeout: NamedPipeConnectTimeout,
            defaultHttpRequestHeaders: headers);
    }

    /// <summary>
    /// Builds a Docker.DotNet client with the same timeout overrides as the
    /// Testcontainers-routed clients. Use this for direct Docker.DotNet consumers
    /// (e.g. ContainerStatsCollector, EmergencyCleanup, ManagedServer) so they
    /// inherit the same fix.
    /// </summary>
    public DockerClient CreateDockerClient() =>
        GetDockerClientConfiguration().CreateClient();

    private static int ParseSeconds(string envVar, int defaultValue)
    {
        var raw = Environment.GetEnvironmentVariable(envVar);
        return int.TryParse(raw, out var v) && v > 0 ? v : defaultValue;
    }
}

/// <summary>
/// Minimal endpoint config for an arbitrary Docker.DotNet endpoint URI (e.g.
/// <c>ssh://user@host</c>). Testcontainers' built-in auth resolver assumes the
/// local daemon, so for remote hosts we synthesize the auth config directly.
/// </summary>
internal sealed class DockerEndpointAuthenticationConfiguration : IDockerEndpointAuthenticationConfiguration
{
    public DockerEndpointAuthenticationConfiguration(Uri endpoint)
    {
        Endpoint = endpoint;
    }

    public Uri Endpoint { get; }

    public Credentials Credentials => new AnonymousCredentials();

    public DockerClientConfiguration GetDockerClientConfiguration(Guid sessionId = default) =>
        new(endpoint: Endpoint, credentials: Credentials);
}
