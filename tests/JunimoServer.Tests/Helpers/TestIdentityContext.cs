using System.Text;
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
                DisplayName: ToHeaderSafeId(displayName)
            );
        }
    }

    /// <summary>
    /// Canonical form of a test display name. One shared normalizer produces both the emitted
    /// <c>test.displayName</c> and the <c>X-Test-Id</c> header, so the two are identical and the
    /// <c>testId</c> to <c>test.displayName</c> join is reliable. The value must be printable ASCII
    /// because the header requires it (<c>HttpClient</c> cannot carry non-ASCII cleanly, and
    /// <c>HttpListener</c> returns 400 on a CR or LF), which a Theory argument can violate. Each
    /// non-ASCII character maps to its own hex code, so names that differ only in non-ASCII
    /// characters produce different output and two tests never merge into one attribution. Applied
    /// once at the single source above; callers use <see cref="TestIdentity.DisplayName"/> as-is.
    /// </summary>
    public static string ToHeaderSafeId(string value)
    {
        // Skip plain ASCII names, which every test currently has.
        if (!value.AsSpan().ContainsAnyExceptInRange(' ', '~'))
        {
            return value;
        }

        // Otherwise replace every non-ASCII character with its hex code so the result stays ASCII.
        var sb = new StringBuilder();
        foreach (var c in value)
        {
            if (c < ' ' || c > '~')
            {
                sb.Append("\\u").Append(((int)c).ToString("X4"));
            }
            else
            {
                sb.Append(c);
            }
        }

        return sb.ToString();
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
