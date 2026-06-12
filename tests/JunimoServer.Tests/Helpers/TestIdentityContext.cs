using Xunit;

namespace JunimoServer.Tests.Helpers;

/// <summary>
/// Identity of an executing test. Serialized to the <c>test</c> field of
/// the structured-event envelope.
/// </summary>
public sealed record TestIdentity(string Class, string Method, string DisplayName);

/// <summary>
/// Reads the current test identity from xUnit's ambient
/// <see cref="TestContext.Current"/>. Orthogonal to
/// <see cref="CorrelationContext"/>: <c>requestId</c> joins events across
/// services for one HTTP call; <c>test.*</c> attributes events to the
/// test that caused them.
///
/// <para>
/// Identity flows from xUnit's <see cref="TestContext.Current"/>, populated
/// across all <c>IAsyncLifetime</c> phases and the test method body.
/// </para>
/// </summary>
public static class TestIdentityContext
{
    /// <summary>
    /// Current test identity, or null when no test owns the ambient context
    /// (background tasks, pre-start, broker shutdown).
    /// </summary>
    public static TestIdentity? Current
    {
        get
        {
            var ctx = TestContext.Current;
            var testClass = ctx?.TestClass;
            var testMethod = ctx?.TestMethod;
            var displayName = ctx?.Test?.TestDisplayName;
            if (testClass == null || testMethod == null || string.IsNullOrEmpty(displayName))
            {
                return null;
            }

            return new TestIdentity(
                Class: testClass.TestClassSimpleName,
                Method: testMethod.MethodName,
                DisplayName: displayName
            );
        }
    }

    /// <summary>
    /// Ambient lifecycle phase (e.g. "setup", "connect", "artifacts", "cleanup")
    /// for the currently-executing test. Read by <see cref="InfrastructureEventLog.Emit"/>
    /// to decorate every envelope with a <c>phase</c> field. Null when no
    /// <see cref="PushPhase"/> scope is active.
    ///
    /// <para>
    /// Backed by <see cref="AsyncLocal{T}"/>. Flows through <c>await</c> but
    /// not across external pump boundaries (game-loop queue, SteamKit callbacks);
    /// see <c>.claude/rules/asynclocal-pitfalls.md</c>. If a boundary is later
    /// added (game-loop queue, SteamKit callback), capture+rebind at enqueue-time
    /// is required to flow context across it.
    /// </para>
    /// </summary>
    public static string? Phase => _phase.Value;

    private static readonly AsyncLocal<string?> _phase = new();

    /// <summary>
    /// Pushes <paramref name="phase"/> onto the ambient phase stack for the
    /// duration of the returned scope. Disposing the scope restores the prior
    /// value, supporting nested phases (e.g. an inner <c>connect</c> inside
    /// an outer <c>setup</c>).
    /// </summary>
    public static IDisposable PushPhase(string phase)
    {
        var prior = _phase.Value;
        _phase.Value = phase;
        return new PhaseScope(prior);
    }

    private sealed class PhaseScope : IDisposable
    {
        private readonly string? _prior;
        private bool _disposed;

        public PhaseScope(string? prior)
        {
            _prior = prior;
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _phase.Value = _prior;
        }
    }
}
