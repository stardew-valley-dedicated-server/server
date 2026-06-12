using System.Collections;
using System.Reflection;
using JunimoServer.Tests.Helpers;
using JunimoServer.Tests.Schema.Json;
using Xunit;

namespace JunimoServer.Tests.Infrastructure;

/// <summary>
/// Scans the test assembly for TestBase subclasses with [TestServer] attributes.
/// Returns deduplicated server demands grouped by config hash, used for pre-start.
/// </summary>
public static class ServerConfigDiscovery
{
    private static void Log(string msg) => TestLog.Test(msg);

    /// <summary>
    /// Discovers all unique server configurations needed by TestBase subclasses.
    /// Skips DeferAcquisition and PerTest classes. Returns demands ordered by
    /// class count descending (highest demand first).
    ///
    /// Each test method's effective key is computed by merging the class-level
    /// and method-level [TestServer] attributes (the same merge that
    /// <see cref="TestServerAttribute.Resolve"/> performs at runtime. This ensures
    /// _remainingDemand counts match the keys tests actually acquire at runtime.
    ///
    /// <para>
    /// <paramref name="skipValidation"/>: when true, skips ALL fail-fast validation
    /// (client capacity AND Steam-account count) -- workers in distributed mode
    /// trust the coordinator's pre-validated manifest and must not re-validate
    /// against their per-worker slice.
    /// </para>
    /// <para>
    /// <paramref name="keyFilter"/>: when non-null, restricts discovered demands to
    /// keys present in the filter set. Workers in distributed mode pass their
    /// assigned config keys so _remainingDemand counters reflect only the
    /// worker's portion of the suite.
    /// </para>
    /// <para>
    /// <paramref name="methodFilter"/>: when non-null and non-empty, mirrors the
    /// runner's `--filter` substring predicate applied per-method (class FullName
    /// contains the substring OR `{ClassFullName}.{MethodName}` contains it,
    /// case-insensitive). Non-matching methods don't contribute to any demand's
    /// counts, so multiple classes sharing a config key don't inflate
    /// <c>TestCount</c>/<c>NonExclusiveTestCount</c> beyond what xUnit will
    /// actually dispatch. Used by the local-runner prestart path; distributed
    /// workers leave it null and rely on <paramref name="keyFilter"/> for
    /// scoping.
    /// </para>
    /// </summary>
    public static List<ServerDemand> DiscoverRequiredConfigs(
        bool skipValidation = false,
        string[]? keyFilter = null,
        string? methodFilter = null
    )
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();

        var assembly = typeof(TestBase).Assembly;
        var demands = new Dictionary<string, ServerDemand>();

        foreach (var type in assembly.GetTypes())
        {
            if (type.IsAbstract || !type.IsSubclassOf(typeof(TestBase)))
                continue;

            var classAttr =
                type.GetCustomAttribute<TestServerAttribute>() ?? new TestServerAttribute();

            // Skip classes that defer acquisition (they create servers dynamically)
            if (classAttr.DeferAcquisition)
                continue;

            // Skip PerTest isolation (each test creates its own server)
            if (classAttr.Isolation == IsolationMode.PerTest)
                continue;

            // Resolve each method's effective (merged) attribute and group by key.
            // Methods with [TestServer] overrides that change server-affecting properties
            // (e.g., WithSteam, Password) produce a different key than the class default.
            var methodDemands = DiscoverMethodDemands(type, classAttr, methodFilter);
            if (methodDemands.Count == 0)
                continue;

            foreach (var md in methodDemands)
            {
                Log(
                    $"  {type.Name}: {md.Total} test case(s) ({md.Exclusive} exclusive) -> key={md.Key}"
                );

                if (demands.TryGetValue(md.Key, out var existing))
                {
                    if (!existing.ClassNames.Contains(type.Name))
                    {
                        existing.ClassCount++;
                        existing.ClassNames.Add(type.Name);
                    }
                    existing.TestCount += md.Total;
                    existing.ExclusiveTestCount += md.Exclusive;
                }
                else
                {
                    demands[md.Key] = new ServerDemand(md.Key, md.Requirements)
                    {
                        ClassCount = 1,
                        TestCount = md.Total,
                        ExclusiveTestCount = md.Exclusive,
                        ClassNames = { type.Name },
                    };
                }
            }
        }

        var result = demands.Values.OrderByDescending(d => d.ClassCount).ToList();

        // Worker mode: filter to assigned keys *before* validation and demand
        // counters are computed, so _remainingDemand on the worker reflects only
        // its assigned tests (not the global count).
        if (keyFilter is { Length: > 0 })
        {
            var allowed = new HashSet<string>(keyFilter);
            result = result.Where(d => allowed.Contains(d.Key)).ToList();
        }

