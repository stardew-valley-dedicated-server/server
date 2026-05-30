namespace JunimoServer.Tests.Infrastructure;

/// <summary>
/// Centralized logging with 3 clear prefixes: [Server], [Client], [Test].
/// All test infrastructure logging flows through here.
/// </summary>
internal static class TestLog
{
    internal static void Server(string message) =>
        Console.Error.WriteLine($"[Server] {DateTime.UtcNow:HH:mm:ss.fff} {message}");

    internal static void Client(string message) =>
        Console.Error.WriteLine($"[Client] {DateTime.UtcNow:HH:mm:ss.fff} {message}");

    internal static void Test(string message) =>
        Console.Error.WriteLine($"[Test]   {DateTime.UtcNow:HH:mm:ss.fff} {message}");

    /// <summary>
    /// Extracts the method name from a fully-qualified test name.
    /// "JunimoServer.Tests.LobbyCommandsTests.Rename_UpdatesLayoutName" → "Rename_UpdatesLayoutName"
    /// </summary>
    internal static string Short(string fullTestName) =>
        fullTestName[(fullTestName.LastIndexOf('.') + 1)..];
}
