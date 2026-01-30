namespace JunimoServer.Tests.Helpers;

/// <summary>
/// Manages the test output directory for artifacts like screenshots.
///
/// Output structure:
///   TestResults/
///     Screenshots/
///       {TestClass}/
///         {TestMethod}/
///           01_label.png
///           02_label.png
/// </summary>
public static class TestArtifacts
{
    private static readonly string RepoRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
    public static string OutputDir { get; } = Path.Combine(RepoRoot, "TestResults");
    public static string ScreenshotsDir { get; } = Path.Combine(OutputDir, "Screenshots");

    /// <summary>
    /// Cleans and recreates the screenshots directory.
    /// Call once at the start of a test run (e.g. from the shared fixture).
    /// </summary>
    public static void InitializeScreenshotsDir()
    {
        if (Directory.Exists(ScreenshotsDir))
        {
            Directory.Delete(ScreenshotsDir, recursive: true);
        }
        Directory.CreateDirectory(ScreenshotsDir);
    }

    /// <summary>
    /// Returns (and creates) the directory for a specific test's screenshots.
    /// </summary>
    public static string GetScreenshotDir(string testClass, string testMethod)
    {
        var dir = Path.Combine(ScreenshotsDir, testClass, testMethod);
        Directory.CreateDirectory(dir);
        return dir;
    }
}
