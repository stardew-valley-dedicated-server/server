namespace SteamService;

/// <summary>
/// Logger with elapsed time tracking between log calls.
/// </summary>
public static class Logger
{
    private static DateTime _startTime = DateTime.Now;
    private static DateTime _lastLogTime = DateTime.Now;

    public static void Log(string message)
    {
        var now = DateTime.Now;
        var elapsed = now - _lastLogTime;
        _lastLogTime = now;

        Console.Write(message);
        Console.ForegroundColor = ConsoleColor.Blue;
        Console.WriteLine($" +{elapsed.TotalSeconds:F1}s");
        Console.ResetColor();
    }

    public static void Reset()
    {
        _startTime = DateTime.Now;
        _lastLogTime = DateTime.Now;
    }

    public static void LogTotal(string prefix = "[Steam] Total time:")
    {
        var total = DateTime.Now - _startTime;
        Console.Write(prefix);
        Console.ForegroundColor = ConsoleColor.Blue;
        Console.WriteLine($" {total.TotalSeconds:F1}s");
        Console.ResetColor();
    }
}
