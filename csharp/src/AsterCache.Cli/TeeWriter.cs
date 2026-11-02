using System.Text;

namespace AsterCache.Cli;

/// 두 곳에 같이 쓰는 TextWriter (--result-file).
internal sealed class TeeWriter(TextWriter first, TextWriter second) : TextWriter
{
    public override Encoding Encoding => first.Encoding;

    public override void Write(char value)
    {
        first.Write(value);
        second.Write(value);
    }

    public override void Write(string? value)
    {
        first.Write(value);
        second.Write(value);
    }

    public override void Flush()
    {
        first.Flush();
        second.Flush();
    }
}
