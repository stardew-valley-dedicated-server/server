using System.Runtime.CompilerServices;
using JunimoServer.Tests.Infrastructure;
using Xunit;

namespace JunimoServer.Tests;

/// <summary>
/// Deterministic guard tests for <see cref="ManagedServer.ReleaseExclusive"/>'s ownership
/// token check.
///
/// The bug this guards: an Exclusive+KeepConnected test releases the exclusive gate from two
/// uncoordinated disposal sites (ResourceLease.DisposeAsync + PersistentSessionCoordinator.
/// ReleaseExclusiveGate). If the second call lands after the gate has since moved on — to a
/// *different* class, or to the *same* class's next method — an unguarded release would erase
/// the new holder's gate, letting non-exclusive tests join mid-exclusive (observed: a wedding
/// gate wiped, farmhand join disrupted, server poisoned). Only the token of the currently-
/// active acquisition may release; a stale token is a rejected no-op.
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
        SetField(server, "_exclusiveNextToken", 1L);

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
    public async Task ReleaseExclusive_WithForeignToken_DoesNotEraseGate()
    {
        var server = NewGateOnlyServer();

        var token = await server.AcquireExclusiveGateOnlyAsync(
            "JunimoServer.Tests.ClassA.MethodOne",
            CancellationToken.None
        );
        Assert.True(server.HasExclusiveGate);

        // A release that never acquired (token 0) and one guessing a wrong token must both
        // be rejected no-ops.
        server.ReleaseExclusive(0, "JunimoServer.Tests.ClassB.MethodTwo");
        server.ReleaseExclusive(token + 1, "JunimoServer.Tests.ClassB.MethodTwo");

        Assert.True(server.HasExclusiveGate);
        Assert.Equal("ClassA", server.ExclusiveOwnerClass);
    }

    [Fact]
    public async Task ReleaseExclusive_WithOwningToken_ReleasesGate()
    {
        var server = NewGateOnlyServer();

        var token = await server.AcquireExclusiveGateOnlyAsync(
            "JunimoServer.Tests.ClassA.MethodOne",
            CancellationToken.None
        );
        Assert.True(server.HasExclusiveGate);

        server.ReleaseExclusive(token, "JunimoServer.Tests.ClassA.MethodOne");

        Assert.False(server.HasExclusiveGate);
        Assert.Null(server.ExclusiveOwnerClass);
    }

    [Fact]
    public async Task ReleaseExclusive_StaleTokenFromSameClass_DoesNotEraseSuccessorGate()
    {
        var server = NewGateOnlyServer();

        // MethodOne acquires and releases; MethodTwo (same class) then holds the gate.
        var first = await server.AcquireExclusiveGateOnlyAsync(
            "JunimoServer.Tests.ClassA.MethodOne",
            CancellationToken.None
        );
        server.ReleaseExclusive(first, "JunimoServer.Tests.ClassA.MethodOne");
        var second = await server.AcquireExclusiveGateOnlyAsync(
            "JunimoServer.Tests.ClassA.MethodTwo",
            CancellationToken.None
        );
        Assert.True(server.HasExclusiveGate);

        // MethodOne's straggling second disposal call: same class, stale token. A
        // class-granular guard would honor it and erase MethodTwo's gate.
        server.ReleaseExclusive(first, "JunimoServer.Tests.ClassA.MethodOne");

        Assert.True(server.HasExclusiveGate);
        Assert.Equal("ClassA", server.ExclusiveOwnerClass);

        server.ReleaseExclusive(second, "JunimoServer.Tests.ClassA.MethodTwo");
        Assert.False(server.HasExclusiveGate);
    }
}