        if (!skipValidation)
        {
            // Fail-fast: ensure no test requires more clients than the *largest* host
            // can serve. A test with `Clients=5` is allowed if any host has cap≥5; the
            // placement layer (HostPool.Place) ensures the test lands on a host that
            // can actually admit it. Per-host caps may differ across the fleet.
            var maxHostCap = HostPool.Instance.Hosts.Max(h => h.ClientCapacity.Capacity);
            foreach (var demand in result)
            {
                if (demand.Requirements.Clients > maxHostCap)
                    throw new InvalidOperationException(
                        $"Test '{demand.ClassNames.First()}' requires {demand.Requirements.Clients} client(s) "
                            + $"but the largest host's client capacity is {maxHostCap}."
                    );
            }

            // Fail-fast: tests with WithSteam=true need at least one Steam-capable
            // host (≥2 accounts in its slice — 1 server + ≥1 client). The slicer
            // is pure and runs again at broker pre-start with the same inputs,
            // producing the same slicing decisions there (single source of truth).
            var steamDemands = result.Where(d => d.Requirements.WithSteam).ToList();
            if (steamDemands.Count > 0)
            {
                var json = Environment.GetEnvironmentVariable("STEAM_ACCOUNTS");
                var slices = SteamAccountSlicer.Slice(json, HostPool.Instance.Hosts);
                if (!slices.Any(s => s.IsSteamCapable))
                {
                    var testNames = string.Join(
                        ", ",
                        steamDemands.SelectMany(d => d.ClassNames).Distinct()
                    );
                    var totalTests = steamDemands.Sum(d => d.TestCount);
                    var configuredAccounts = UserConfigJson.CountArrayTolerant(json);
                    var hostCount = HostPool.Instance.Hosts.Count;
                    throw new InvalidOperationException(
                        $"Requirements not satisfied: {totalTests} test(s) in [{testNames}] require "
                            + $"WithSteam=true, but no host's slice has ≥2 Steam accounts (1 server + ≥1 client). "
                            + $"Configured: {configuredAccounts} account(s) across {hostCount} host(s) — grow STEAM_ACCOUNTS or "
                            + $"reduce remote-host count. See docs/developers/testing/remote-host-setup.md."
                    );
                }

                // Diagnostic: when a Steam-capable host's slice has fewer client
                // accounts than its client-slot count, concurrent Steam tests on
                // that host serialize through ClientPool's lease-bound availability
                // gate (correct under the lease-binding model — never deadlocks —
                // but wall-time bloats vs. the slot count). Emit so operators can
                // see it in infrastructure.jsonl; do not throw, since growing
                // STEAM_ACCOUNTS to cover ClientSlots is often impractical.
                var slicesByHost = slices.ToDictionary(s => s.HostId, StringComparer.Ordinal);
                foreach (var host in HostPool.Instance.Hosts)
                {
                    if (!slicesByHost.TryGetValue(host.Id, out var slice))
                        continue;
                    if (!slice.IsSteamCapable)
                        continue;
                    if (slice.ClientPoolSize >= host.ClientSlots)
                        continue;
                    InfrastructureEventLog.Emit(
                        "steam_slice_undersized",
                        new
                        {
                            host_id = host.Id,
                            steamClientAccounts = slice.ClientPoolSize,
                            clientSlots = host.ClientSlots,
                        }
                    );
                }
            }
        }

        sw.Stop();

        // Called once by the assembly fixture, once by the broker's Task.Run —
        // latch so we don't emit the same event twice.
        if (Interlocked.CompareExchange(ref _discoveryEmitted, 1, 0) == 0)
        {
            InfrastructureEventLog.Emit(
                "config_discovery_completed",
                new
                {
                    configCount = result.Count,
                    configs = result
                        .Select(d => new
                        {
                            key = d.Key,
                            classCount = d.ClassCount,
                            testCount = d.TestCount,
                        })
                        .ToArray(),
                    durationMs = sw.ElapsedMilliseconds,
                }
            );
        }

