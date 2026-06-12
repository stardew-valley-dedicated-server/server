namespace JunimoServer.Tests.Helpers;

/// <summary>
/// Process-wide registry of active test run IDs.
/// Stale cleanup skips Docker resources whose sdvd.run-id matches any registered ID.
/// </summary>
public static class TestRunRegistry
{
    private static readonly HashSet<string> ActiveRunIds = new();
    private static readonly object Lock = new();

    public static void Register(string runId)
    {
        lock (Lock)
            ActiveRunIds.Add(runId);
    }

    public static void Unregister(string runId)
    {
        lock (Lock)
            ActiveRunIds.Remove(runId);
    }

    public static bool IsActive(string runId)
    {
        lock (Lock)
            return ActiveRunIds.Contains(runId);
    }
}
