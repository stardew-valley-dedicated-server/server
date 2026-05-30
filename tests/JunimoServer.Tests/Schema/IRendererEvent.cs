namespace JunimoServer.Tests.Schema;

/// <summary>
/// Base interface for all renderer events. Carries the local emit timestamp.
/// Records that implement this interface flow producer → IPC pipe → dispatcher
/// → renderer with their wire-format JSON layout fixed by [JsonPropertyName]
/// attributes (default policy is camelCase + WhenWritingNull).
/// </summary>
public interface IRendererEvent
{
    DateTime Timestamp { get; }
}
