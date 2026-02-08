using System.Text;

namespace JunimoServer.Tests.Helpers;

/// <summary>
/// A TextWriter wrapper that prepends timestamps to each line of output.
/// Used to automatically timestamp all console output (including Spectre.Console).
/// </summary>
public class TimestampingTextWriter : TextWriter
{
    private readonly TextWriter _inner;
    private readonly object _lock = new();
    private bool _atLineStart = true;

    public TimestampingTextWriter(TextWriter inner)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
    }

    public override Encoding Encoding => _inner.Encoding;

    public override void Write(char value)
    {
        lock (_lock)
        {
            WriteTimestampIfNeeded(value);
            _inner.Write(value);
            if (value == '\n') _atLineStart = true;
        }
    }

    public override void Write(string? value)
    {
        if (value == null) return;

        lock (_lock)
        {
            foreach (char c in value)
            {
                WriteTimestampIfNeeded(c);
                _inner.Write(c);
                if (c == '\n') _atLineStart = true;
            }
        }
    }

    public override void WriteLine(string? value)
    {
        lock (_lock)
        {
            if (value != null)
            {
                foreach (char c in value)
                {
                    WriteTimestampIfNeeded(c);
                    _inner.Write(c);
                    if (c == '\n') _atLineStart = true;
                }
            }
            WriteTimestampIfNeeded('\n');
            _inner.WriteLine();
            _atLineStart = true;
        }
    }

    public override void WriteLine()
    {
        lock (_lock)
        {
            WriteTimestampIfNeeded('\n');
            _inner.WriteLine();
            _atLineStart = true;
        }
    }

    public override void Flush() => _inner.Flush();

    private void WriteTimestampIfNeeded(char nextChar)
    {
        // Only prepend timestamp at the start of a real content line
        // Skip for newlines, carriage returns, and the zero-width space used for blank lines
        if (_atLineStart && nextChar != '\n' && nextChar != '\r' && nextChar != '\u200B')
        {
            _inner.Write($"{DateTime.Now:HH:mm:ss.fff} ");
            _atLineStart = false;
        }
    }
}
