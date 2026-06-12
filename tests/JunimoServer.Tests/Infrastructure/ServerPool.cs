namespace JunimoServer.Tests.Infrastructure;

/// <summary>
/// Thread-safe pool of <see cref="ManagedServer"/> instances keyed by server configuration.
/// Supports multiple instances per key for parallel test execution.
/// All public methods are thread-safe; the internal lock is held only for O(1)
/// list scans (no I/O under lock).
/// </summary>
internal sealed class ServerPool
{
    private readonly object _lock = new();
    private readonly Dictionary<string, List<ManagedServer>> _instances = new();

    /// <summary>
    /// Adds a server instance to the pool for the given key.
    /// </summary>
    public void Add(string key, ManagedServer server)
    {
        lock (_lock)
        {
            if (!_instances.TryGetValue(key, out var list))
            {
                list = new List<ManagedServer>();
                _instances[key] = list;
            }
            list.Add(server);
        }
    }

    /// <summary>
    /// Returns the best available instance for a non-exclusive test: healthy, non-gated,
    /// lowest RefCount. Falls back to gated instances if all are gated. When
    /// <paramref name="hostId"/> is non-null, restricts the search to instances on
    /// that host so a test pinned to host X can't reuse a server on host Y (clients
    /// can't migrate networks). Returns null if no healthy initialized instances exist.
    /// </summary>
    public ManagedServer? TryGetBest(string key, string? hostId = null)
    {
        lock (_lock)
        {
            if (!_instances.TryGetValue(key, out var list))
            {
                return null;
            }

            ManagedServer? bestNonGated = null;
            ManagedServer? bestGated = null;

            foreach (var server in list)
            {
                if (server.IsPoisoned || !server.IsInitialized)
                {
                    continue;
                }

                if (hostId != null && server.Host.Id != hostId)
                {
                    continue;
                }

                if (!server.HasExclusiveGate)
                {
                    if (bestNonGated == null || server.RefCount < bestNonGated.RefCount)
                    {
                        bestNonGated = server;
                    }
                }
                else
                {
                    if (bestGated == null || server.RefCount < bestGated.RefCount)
                    {
                        bestGated = server;
                    }
                }
            }

            return bestNonGated ?? bestGated;
        }
    }

    /// <summary>
    /// Returns the best instance for an exclusive test. First looks for an instance
    /// where the caller's class already holds the exclusive gate (same-class routing),
    /// then falls back to <see cref="TryGetBest"/>. When <paramref name="hostId"/> is
    /// non-null, restricts both passes to instances on that host.
    /// </summary>
    public ManagedServer? TryGetBestForExclusive(
        string key,
        string? callerClass,
        string? hostId = null
    )
    {
        lock (_lock)
        {
            if (callerClass != null && _instances.TryGetValue(key, out var list))
            {
                foreach (var server in list)
                {
                    if (server.IsPoisoned || !server.IsInitialized)
                    {
                        continue;
                    }

                    if (hostId != null && server.Host.Id != hostId)
                    {
                        continue;
                    }

                    if (server.HasExclusiveGate && server.ExclusiveOwnerClass == callerClass)
                    {
                        return server;
                    }
                }
            }
        }

        // No same-class gate found; pick the least-loaded instance on the host
        return TryGetBest(key, hostId);
    }

    /// <summary>
    /// Like <see cref="TryGetBest"/>, but atomically stakes a reservation on the
    /// chosen instance via <see cref="ManagedServer.ReserveForAcquire"/> under
    /// <c>_lock</c>. Ordering uses <c>RefCount + Reservations</c> so concurrent
    /// reservers see climbing load and fan out across siblings instead of
    /// converging on the same all-zeros snapshot. The caller MUST either
    /// commit the reservation via <c>AddRef(consumeReservation: true)</c> or
    /// release it via <see cref="ManagedServer.ReleaseReservation"/>.
    /// </summary>
    public ManagedServer? TryReserveBest(string key, string? hostId = null)
    {
        lock (_lock)
        {
            if (!_instances.TryGetValue(key, out var list))
            {
                return null;
            }

            ManagedServer? bestNonGated = null;
            ManagedServer? bestGated = null;
            var bestNonGatedLoad = 0;
            var bestGatedLoad = 0;

            foreach (var server in list)
            {
                if (server.IsPoisoned || !server.IsInitialized)
                {
                    continue;
                }

                if (hostId != null && server.Host.Id != hostId)
                {
                    continue;
                }

                var load = server.RefCount + server.Reservations;
                if (!server.HasExclusiveGate)
                {
                    if (bestNonGated == null || load < bestNonGatedLoad)
                    {
                        bestNonGated = server;
                        bestNonGatedLoad = load;
                    }
                }
                else
                {
                    if (bestGated == null || load < bestGatedLoad)
                    {
                        bestGated = server;
                        bestGatedLoad = load;
                    }
                }
            }

            var chosen = bestNonGated ?? bestGated;
            chosen?.ReserveForAcquire();
            return chosen;
        }
    }

