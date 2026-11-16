using System.Text;
using Exvs2Launcher.Core.Ini;

namespace Exvs2Launcher.Core.Tests;

public class IniDocumentTests
{
    // Shape copied from a real config.ini (values are fake).
    private const string ConfigIni =
        "[config]\r\n# Game mode, Client or LM.\r\nmode =Client\r\nserial =AbCd\r\n\r\n" +
        "# Card server address\r\nServer =127.0.0.1:28080\r\n\r\nRegion =1\r\n# IpAddress=10.0.0.1\r\n" +
        "BootToken=\r\nIpAddress=192.168.1.10\r\n\r\n[controller]\r\n\r\nEnabled =False\r\n\r\n\r\n\r\n" +
        "ArcadeButton1 = 4\r\n\r\n[display]\r\nResolution=1080p\r\n";

    [Fact]
    public void Roundtrip_is_byte_identical()
    {
        var bytes = Encoding.UTF8.GetBytes(ConfigIni);
        Assert.Equal(bytes, IniDocument.FromBytes(bytes).ToBytes());
    }

    [Fact]
    public void Roundtrip_without_trailing_newline_and_lf_only()
    {
        var bytes = Encoding.UTF8.GetBytes("[a]\nx=1\n; c\ny = 2");
        Assert.Equal(bytes, IniDocument.FromBytes(bytes).ToBytes());
    }

    [Fact]
    public void Set_keeps_key_spelling_spacing_and_other_lines()
    {
        var doc = IniDocument.FromBytes(Encoding.UTF8.GetBytes(ConfigIni));
        Assert.True(doc.Set("CONFIG", "Serial", "Wx7k"));
        Assert.True(doc.Set("controller", "arcadebutton1", "8"));
        var text = doc.ToText();
        Assert.Contains("\r\nserial =Wx7k\r\n", text);
        Assert.Contains("\r\nArcadeButton1 = 8\r\n", text);
        Assert.Equal(ConfigIni.Replace("serial =AbCd", "serial =Wx7k").Replace("ArcadeButton1 = 4", "ArcadeButton1 = 8"), text);
    }

    [Fact]
    public void Commented_key_is_not_matched()
    {
        var doc = IniDocument.FromBytes(Encoding.UTF8.GetBytes(ConfigIni));
        Assert.Equal("192.168.1.10", doc.Get("config", "IpAddress"));
        doc.Set("config", "IpAddress", "192.168.1.69");
        Assert.Contains("# IpAddress=10.0.0.1", doc.ToText());
        Assert.Contains("\r\nIpAddress=192.168.1.69\r\n", doc.ToText());
    }

    [Fact]
    public void Set_same_value_reports_no_change()
    {
        var doc = IniDocument.FromBytes(Encoding.UTF8.GetBytes(ConfigIni));
        Assert.False(doc.Set("config", "serial", "AbCd"));
    }

    [Fact]
    public void Missing_key_goes_to_end_of_its_section_before_blank_lines()
    {
        var doc = IniDocument.Parse("[a]\r\nx=1\r\n\r\n[b]\r\ny=2\r\n", new TextFormat(new UTF8Encoding(false), false, "\r\n", true));
        doc.Set("a", "z", "3");
        Assert.Equal("[a]\r\nx=1\r\nz=3\r\n\r\n[b]\r\ny=2\r\n", doc.ToText());
    }

    [Fact]
    public void Missing_section_is_appended()
    {
        var doc = IniDocument.Parse("[a]\nx=1\n", new TextFormat(new UTF8Encoding(false), false, "\n", true));
        doc.Set("card", "accessCode", "1");
        Assert.Equal("[a]\nx=1\n[card]\naccessCode=1\n", doc.ToText());
    }

    [Fact]
    public void Empty_value_is_read_and_set()
    {
        var doc = IniDocument.FromBytes(Encoding.UTF8.GetBytes(ConfigIni));
        Assert.Equal("", doc.Get("config", "BootToken"));
        Assert.Null(doc.Get("config", "Nope"));
        Assert.Null(doc.Get("nosection", "serial"));
    }

