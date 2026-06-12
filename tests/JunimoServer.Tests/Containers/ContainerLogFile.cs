using JunimoServer.Tests.Helpers;

namespace JunimoServer.Tests.Containers;

/// <summary>
/// Thread-safe sink for a single container's lifecycle log. Writes to
/// <c>{RunDir}/containers/{slug}/container.log</c> as lines arrive from the
/// streaming loop. The file is opened eagerly at construction; any I/O error
/// propagates so infrastructure failures fail the run loudly instead of
/// silently losing diagnostics.
/// </summary>
public sealed class ContainerLogFile : IAsyncDisposable
{
    private const int FlushEveryNLines = 50;

    private readonly object _lock = new();
    private readonly StreamWriter _writer;
    private int _linesSinceFlush;
    private bool _disposed;

    public ContainerLogFile(string containerSlug)
    {
        var path = Path.Combine(TestArtifacts.GetContainerDir(containerSlug), "container.log");
        _writer = new StreamWriter(path, append: false) { AutoFlush = false };
    }

    /// <summary>
    /// Appends one line. Thread-safe. No-op after dispose.
    /// </summary>
    public void WriteLine(string line)
    {
        lock (_lock)
        {
            if (_disposed)
            {
                return;
            }

            _writer.WriteLine(line);
            if (++_linesSinceFlush >= FlushEveryNLines)
            {
                _writer.Flush();
                _linesSinceFlush = 0;
            }
        }
    }

    public ValueTask DisposeAsync()
    {
        lock (_lock)
        {
            if (_disposed)
            {
                return ValueTask.CompletedTask;
            }

            _disposed = true;
            _writer.Flush();
            _writer.Dispose();
        }
        return ValueTask.CompletedTask;
    }
}
