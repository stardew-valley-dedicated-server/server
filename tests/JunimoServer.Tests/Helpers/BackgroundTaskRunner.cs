namespace JunimoServer.Tests.Helpers;

/// <summary>
/// Helpers for background tasks in test infrastructure. Standardizes the
/// <c>using (ExecutionContext.SuppressFlow()) Task.Run(...)</c> pattern that
/// long-lived emitters (stats loops, watchdogs, log-streamers, broker
/// prestart) need to avoid inheriting the test-context of whichever test
/// happened to first wake them.
///
/// <para>
/// Per <c>.claude/rules/asynclocal-pitfalls.md</c>:
/// <list type="bullet">
///   <item>Long-lived background tasks (outliving the calling test) must
///   suppress flow so they don't poison every later event with the first
///   waker's <c>TestContext.Current</c>.</item>
///   <item>Short-lived per-test work (helper called from the test body)
///   should NOT suppress flow — losing test identity there breaks per-test
///   attribution.</item>
/// </list>
/// </para>
/// </summary>
internal static class BackgroundTaskRunner
{
    /// <summary>
    /// Starts <paramref name="work"/> on a thread-pool task with execution
    /// context flow suppressed. Catches and emits faults via
    /// <c>Console.Error</c> (NOT through <see cref="InfrastructureEventLog"/>
    /// — recursion risk if the writer itself faulted). Returns the started
    /// <see cref="Task"/> so the caller can store / cancel / await it.
    /// </summary>
    public static Task RunLongLived(
        Func<CancellationToken, Task> work,
        string label,
        CancellationToken ct
    )
    {
        using (ExecutionContext.SuppressFlow())
        {
            return Task.Run(
                async () =>
                {
                    try
                    {
                        await work(ct);
                    }
                    catch (OperationCanceledException) when (ct.IsCancellationRequested)
                    { /* expected */
                    }
                    catch (Exception ex)
                    {
                        try
                        {
                            Console.Error.WriteLine(
                                $"[BackgroundTaskRunner:{label}] FAILED ({ex.GetType().Name}: {ex.Message})"
                            );
                        }
                        catch
                        { /* stderr unavailable */
                        }
                    }
                },
                ct
            );
        }
    }
}
