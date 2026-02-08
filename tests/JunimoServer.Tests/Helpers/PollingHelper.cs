namespace JunimoServer.Tests.Helpers;

/// <summary>
/// Provides tight polling loops that replace fixed delays.
/// Polls a condition at short intervals, returning as soon as it's met.
/// Falls back to the timeout if the condition is never satisfied.
/// </summary>
public static class PollingHelper
{
    /// <summary>
    /// Polls until the condition returns true, or the timeout expires.
    /// Returns true if the condition was met, false if timed out.
    /// </summary>
    public static async Task<bool> WaitUntilAsync(
        Func<Task<bool>> condition,
        TimeSpan timeout,
        TimeSpan? pollInterval = null,
        CancellationToken cancellationToken = default)
    {
        var interval = pollInterval ?? TestTimings.FastPollInterval;
        var deadline = DateTime.UtcNow + timeout;

        while (DateTime.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                if (await condition())
                    return true;
            }
            catch
            {
                // Condition threw — treat as not-yet-ready, keep polling
            }

            await Task.Delay(interval, cancellationToken);
        }

        return false;
    }

    /// <summary>
    /// Polls until the async function returns a non-null result, or the timeout expires.
    /// Returns the result, or default if timed out.
    /// </summary>
    public static async Task<T?> WaitForResultAsync<T>(
        Func<Task<T?>> producer,
        TimeSpan timeout,
        TimeSpan? pollInterval = null,
        CancellationToken cancellationToken = default) where T : class
    {
        var interval = pollInterval ?? TestTimings.FastPollInterval;
        var deadline = DateTime.UtcNow + timeout;

        while (DateTime.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                var result = await producer();
                if (result != null)
                    return result;
            }
            catch
            {
                // Producer threw — treat as not-yet-ready, keep polling
            }

            await Task.Delay(interval, cancellationToken);
        }

        return default;
    }
}
