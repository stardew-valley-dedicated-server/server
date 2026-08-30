using System.Text;

namespace JunimoServer.Tests.Helpers;

/// <summary>
/// Duplicates every <see cref="Console.Error"/> write of this process into a
/// file under the run's diagnostics dir. The xUnit child installs it right after
/// <see cref="RunMetadata.BeginRun"/> so <c>TestLog</c> lines survive the run:
/// its stderr is otherwise only the inherited console, which the runner cannot
/// intercept (xunit's <c>AssemblyRunner</c> exposes no process-launcher hook).
/// </summary>
internal static class StderrTee
{
    private static readonly object InstallLock = new();
    private static StreamWriter? _file;

    public static void Install(string filePath)
    {
        lock (InstallLock)
        {
            if (_file is not null)
            {
                return;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(filePath)!);
            _file = new StreamWriter(
                new FileStream(filePath, FileMode.Append, FileAccess.Write, FileShare.Read)
            )
            {
                AutoFlush = true,
            };
            // Console.SetError wraps the writer in TextWriter.Synchronized.
            Console.SetError(new TeeWriter(Console.Error, _file));
        }
    }

    /// <summary>
    /// The file sink is diagnostics only: its first I/O failure drops it for the rest
    /// of the process, so a full disk or vanished run dir can't mask the error being logged.
    /// </summary>
    private sealed class TeeWriter(TextWriter console, TextWriter file) : TextWriter
    {
        private TextWriter? _file = file;

        public override Encoding Encoding => console.Encoding;

        public override void Write(char value)
        {
            console.Write(value);
            ToFile(f => f.Write(value));
        }

        public override void Write(string? value)
        {
            console.Write(value);
            ToFile(f => f.Write(value));
        }

        public override void WriteLine(string? value)
        {
            console.WriteLine(value);
            ToFile(f => f.WriteLine(value));
        }

        public override void Flush()
        {
            console.Flush();
            ToFile(f => f.Flush());
        }

        private void ToFile(Action<TextWriter> write)
        {
            var f = _file;
            if (f is null)
            {
                return;
            }

            try
            {
                write(f);
            }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException)
            {
                _file = null;
                console.WriteLine(
                    $"[stderr-tee] file sink disabled: {ex.GetType().Name}: {ex.Message}"
                );
            }
        }
    }
}