        return result;
    }

    private static int _discoveryEmitted;

    /// <summary>
    /// Per-key demand from a single test class, computed by resolving the merged
    /// (class + method) attribute for each test method.
    /// </summary>
    private record MethodDemand(
        string Key,
        ResourceRequirements Requirements,
        int Total,
        int Exclusive
    );

    /// <summary>
    /// Resolves each test method's effective attribute (class + method merge) and
    /// groups test case counts by the resulting server key. This mirrors the merge
    /// that <see cref="TestBase.InitializeAsync"/> performs at runtime.
    ///
    /// <paramref name="methodFilter"/> mirrors the runner's `--filter` substring
    /// predicate: methods are kept only when the class FullName contains the
    /// substring OR the `{ClassFullName}.{MethodName}` display name does
    /// (case-insensitive). Null/empty means "no filter".
    /// </summary>
    private static List<MethodDemand> DiscoverMethodDemands(
        Type type,
        TestServerAttribute classAttr,
        string? methodFilter
    )
    {
        // Accumulate (total, exclusive) per key
        var perKey =
            new Dictionary<string, (ResourceRequirements reqs, int total, int exclusive)>();

        var classFull = type.FullName ?? type.Name;
        var classMatches =
            string.IsNullOrEmpty(methodFilter)
            || classFull.Contains(methodFilter, StringComparison.OrdinalIgnoreCase);

        foreach (var method in type.GetMethods(BindingFlags.Public | BindingFlags.Instance))
        {
            var attrs = method.GetCustomAttributes().ToList();
            var isFact = attrs.Any(a => a.GetType().Name == "FactAttribute");
            var isTheory = attrs.Any(a => a.GetType().Name == "TheoryAttribute");

            if (!isFact && !isTheory)
                continue;

            var skipValue =
                (
                    attrs.FirstOrDefault(a => a.GetType().Name == "FactAttribute") as FactAttribute
                )?.Skip
                ?? (
                    attrs.FirstOrDefault(a => a.GetType().Name == "TheoryAttribute")
                    as TheoryAttribute
                )?.Skip;
            if (!string.IsNullOrEmpty(skipValue))
                continue;

            if (!classMatches)
            {
                var displayName = $"{classFull}.{method.Name}";
                if (!displayName.Contains(methodFilter!, StringComparison.OrdinalIgnoreCase))
                    continue;
            }

            var cases = (isFact && !isTheory) ? 1 : CountTheoryDataRows(type, method, attrs);

            // Merge class + method attributes (same logic as TestServerAttribute.Resolve)
            var methodAttr = method.GetCustomAttribute<TestServerAttribute>();
            var merged = classAttr.MergeWith(methodAttr);
            var reqs = ResourceRequirements.FromAttribute(merged, type.Name, method.Name);
            var key = reqs.GetServerKey();

            var isExclusive = merged.Exclusive;

            if (perKey.TryGetValue(key, out var existing))
            {
                perKey[key] = (
                    existing.reqs,
                    existing.total + cases,
                    existing.exclusive + (isExclusive ? cases : 0)
                );
            }
            else
            {
                perKey[key] = (reqs, cases, isExclusive ? cases : 0);
            }
        }

        return perKey
            .Select(kv => new MethodDemand(
                kv.Key,
                kv.Value.reqs,
                kv.Value.total,
                kv.Value.exclusive
            ))
            .ToList();
    }

    /// <summary>
    /// Counts data rows for a Theory method by inspecting its data source attributes.
    /// </summary>
    private static int CountTheoryDataRows(Type type, MethodInfo method, List<Attribute> attrs)
    {
        var total = 0;

        // Count [InlineData] attributes directly
        total += attrs.Count(a => a is InlineDataAttribute);

        // Count [MemberData] by evaluating the referenced member
        foreach (var memberData in attrs.OfType<MemberDataAttribute>())
        {
            var memberType = memberData.MemberType ?? type;
            var memberName = memberData.MemberName;
            try
            {
                var rows = GetMemberDataCount(memberType, memberName);
                if (rows > 0)
                    total += rows;
                else
                    total += 1; // Fallback: count as at least 1
            }
            catch (Exception ex)
            {
                Log(
                    $"Could not evaluate MemberData '{memberName}' on {type.Name}.{method.Name}: {ex.Message}"
                );
                total += 1; // Fallback
            }
        }

        // Count [ClassData] by instantiating the data class
        foreach (var classData in attrs.OfType<ClassDataAttribute>())
        {
            try
            {
                if (Activator.CreateInstance(classData.Class) is IEnumerable enumerable)
                    total += enumerable.Cast<object>().Count();
                else
                    total += 1;
            }
            catch
            {
                total += 1; // Fallback
            }
        }

        // If no data source attributes found, count as 1
        return total > 0 ? total : 1;
    }

    /// <summary>
    /// Evaluates a MemberData source (property, field, or method) and returns row count.
    /// </summary>
    private static int GetMemberDataCount(Type type, string memberName)
    {
        // Try property
        var prop = type.GetProperty(
            memberName,
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static
        );
        if (prop?.GetValue(null) is IEnumerable propEnum)
            return propEnum.Cast<object>().Count();

        // Try method
        var methodInfo = type.GetMethod(
            memberName,
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static
        );
        if (methodInfo?.Invoke(null, null) is IEnumerable methodEnum)
            return methodEnum.Cast<object>().Count();

        // Try field
        var field = type.GetField(
            memberName,
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static
        );
        if (field?.GetValue(null) is IEnumerable fieldEnum)
            return fieldEnum.Cast<object>().Count();

        return 0;
    }
}

/// <summary>
/// Represents demand for a particular server configuration.
/// </summary>
public class ServerDemand
{
    public string Key { get; }
    public ResourceRequirements Requirements { get; }
    public int ClassCount { get; set; }
    public int TestCount { get; set; }
    public int ExclusiveTestCount { get; set; }
    public int NonExclusiveTestCount => TestCount - ExclusiveTestCount;
    public List<string> ClassNames { get; } = new();

    public ServerDemand(string key, ResourceRequirements requirements)
    {
        Key = key;
        Requirements = requirements;
    }
}
