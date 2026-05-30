using System.Runtime.CompilerServices;
using DotNet.Testcontainers.Configurations;

namespace JunimoServer.Tests.Helpers;

internal static class ModuleInit
{
    [ModuleInitializer]
    internal static void Init()
    {
        TestEnvLoader.EnsureLoaded();

        // Disable Testcontainers' Ryuk reaper. Cleanup is handled in-tree by
        // EmergencyCleanup (Register/Unregister + SweepAtStartup, keyed on the
        // sdvd.test=true label that every container, network, and volume
        // carries). This is structural, not a workaround:
        //
        //   - Ryuk dials its container's mapped port from the test process.
        //     For ssh:// Docker endpoints, DockerContainer.Hostname throws
        //     "endpoint not supported"; the dial loops 30x for 60s and fails
        //     with ResourceReaperException("Initialization has been cancelled").
        //     See Testcontainers .NET 4.3.0 DockerContainer.cs:148-176.
        //   - Multi-host runs need one reaper per Docker daemon. Testcontainers'
        //     reaper is a process-global singleton; per-host instantiation
        //     requires private API. Our HostPool is multi-host by design.
        //
        // Trade-off: the in-tree sweep runs at the next test start, not within
        // seconds of test-process death. If a test run is killed with -9 / OOM /
        // BSOD, leaked containers persist on each host until the next `make test`
        // sweeps them by label. Acceptable for the current single-user / VPS
        // workflow; closing the kill-9 gap would mean a homemade per-host
        // watchdog routed through TunnelManager.
        TestcontainersSettings.ResourceReaperEnabled = false;
    }
}
