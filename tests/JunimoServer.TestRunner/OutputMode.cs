namespace JunimoServer.TestRunner;

/// <summary>
/// Output rendering mode for the test runner.
/// </summary>
public enum OutputMode
{
    /// <summary>
    /// Streaming line-by-line output for CI environments.
    /// Includes timestamps, GitHub Actions annotations.
    /// Log level: Info+
    /// </summary>
    CI,

    /// <summary>
    /// Structured JSONL output for LLM/AI agent consumption.
    /// Failures and summary only - minimal noise.
    /// </summary>
    LLM,

    /// <summary>
    /// Web-based UI served via embedded Kestrel with WebSocket push.
    /// Live test tree, inline screenshots, and static report generation.
    /// </summary>
    Web,
}

/// <summary>
/// Utility methods for output mode detection.
/// </summary>
public static class OutputModeDetector
{
    /// <summary>
    /// Determine output mode from command-line arguments with auto-fallback.
    /// </summary>
    public static OutputMode Detect(string[] args)
    {
        if (args.Contains("--llm", StringComparer.OrdinalIgnoreCase))
        {
            return OutputMode.LLM;
        }

        if (args.Contains("--web", StringComparer.OrdinalIgnoreCase))
        {
            return OutputMode.Web;
        }

        return OutputMode.CI;
    }

    /// <summary>
    /// Check if --report flag is present. Generates a static HTML report after tests finish.
    /// </summary>
    public static bool ShouldGenerateReport(string[] args)
    {
        return args.Contains("--report", StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Check if --verbose flag is present in args.
    /// Verbose mode shows detailed setup steps and diagnostic output inline.
    /// </summary>
    public static bool IsVerbose(string[] args)
    {
        return args.Contains("--verbose", StringComparer.OrdinalIgnoreCase);
    }
}
