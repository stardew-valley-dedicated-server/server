using System.Collections.Concurrent;

namespace JunimoServer.TestRunner.Diagnostics;

/// <summary>
/// Reports the first occurrence of each distinct exception type, suppressing the rest.
/// Lets a transient blip stay visible for post-mortem without letting a sustained
/// failure flood the log.
///
/// <para>
/// The dedup key is exception type per (component, type) — different components
/// each get to report their own first failure of a given type. Pass the writer
/// callback so callers can target stderr (early bootstrap) or
/// <see cref="JunimoServer.Tests.Helpers.InfrastructureEventLog"/> as appropriate.
/// </para>
/// </summary>
public static class FirstFailureLog
{
    private static readonly ConcurrentDictionary<(string Component, Type ExceptionType), byte> _seen = new();

    /// <summary>
    /// Invokes <paramref name="writeLine"/> with a single formatted message the first
    /// time a given (component, exception type) pair is seen. Subsequent calls with the
    /// same pair are silent.
    /// </summary>
    public static void ReportOnce(string component, Exception ex, string context, Action<string> writeLine)
    {
        if (_seen.TryAdd((component, ex.GetType()), 0))
        {
            writeLine(
                $"[{component}] {context} failed ({ex.GetType().Name}: {ex.Message}). " +
                $"Further '{ex.GetType().Name}' failures will be silent.");
        }
    }
}
