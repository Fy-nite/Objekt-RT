using System.IO;
using System.Text;

namespace ObjectRT.Dap;

/// <summary>
/// TextWriter that forwards written lines to the debug console as DAP output
/// events. Partial writes are buffered; a line is emitted on newline or when
/// the writer is flushed.
/// </summary>
public sealed class DapEventTextWriter : TextWriter
{
    private readonly string _category;
    private readonly DapOutputHandler _handler;
    private readonly StringBuilder _pending = new();

    public DapEventTextWriter(string category, DapOutputHandler handler)
    {
        _category = category;
        _handler = handler;
    }

    public override Encoding Encoding => Encoding.UTF8;

    public override void Write(char value)
    {
        lock (_pending)
        {
            if (value == '\n') EmitPending();
            else if (value != '\r') _pending.Append(value);
        }
    }

    public override void Write(string? value)
    {
        if (string.IsNullOrEmpty(value)) return;
        lock (_pending) _pending.Append(value);
    }

    public override void WriteLine()
    {
        lock (_pending) EmitPending();
    }

    public override void WriteLine(string? value)
    {
        lock (_pending)
        {
            _pending.Append(value);
            EmitPending();
        }
    }

    public override void Flush()
    {
        lock (_pending) EmitPending();
    }

    private void EmitPending()
    {
        if (_pending.Length == 0) return;
        var text = _pending.ToString();
        _pending.Clear();
        _handler(_category, text + "\n");
    }
}
