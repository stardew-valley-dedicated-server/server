namespace JunimoServer.Tests.Infrastructure;

/// <summary>
/// Defines how servers are shared between tests.
/// </summary>
public enum IsolationMode
{
    /// <summary>Server shared across all tests in the same class. Default.</summary>
    SharedClass,
    /// <summary>Server shared across all classes with matching SharedGroup name.</summary>
    SharedGroup,
    /// <summary>Server shared across entire assembly (all tests with same config).</summary>
    SharedAssembly,
    /// <summary>Fresh server per test method. Most expensive, most isolated.</summary>
    PerTest
}