    [Fact]
    public void Utf8_bom_is_preserved()
    {
        var bytes = new byte[] { 0xEF, 0xBB, 0xBF }.Concat(Encoding.UTF8.GetBytes("[c]\r\nname=テスト\r\nk=1\r\n")).ToArray();
        var doc = IniDocument.FromBytes(bytes);
        Assert.True(doc.Format.HasBom);
        doc.Set("c", "k", "2");
        var outBytes = doc.ToBytes();
        Assert.Equal(new byte[] { 0xEF, 0xBB, 0xBF }, outBytes[..3]);
        Assert.Equal("[c]\r\nname=テスト\r\nk=2\r\n", Encoding.UTF8.GetString(outBytes, 3, outBytes.Length - 3));
    }

    [Fact]
    public void Utf16_le_bom_is_preserved()
    {
        var enc = new UnicodeEncoding(false, false);
        var bytes = new byte[] { 0xFF, 0xFE }.Concat(enc.GetBytes("[c]\r\nk=1\r\n")).ToArray();
        var doc = IniDocument.FromBytes(bytes);
        Assert.Equal(bytes, doc.ToBytes());
        doc.Set("c", "k", "2");
        Assert.Equal(new byte[] { 0xFF, 0xFE }.Concat(enc.GetBytes("[c]\r\nk=2\r\n")).ToArray(), doc.ToBytes());
    }

    [Fact]
    public void Non_utf8_bytes_survive_unchanged()
    {
        // GBK bytes for a comment; not valid UTF-8.
        var head = Encoding.ASCII.GetBytes("[c]\r\n# ");
        var gbk = new byte[] { 0xD6, 0xD0, 0xCE, 0xC4 };
        var tail = Encoding.ASCII.GetBytes("\r\nk=1\r\n");
        var bytes = head.Concat(gbk).Concat(tail).ToArray();
        var doc = IniDocument.FromBytes(bytes);
        Assert.Equal(bytes, doc.ToBytes());
        doc.Set("c", "k", "2");
        var expected = head.Concat(gbk).Concat(Encoding.ASCII.GetBytes("\r\nk=2\r\n")).ToArray();
        Assert.Equal(expected, doc.ToBytes());
    }

    [Fact]
    public void Values_with_line_breaks_are_rejected()
    {
        var doc = IniDocument.Parse("[a]\nx=1\n");
        Assert.Throws<ArgumentException>(() => doc.Set("a", "x", "1\nevil=2"));
    }

    [Fact]
    public void Save_and_load_file()
    {
        var path = Path.Combine(Path.GetTempPath(), "ini-" + Guid.NewGuid() + ".ini");
        try
        {
            File.WriteAllBytes(path, Encoding.UTF8.GetBytes(ConfigIni));
            var doc = IniDocument.Load(path);
            doc.Set("display", "Resolution", "720p");
            doc.Save(path);
            Assert.Equal(ConfigIni.Replace("1080p", "720p"), File.ReadAllText(path));
            Assert.Single(Directory.GetFiles(Path.GetTempPath(), Path.GetFileName(path) + "*"));
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Remove_deletes_only_the_named_key()
    {
        var doc = IniDocument.Parse("[keyboard]\r\nEnabled=True\r\nUp=W\r\nService=F2\r\nKill=F4\r\n", new TextFormat(new UTF8Encoding(false), false, "\r\n", true));
        Assert.True(doc.Remove("keyboard", "Service"));
        Assert.False(doc.Remove("keyboard", "Service"));
        Assert.False(doc.Remove("keyboard", "Nope"));
        Assert.Null(doc.Get("keyboard", "Service"));
        Assert.Equal("W", doc.Get("keyboard", "Up"));
        Assert.Equal("[keyboard]\r\nEnabled=True\r\nUp=W\r\nKill=F4\r\n", doc.ToText());
    }
}
