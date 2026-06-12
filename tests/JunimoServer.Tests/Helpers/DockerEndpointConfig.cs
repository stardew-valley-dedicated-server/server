using System.Reflection;
using Docker.DotNet;
using Docker.DotNet.Handler.Abstractions;
using Docker.DotNet.NPipe;
using DotNet.Testcontainers.Configurations;

namespace JunimoServer.Tests.Helpers;

/// <summary>
/// Wraps a Docker endpoint auth and builds Docker.DotNet clients/builders with
/// two timeout overrides plus identifying headers:
/// - <c>NPipeTransportOptions.ConnectTimeout</c>: 5s (vs Docker.DotNet default 100ms),
///   applied only for named-pipe endpoints. Windows named-pipe accept queue saturates
///   under parallel container startup; the 100ms default fails individual API calls when
///   the daemon is busy.
/// - default timeout: <see cref="Timeout.InfiniteTimeSpan"/>. Caller-provided
///   CancellationTokens are the authoritative deadline (<c>StartupTimeout</c>,
///   <c>_errorCancellation</c>); the 100s HTTP default is a redundant lower bound.
///
/// One instance per Docker host. Local hosts get the Testcontainers-resolved auth
/// (Unix socket on Linux/macOS, named pipe on Windows). Remote hosts get a
/// <see cref="DockerEndpointAuthenticationConfiguration"/> built from a coordinator-side
/// <c>tcp://localhost:N</c> endpoint that <c>TunnelManager</c> forwards to the remote
/// daemon socket over SSH.
///
/// Wire via <c>builder.WithDockerEndpoint(host.EndpointConfig)</c> on every
/// ContainerBuilder/NetworkBuilder. For direct Docker.DotNet callers, use
/// <see cref="CreateDockerClient"/> off the per-host instance.
/// </summary>
public sealed class DockerEndpointConfig : IDockerEndpointAuthenticationConfiguration
{
    private static readonly TimeSpan NamedPipeConnectTimeout = TimeSpan.FromSeconds(
        ParseSeconds("SDVD_NAMED_PIPE_CONNECT_TIMEOUT_SEC", 5)
    );

    private static readonly string TestcontainersUserAgent =
        "tc-dotnet/"
        + (
            typeof(TestcontainersSettings)
                .Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()
                ?.InformationalVersion
            ?? typeof(TestcontainersSettings).Assembly.GetName().Version?.ToString()
            ?? "unknown"
        );

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
    /// Remote config — uses the coordinator-side <c>tcp://localhost:N</c> endpoint that
    /// <c>TunnelManager</c> opened via <c>ssh -L N:/var/run/docker.sock</c>. Docker.DotNet
    /// doesn't speak <c>ssh://</c>, so the daemon socket is forwarded over SSH and dialed
    /// as plain TCP. No daemon authentication is required over the loopback forward.
    /// </summary>
    public static DockerEndpointConfig CreateRemote(Uri endpoint) =>
        new(new DockerEndpointAuthenticationConfiguration(endpoint, NoopAuthProvider.Instance));

    public Uri Endpoint => _inner.Endpoint;

    public Version Version => _inner.Version;

    public IAuthProvider AuthProvider => _inner.AuthProvider;

    /// <summary>
    /// Builds the Docker.DotNet client builder Testcontainers uses for this endpoint,
    /// and the single source of truth for our client overrides. Applies the infinite
    /// default timeout and the two identifying headers unconditionally; applies the
    /// named-pipe connect-timeout override only for named-pipe endpoints, since
    /// <c>WithTransportOptions</c> selects the transport and must not force a pipe
    /// transport onto a TCP/Unix endpoint.
    /// </summary>
    public DockerClientBuilder GetDockerClientBuilder(Guid sessionId = default)
    {
        var builder = new DockerClientBuilder()
            .WithEndpoint(Endpoint)
            .WithAuthProvider(AuthProvider)
            .WithTimeout(Timeout.InfiniteTimeSpan)
            .WithHeader("User-Agent", TestcontainersUserAgent)
            .WithHeader("x-tc-sid", sessionId.ToString("D"));

        if (string.Equals(Endpoint.Scheme, "npipe", StringComparison.OrdinalIgnoreCase))
        {
            return builder.WithTransportOptions(
                new NPipeTransportOptions { ConnectTimeout = NamedPipeConnectTimeout }
            );
        }

        return builder;
    }

    /// <summary>
    /// Builds a Docker.DotNet client with the same overrides as the
    /// Testcontainers-routed clients. Use this for direct Docker.DotNet consumers
    /// (e.g. ContainerStatsCollector, EmergencyCleanup, ManagedServer) so they
    /// inherit the same fix.
    /// </summary>
    public DockerClient CreateDockerClient() => GetDockerClientBuilder().Build();

    private static int ParseSeconds(string envVar, int defaultValue)
    {
        var raw = Environment.GetEnvironmentVariable(envVar);
        return int.TryParse(raw, out var v) && v > 0 ? v : defaultValue;
    }
}