    /// <summary>
    /// Reserving variant of <see cref="TryGetBestForExclusive"/>. Same-class
    /// gate match wins; otherwise falls back to <see cref="TryReserveBest"/>.
    /// In both cases the returned instance has a reservation outstanding that
    /// the caller must commit or release.
    /// </summary>
    public ManagedServer? TryReserveBestForExclusive(
        string key,
        string? callerClass,
        string? hostId = null
    )
    {
        lock (_lock)
        {
            if (callerClass != null && _instances.TryGetValue(key, out var list))
            {
                foreach (var server in list)
                {
                    if (server.IsPoisoned || !server.IsInitialized)
                    {
                        continue;
                    }

                    if (hostId != null && server.Host.Id != hostId)
                    {
                        continue;
                    }

                    if (server.HasExclusiveGate && server.ExclusiveOwnerClass == callerClass)
                    {
                        server.ReserveForAcquire();
                        return server;
                    }
                }
            }
        }

        return TryReserveBest(key, hostId);
    }

    /// <summary>
    /// Returns true if the specific instance is still in the pool for the given key.
    /// </summary>
    public bool Contains(string key, ManagedServer server)
    {
        lock (_lock)
        {
            return _instances.TryGetValue(key, out var list) && list.Contains(server);
        }
    }

    /// <summary>
    /// Removes a specific instance from the pool. Returns true if removed.
    /// </summary>
    public bool TryRemove(string key, ManagedServer server)
    {
        lock (_lock)
        {
            if (!_instances.TryGetValue(key, out var list))
            {
                return false;
            }

            var removed = list.Remove(server);
            if (list.Count == 0)
            {
                _instances.Remove(key);
            }

            return removed;
        }
    }

    /// <summary>
    /// Returns true if at least one non-poisoned, initialized instance exists for the key.
    /// </summary>
    public bool HasHealthy(string key)
    {
        lock (_lock)
        {
            if (!_instances.TryGetValue(key, out var list))
            {
                return false;
            }

            foreach (var server in list)
            {
                if (!server.IsPoisoned && server.IsInitialized)
                {
                    return true;
                }
            }
            return false;
        }
    }

    /// <summary>
    /// Returns a snapshot of all instances for a key. Safe to iterate while mutating the pool.
    /// Returns empty list if key not found.
    /// </summary>
    public IReadOnlyList<ManagedServer> GetAll(string key)
    {
        lock (_lock)
        {
            if (!_instances.TryGetValue(key, out var list))
            {
                return Array.Empty<ManagedServer>();
            }

            return list.ToList();
        }
    }

    /// <summary>
    /// Returns a snapshot of all (key, server) pairs across all keys.
    /// Safe to iterate while mutating the pool.
    /// </summary>
    public IReadOnlyList<(string Key, ManagedServer Server)> GetAll()
    {
        lock (_lock)
        {
            var result = new List<(string, ManagedServer)>();
            foreach (var (key, list) in _instances)
            {
                foreach (var server in list)
                {
                    result.Add((key, server));
                }
            }
            return result;
        }
    }

    /// <summary>
    /// All keys currently in the pool.
    /// </summary>
    public IEnumerable<string> Keys
    {
        get
        {
            lock (_lock)
            {
                return _instances.Keys.ToList();
            }
        }
    }

    /// <summary>
    /// Removes all instances from the pool.
    /// </summary>
    public void Clear()
    {
        lock (_lock)
        {
            _instances.Clear();
        }
    }
}
