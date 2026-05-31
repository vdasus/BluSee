using System.Text;

namespace BluSee.Diagnostics;

/// <summary>Writes every console character to both the original console and a log file.</summary>
public sealed class TeeTextWriter(TextWriter console, TextWriter file) : TextWriter
{
    public override Encoding Encoding => console.Encoding;

    public override void Write(char value)
    {
        console.Write(value);
        file.Write(value);
    }

    public override void Write(string? value)
    {
        console.Write(value);
        file.Write(value);
    }

    public override void Flush()
    {
        console.Flush();
        file.Flush();
    }
}
