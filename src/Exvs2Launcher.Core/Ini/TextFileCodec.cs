using System.Text;

namespace Exvs2Launcher.Core.Ini;

/// <summary>
/// How a text file was stored on disk, so it can be written back the same way.
/// </summary>
public sealed record TextFormat(Encoding Encoding, bool HasBom, string NewLine, bool EndsWithNewLine)
{
    public static TextFormat Default { get; } = new(new UTF8Encoding(false), false, "\r\n", true);
}

/// <summary>
/// Reads and writes text while keeping the original encoding, byte-order mark and line endings.
/// Files that are not valid UTF-8 (for example a legacy code page) are read as Latin-1, which maps every
/// byte to one char, so untouched bytes round-trip exactly.
/// </summary>
public static class TextFileCodec
{
    private static readonly Encoding StrictUtf8 = new UTF8Encoding(false, throwOnInvalidBytes: true);

    public static (string Text, TextFormat Format) Decode(byte[] bytes)
    {
        Encoding enc;
        bool bom = false;
        int skip = 0;
        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
        {
            enc = new UTF8Encoding(false); bom = true; skip = 3;
        }
        else if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE)
        {
            enc = new UnicodeEncoding(false, false); bom = true; skip = 2;
        }
        else if (bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF)
        {
            enc = new UnicodeEncoding(true, false); bom = true; skip = 2;
        }
        else
        {
            try
            {
                StrictUtf8.GetString(bytes);
                enc = new UTF8Encoding(false);
            }
            catch (DecoderFallbackException)
            {
                enc = Encoding.Latin1;
            }
        }

        string text = enc.GetString(bytes, skip, bytes.Length - skip);
        string nl = text.Contains("\r\n", StringComparison.Ordinal) ? "\r\n"
                  : text.Contains('\n') ? "\n"
                  : "\r\n";
        bool endsWithNl = text.EndsWith('\n');
        return (text, new TextFormat(enc, bom, nl, endsWithNl));
    }

    public static byte[] Encode(string text, TextFormat format)
    {
        byte[] body = format.Encoding.GetBytes(text);
        if (!format.HasBom) return body;
        byte[] preamble = format.Encoding is UnicodeEncoding
            ? (format.Encoding.GetBytes("A")[0] == 0 ? new byte[] { 0xFE, 0xFF } : new byte[] { 0xFF, 0xFE })
            : new byte[] { 0xEF, 0xBB, 0xBF };
        var all = new byte[preamble.Length + body.Length];
        preamble.CopyTo(all, 0);
        body.CopyTo(all, preamble.Length);
        return all;
    }
}
