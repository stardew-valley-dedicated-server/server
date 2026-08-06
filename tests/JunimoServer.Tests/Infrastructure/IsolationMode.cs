namespace JunimoServer.Tests.Infrastructure;

/// <summary>
/// Defines how servers are shared between tests.
/// </summary>
public enum IsolationMode
{
    /// <summary>
    /// Server shared across all tests in the same class. Default. Governs lifetime and reuse, NOT
    /// exclusivity: the key is <c>config-{hash}</c> with no class identity, so other classes with a
    /// matching config share the same instance (see <c>.claude/rules/test-broker-invariants.md</c>).
    /// </summary>
    SharedClass,

    /// <summary>Server shared across all classes with matching SharedGroup name.</summary>
    SharedGroup,

    /// <summary>Server shared across entire assembly (all tests with same config).</summary>
    SharedAssembly,

    /// <summary>Fresh server per test method. Most expensive, most isolated.</summary>
    PerTest,
}
