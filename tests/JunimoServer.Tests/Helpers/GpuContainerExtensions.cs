using Docker.DotNet.Models;
using DotNet.Testcontainers.Builders;

namespace JunimoServer.Tests.Helpers;

/// <summary>
/// Extension methods for adding NVIDIA GPU passthrough to Testcontainers builders.
/// Enables NVENC hardware video encoding when the placed host advertises a GPU.
/// </summary>
public static class GpuContainerExtensions
{
    /// <summary>
    /// Adds NVIDIA GPU device requests and environment variables to the container builder
    /// when <paramref name="host"/> advertises GPU support
    /// (<see cref="Infrastructure.DockerHost.HasGpu"/>). No-op otherwise. Per-host so a
    /// fleet mixing GPU-equipped (workstation) and CPU-only (cheap VPS) machines can
    /// run side-by-side: the workstation's containers get NVENC, the VPS's fall back
    /// to libx264.
    /// </summary>
    public static ContainerBuilder WithGpuIfEnabled(
        this ContainerBuilder builder,
        Infrastructure.DockerHost host
    )
    {
        if (!host.HasGpu)
            return builder;

        return builder
            .WithEnvironment("NVIDIA_VISIBLE_DEVICES", "all")
            .WithEnvironment("NVIDIA_DRIVER_CAPABILITIES", "all")
            .WithCreateParameterModifier(p =>
            {
                // Request all available GPUs, equivalent to `docker run --gpus all`
                p.HostConfig ??= new HostConfig();
                p.HostConfig.DeviceRequests ??= new List<DeviceRequest>();
                p.HostConfig.DeviceRequests.Add(
                    new DeviceRequest
                    {
                        Count = -1,
                        Capabilities = new List<IList<string>> { new List<string> { "gpu" } },
                    }
                );
            });
    }
}
