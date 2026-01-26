using Spectre.Console;
using Xunit.Abstractions;

namespace JunimoServer.Tests.Helpers;

/// <summary>
/// Shared logging utility for test output.
/// Provides consistent, color-coded logging with optional dual output to ITestOutputHelper.
/// </summary>
public class TestLogger
{
    private readonly ITestOutputHelper? _output;
    private readonly bool _dualOutput;
    private readonly string _prefix;

    // Configuration from environment
    private static readonly bool UseIcons = !string.Equals(
        Environment.GetEnvironmentVariable("SDVD_TEST_ICONS"), "false", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Returns true if verbose logging is enabled via SDVD_TEST_VERBOSE environment variable.
    /// </summary>
    public static bool VerboseLogging => string.Equals(
        Environment.GetEnvironmentVariable("SDVD_TEST_VERBOSE"), "true", StringComparison.OrdinalIgnoreCase);

    // Unicode status icons with fallbacks
    private static readonly string IconSuccess = UseIcons ? "✓" : "[OK]";
    private static readonly string IconError = UseIcons ? "✗" : "[ERROR]";
    private static readonly string IconWarning = UseIcons ? "!" : "[WARN]";
    private static readonly string IconDetail = UseIcons ? "→" : "->";

    /// <summary>
    /// Creates a new TestLogger instance.
    /// </summary>
    /// <param name="prefix">Prefix for log messages (e.g., "[Test]", "[Setup]").</param>
    /// <param name="output">Optional xUnit output helper for test artifact capture.</param>
    /// <param name="dualOutput">If true, writes to both AnsiConsole and ITestOutputHelper.</param>
    public TestLogger(string prefix = "[Test]", ITestOutputHelper? output = null, bool dualOutput = false)
    {
        _prefix = prefix;
        _output = output;
        _dualOutput = dualOutput;
    }

    /// <summary>
    /// Log a standard message.
    /// </summary>
    public void Log(string message)
    {
        if (_dualOutput && _output != null)
            _output.WriteLine($"{_prefix} {message}");

        AnsiConsole.MarkupLine($"{Markup.Escape(_prefix)} {Markup.Escape(message)}");
    }

    /// <summary>
    /// Log a success message with green icon.
    /// </summary>
    public void LogSuccess(string message)
    {
        if (_dualOutput && _output != null)
            _output.WriteLine($"{_prefix} {IconSuccess} {message}");

        AnsiConsole.MarkupLine($"{Markup.Escape(_prefix)} [green]{IconSuccess} {Markup.Escape(message)}[/]");
    }

    /// <summary>
    /// Log a warning message with yellow icon.
    /// </summary>
    public void LogWarning(string message)
    {
        if (_dualOutput && _output != null)
            _output.WriteLine($"{_prefix} {IconWarning} {message}");

        AnsiConsole.MarkupLine($"{Markup.Escape(_prefix)} [yellow]{IconWarning} {Markup.Escape(message)}[/]");
    }

    /// <summary>
    /// Log an error message with red icon.
    /// </summary>
    public void LogError(string message)
    {
        if (_dualOutput && _output != null)
            _output.WriteLine($"{_prefix} {IconError} {message}");

        AnsiConsole.MarkupLine($"{Markup.Escape(_prefix)} [red]{IconError} {Markup.Escape(message)}[/]");
    }

    /// <summary>
    /// Log a detail message with grey icon (for debug/diagnostic info).
    /// </summary>
    public void LogDetail(string message)
    {
        if (_dualOutput && _output != null)
            _output.WriteLine($"{_prefix} {IconDetail} {message}");

        AnsiConsole.MarkupLine($"{Markup.Escape(_prefix)} [grey]{IconDetail} {Markup.Escape(message)}[/]");
    }

    /// <summary>
    /// Log a section header in bold.
    /// </summary>
    public void LogSection(string title)
    {
        if (_dualOutput && _output != null)
            _output.WriteLine($"{_prefix} {title}");

        AnsiConsole.MarkupLine($"{Markup.Escape(_prefix)} [bold]{Markup.Escape(title)}[/]");
    }
}
