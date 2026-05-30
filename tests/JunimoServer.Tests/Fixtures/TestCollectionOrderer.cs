using System.Reflection;
using JunimoServer.Tests.Infrastructure;
using Xunit;
using Xunit.v3;

namespace JunimoServer.Tests.Fixtures;

/// <summary>
/// Orders test collections to minimize server churn. Scans TestBase subclasses
/// at static init time, groups them by server config key, and assigns priorities
/// so same-config classes run adjacently.
///
/// Priority layout:
///   0..N    Shared-config groups (SharedAssembly / SharedGroup), ordered by
///           class count descending (most-used config starts first)
///   80      SharedClass / PerTest / DeferAcquisition (run after shared tests)
///   N       Explicit [TestServer(Priority = N)] overrides auto-assignment
///   Explicit [CollectionPriority]: Honored for [CollectionDefinition] classes
/// </summary>
public class TestCollectionOrderer : ITestCollectionOrderer
{
    private const int PerTestPriority = 160;
    private const int UnknownPriority = 50;

    /// <summary>
    /// Keyed by full type name (e.g. "JunimoServer.Tests.CabinStrategyTests")
    /// for TestBase subclasses, and by collection definition name (e.g.
    /// "DownloadValidation") for explicit [CollectionDefinition] classes.
    /// </summary>
    private static readonly Dictionary<string, int> PriorityMap = BuildPriorityMap();

    private static void Log(string message) => TestLog.Test(message);

    private static Dictionary<string, int> BuildPriorityMap()
    {
        var map = new Dictionary<string, int>(StringComparer.Ordinal);
        var assembly = typeof(TestBase).Assembly;

        // Discover explicit [CollectionDefinition] classes with [CollectionPriority]
        foreach (var type in assembly.GetTypes())
        {
            var collectionDef = type.GetCustomAttribute<CollectionDefinitionAttribute>();
            var priority = type.GetCustomAttribute<CollectionPriorityAttribute>();
            if (collectionDef?.Name != null && priority != null)
            {
                map[collectionDef.Name] = priority.Priority;
            }
        }

        // Group TestBase subclasses by server config key
        // Key: server config key → Value: list of full type names
        var configGroups = new Dictionary<string, List<string>>(StringComparer.Ordinal);

        foreach (var type in assembly.GetTypes())
        {
            if (type.IsAbstract || !type.IsSubclassOf(typeof(TestBase)))
                continue;

            var attr = type.GetCustomAttribute<TestServerAttribute>() ?? new TestServerAttribute();
            var fullName = type.FullName!;

            // Explicit priority on [TestServer(Priority = N)] always wins
            if (attr.Priority >= 0)
            {
                map[fullName] = attr.Priority;
                continue;
            }

            // PerTest and DeferAcquisition classes run after all shared-config groups.
            if (attr.Isolation is IsolationMode.PerTest || attr.DeferAcquisition)
            {
                map[fullName] = PerTestPriority;
                continue;
            }

            // Compute server config key for grouping
            var requirements = ResourceRequirements.FromAttribute(attr, type.Name, testMethodName: null);
            var key = requirements.GetServerKey();

            if (!configGroups.TryGetValue(key, out var group))
            {
                group = new List<string>();
                configGroups[key] = group;
            }
            group.Add(fullName);
        }

        // Sort groups by class count descending; most-used config first
        var sortedGroups = configGroups
            .OrderByDescending(g => g.Value.Count)
            .ThenBy(g => g.Key, StringComparer.Ordinal)
            .ToList();

        // Assign priorities with 2x spacing to allow sub-sorting within groups.
        // Non-exclusive classes get the base priority; class-level exclusive classes
        // get base+1 so non-exclusive tests drain from the queue first (they're faster
        // and return capacity slots sooner, improving overall throughput).
        for (var groupIdx = 0; groupIdx < sortedGroups.Count; groupIdx++)
        {
            var basePriority = groupIdx * 2;

            foreach (var fullName in sortedGroups[groupIdx].Value)
            {
                var type = assembly.GetType(fullName);
                var classAttr = type?.GetCustomAttribute<TestServerAttribute>();
                var isClassExclusive = classAttr?._exclusive == true;
                map[fullName] = isClassExclusive ? basePriority + 1 : basePriority;
            }
        }

        // Diagnostic logging
        Log($"Priority map ({map.Count} entries):");
        foreach (var entry in map.OrderBy(e => e.Value).ThenBy(e => e.Key))
        {
            var shortName = entry.Key;
            var lastDot = shortName.LastIndexOf('.');
            if (lastDot >= 0) shortName = shortName[(lastDot + 1)..];
            Log($"  {entry.Value,3} -> {shortName}");
        }

        return map;
    }

    /// <summary>
    /// Returns the priority for a test class by its full type name.
    /// Used by <see cref="TestBase"/> to pass priority into the broker.
    /// </summary>
    public static int GetPriorityForClass(string? fullTypeName)
    {
        if (fullTypeName != null && PriorityMap.TryGetValue(fullTypeName, out var priority))
            return priority;
        return UnknownPriority;
    }

    private static int GetPriority(string? collectionName)
    {
        if (collectionName == null)
            return UnknownPriority;

        // Exact match (explicit collections like "DownloadValidation")
        if (PriorityMap.TryGetValue(collectionName, out var priority))
            return priority;

        // Implicit collections: xUnit uses a decorated display name that contains
        // the full type name. Check if any of our known type name keys appear
        // within the display name.
        foreach (var (key, p) in PriorityMap)
        {
            if (collectionName.Contains(key, StringComparison.Ordinal))
                return p;
        }

        return UnknownPriority;
    }

    IReadOnlyCollection<TTestCollection> ITestCollectionOrderer.OrderTestCollections<TTestCollection>(
        IReadOnlyCollection<TTestCollection> testCollections)
    {
        var ordered = testCollections
            .OrderBy(c => GetPriority((c as IXunitTestCollection)?.TestCollectionDisplayName))
            .ToList();

        Log($"Final ordering ({ordered.Count} collections):");
        foreach (var c in ordered)
        {
            var name = (c as IXunitTestCollection)?.TestCollectionDisplayName ?? "(null)";
            var p = GetPriority(name);
            var matched = p != UnknownPriority ? "" : " [UNMATCHED]";
            Log($"  {p,3} -> {name}{matched}");
        }

        return ordered;
    }
}
