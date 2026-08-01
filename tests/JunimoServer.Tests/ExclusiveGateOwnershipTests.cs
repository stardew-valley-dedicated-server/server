using System.Runtime.CompilerServices;
using JunimoServer.Tests.Infrastructure;
using Xunit;

namespace JunimoServer.Tests;

/// <summary>
/// Deterministic guard tests for <see cref="ManagedServer.ReleaseExclusive"/>'s owner check.
///
/// The bug this guards: an Exclusive+KeepConnected test releases the exclusive gate from two
/// uncoordinated disposal sites (ResourceLease.DisposeAsync + PersistentSessionCoordinator.
/// ReleaseExclusiveGate). If the second call lands after a *different* class has since claimed
/// the gate, an unguarded release would erase that class's gate, letting non-exclusive tests
/// join mid-exclusive (observed: a wedding gate wiped, farmhand join disrupted, server poisoned).
///
/// ManagedServer needs a live Docker container to construct, but the gate logic is pure in-memory
/// field manipulation. These tests build an uninitialized instance and set only the fields the
/// gate methods touch — no Docker — so the ownership invariant is verified deterministically
/// rather than relying on a ~1ms production race to recur.
/// </summary>
public class ExclusiveGateOwnershipTests
{
    private static ManagedServer NewGateOnlyServer()
    {
        // Skip the Docker-dependent constructor; initialize only the fields the exclusive-gate
        // methods read/write. If ManagedServer's gate internals are renamed, this throws loudly.
        var server = (ManagedServer)RuntimeHelpers.GetUninitializedObject(typeof(ManagedServer));

        SetField(server, "<Key>k__BackingField", "config-test");
        SetField(server, "<InstanceId>k__BackingField", "server-config-test-0");
        SetField(server, "_displayLabel", "gate-test");
        SetField(server, "_exclusiveLock", new object());
        SetField(server, "_exclusiveClassTurn", new SemaphoreSlim(0));

        return server;
    }

    private static void SetField(object target, string name, object value)
    {
        var field =
            typeof(ManagedServer).GetField(
                name,
                System.Reflection.BindingFlags.Instance
                    | System.Reflection.BindingFlags.NonPublic
                    | System.Reflection.BindingFlags.Public
            ) ?? throw new InvalidOperationException($"ManagedServer field '{name}' not found");
        field.SetValue(target, value);
    }

    [Fact]
    public async Task ReleaseExclusive_FromDifferentClass_DoesNotEraseGate()
    {
        var server = NewGateOnlyServer();

        await server.AcquireExclusiveGateOnlyAsync(
            "JunimoServer.Tests.ClassA.MethodOne",
            CancellationToken.None
        );
        Assert.True(server.HasExclusiveGate);

        // A stale release from a sibling class (the double-release second call) must be a no-op.
        server.ReleaseExclusive("JunimoServer.Tests.ClassB.MethodTwo");

        Assert.True(server.HasExclusiveGate);
        Assert.Equal("ClassA", server.ExclusiveOwnerClass);
    }

    [Fact]
    public async Task ReleaseExclusive_FromOwningClass_ReleasesGate()
    {
        var server = NewGateOnlyServer();

        await server.AcquireExclusiveGateOnlyAsync(
            "JunimoServer.Tests.ClassA.MethodOne",
            CancellationToken.None
        );
        Assert.True(server.HasExclusiveGate);

        // The owner (same class, any method) releases normally.
        server.ReleaseExclusive("JunimoServer.Tests.ClassA.MethodTwo");

        Assert.False(server.HasExclusiveGate);
        Assert.Null(server.ExclusiveOwnerClass);
    }
}
