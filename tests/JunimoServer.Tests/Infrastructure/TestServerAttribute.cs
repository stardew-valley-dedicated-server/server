using System.Reflection;

namespace JunimoServer.Tests.Infrastructure;

/// <summary>
/// Declares server requirements on test classes/methods via a declarative attribute.
/// All properties are internally nullable for safe tri-state merge semantics.
/// </summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method, AllowMultiple = false)]
public class TestServerAttribute : Attribute
{
    // Backing fields: null means "not specified"
    private string? _password;
    private int? _farmType;
    private bool? _withSteam;
    private int? _startingCabins;
    private int? _maxPlayers;
    private int? _clients;
    private string? _cabinStrategy;
    private string? _existingCabinBehavior;
    private bool? _allowIpConnections;
    private IsolationMode? _isolation;
    private int? _priority;
    private bool? _keepConnected;
    internal bool? _exclusive;
    private bool? _artifacts;
    private bool? _fixtureFarmMod;

    // "Was this property explicitly set?" tracking for Password
    // because null is a meaningful value (no password)
    private bool _passwordSet;

    public string? Password
    {
        get => _password;
        set { _password = value; _passwordSet = true; }
    }

    /// <summary>
    /// Vanilla boot farm index (0-6). Attribute arguments can't hold a custom struct, so
    /// only vanilla indices are expressible here; a modded farm is selected at runtime via
    /// <c>CreateNewGameOnServerAsync("&lt;Id&gt;")</c> (POST /newgame), not at boot.
    /// </summary>
    public int FarmType { get => _farmType ?? 0; set => _farmType = value; }

    /// <summary>
    /// When true, the test's server loads the TestFarmMod fixture (adds a second
    /// Data/AdditionalFarms entry) so by-Id farm disambiguation can be exercised. This
    /// changes which mods load, so it is part of the server reuse key — at most one extra
    /// pooled server for the test class that sets it.
    /// </summary>
    public bool FixtureFarmMod { get => _fixtureFarmMod ?? false; set => _fixtureFarmMod = value; }

    public bool WithSteam { get => _withSteam ?? false; set => _withSteam = value; }
    public int StartingCabins { get => _startingCabins ?? Math.Max(4, HostPool.Instance.Hosts.Max(h => h.ClientCapacity.Capacity) * 3); set => _startingCabins = value; }
    public int MaxPlayers { get => _maxPlayers ?? Math.Max(10, StartingCabins + 1); set => _maxPlayers = value; }
    public int Clients { get => _clients ?? 1; set => _clients = value; }
    public string CabinStrategy { get => _cabinStrategy ?? "CabinStack"; set => _cabinStrategy = value; }
    public string ExistingCabinBehavior { get => _existingCabinBehavior ?? "KeepExisting"; set => _existingCabinBehavior = value; }
    public bool AllowIpConnections { get => _allowIpConnections ?? false; set => _allowIpConnections = value; }
    public IsolationMode Isolation { get => _isolation ?? IsolationMode.SharedClass; set => _isolation = value; }

    /// <summary>
    /// When true, the test acquires exclusive access to the shared server.
    /// Other tests wait until the exclusive test completes before they can acquire the server.
    /// Use for tests that require specific server state (e.g., no players connected).
    /// </summary>
    public bool Exclusive { get => _exclusive ?? false; set => _exclusive = value; }

    /// <summary>
    /// Explicit scheduling priority override. Lower values run first.
    /// When not set (-1), priority is auto-assigned by TestCollectionOrderer based on isolation mode.
    /// </summary>
    public int Priority { get => _priority ?? -1; set => _priority = value; }

    /// <summary>Named group for SharedGroup isolation.</summary>
    public string? SharedGroup { get; set; }

    /// <summary>
    /// When true, the test class uses persistent sessions: the client stays connected
    /// between tests, and TestBase manages the session lifecycle automatically.
    /// </summary>
    public bool KeepConnected
    {
        get => _keepConnected ?? false;
        set => _keepConnected = value;
    }

    /// <summary>
    /// When true, InitializeAsync skips resource acquisition.
    /// The test method must call AcquireServerAsync() with custom requirements.
    /// Used for Theory tests where server config depends on parameters.
    /// </summary>
    public bool DeferAcquisition { get; set; }

    /// <summary>
    /// Whether to collect test artifacts (screenshots, video clips) for this test.
    /// Defaults to true. Set to false for API-only tests where visual artifacts have
    /// no diagnostic value. Artifacts are always collected on test failure regardless.
    /// </summary>
    public bool Artifacts { get => _artifacts ?? true; set => _artifacts = value; }

    /// <summary>
    /// Merges this (class-level) attribute with a method-level attribute.
    /// Method-level wins only if its backing field was explicitly set.
    /// </summary>
    public TestServerAttribute MergeWith(TestServerAttribute? method)
    {
        if (method == null) return this;
        var merged = new TestServerAttribute();

        merged._farmType = method._farmType ?? _farmType;
        merged._clients = method._clients ?? _clients;
        merged._withSteam = method._withSteam ?? _withSteam;
        merged._startingCabins = method._startingCabins ?? _startingCabins;
        merged._maxPlayers = method._maxPlayers ?? _maxPlayers;
        merged._cabinStrategy = method._cabinStrategy ?? _cabinStrategy;
        merged._existingCabinBehavior = method._existingCabinBehavior ?? _existingCabinBehavior;
        merged._allowIpConnections = method._allowIpConnections ?? _allowIpConnections;
        merged._isolation = method._isolation ?? _isolation;
        merged._priority = method._priority ?? _priority;
        merged._keepConnected = method._keepConnected ?? _keepConnected;
        merged._exclusive = method._exclusive ?? _exclusive;
        merged._fixtureFarmMod = method._fixtureFarmMod ?? _fixtureFarmMod;
        merged.SharedGroup = method.SharedGroup ?? SharedGroup;

        // DeferAcquisition uses OR: if either says defer, we defer
        merged.DeferAcquisition = method.DeferAcquisition || DeferAcquisition;

        // Password: null is meaningful, so use explicit _passwordSet flag
        if (method._passwordSet)
        {
            merged._password = method._password;
            merged._passwordSet = true;
        }
        else if (_passwordSet)
        {
            merged._password = _password;
            merged._passwordSet = true;
        }

        merged._artifacts = method._artifacts ?? _artifacts;

        return merged;
    }

    /// <summary>
    /// Resolves the effective attribute for a test by merging class + method attributes.
    /// </summary>
    public static TestServerAttribute Resolve(Type testClass, string? methodName)
    {
        var classAttr = testClass.GetCustomAttribute<TestServerAttribute>() ?? new TestServerAttribute();

        if (methodName == null) return classAttr;

        var methodInfo = testClass.GetMethod(methodName,
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
        var methodAttr = methodInfo?.GetCustomAttribute<TestServerAttribute>();

        return classAttr.MergeWith(methodAttr);
    }
}
