using Exvs2Launcher.Core.Diagnostics;

namespace Exvs2Launcher.Core.Tests;

public class RedactorTests
{
    private const string Access = "12345678901234567890";
    private const string Chip = "0123456789ABCDEF0123456789ABCDEF";

    [Fact]
    public void Ini_masks_card_keys_and_keeps_other_lines()
    {
        var ini = "[card]\r\n; my card\r\naccessCode=" + Access + "\r\nchipId = \"" + Chip + "\"\r\n[config]\r\nserial=AB12\r\nServer=127.0.0.1:28080\r\n";
        var m = Redactor.MaskIni(ini);
        Assert.DoesNotContain(Access, m);
        Assert.DoesNotContain(Chip, m);
        Assert.Contains("; my card\r\n", m);
        Assert.Contains("serial=AB12", m);
        Assert.Contains("Server=127.0.0.1:28080", m);
        Assert.Contains("accessCode=***", m);
    }

    [Fact]
    public void Json_masks_tag_secret_boot_token_and_passwords_but_keeps_match_url()
    {
        var json = """
        { "CardServerConfig": { "OnlineMatching": { "MatchUrl": "http://mm.example.org:28090/match" },
          "OnlineTag": { "TagSecret": "super-secret-value" }, "BootToken": "tok123" },
          "Kestrel": { "Certificates": { "Default": { "Path": "Certificates/cert.pfx", "Password": "pfxpass" } } } }
        """;
        var m = Redactor.MaskJson(json);
        Assert.DoesNotContain("super-secret-value", m);
        Assert.DoesNotContain("tok123", m);
        Assert.DoesNotContain("pfxpass", m);
        Assert.Contains("http://mm.example.org:28090/match", m);
        Assert.Contains("cert.pfx", m);
    }

    [Fact]
    public void Text_masks_card_values_secrets_card_ids_public_ips_and_user_name()
    {
        var line = $"GET /ui/card/getBy/{Access} chip {Chip} CardId=42 TagSecret=abc \"accessCode\": \"{Access}\" from 8.8.8.8 lan 192.168.1.20 C:\\Users\\server\\Desktop";
        var m = Redactor.MaskText(line);
        Assert.DoesNotContain(Access, m);
        Assert.DoesNotContain(Chip, m);
        Assert.DoesNotContain("CardId=42", m);
        Assert.DoesNotContain("TagSecret=abc", m);
        Assert.DoesNotContain("8.8.8.8", m);
        Assert.Contains("192.168.1.20", m);
        Assert.DoesNotContain("\\server\\", m);
        Assert.Contains("%USERNAME%", m);
    }

    [Theory]
    [InlineData("10.0.0.1", false)]
    [InlineData("127.0.0.1", false)]
    [InlineData("172.16.5.5", false)]
    [InlineData("192.168.0.1", false)]
    [InlineData("100.64.1.1", false)]
    [InlineData("1.1.1.1", true)]
    [InlineData("203.0.113.9", true)]
    public void Public_ip_detection(string ip, bool isPublic) => Assert.Equal(isPublic, Redactor.IsPublicIpv4(ip));

    [Fact]
    public void Hex_access_code_is_masked_and_safe_keys_kept()
    {
        Assert.DoesNotContain("ABCDEF0123456789ABCD", Redactor.MaskText("code ABCDEF0123456789ABCD end"));
        Assert.False(Redactor.IsSensitiveKey("Serial"));
        Assert.False(Redactor.IsSensitiveKey("MatchUrl"));
        Assert.False(Redactor.IsSensitiveKey("CamIdx"));
        Assert.True(Redactor.IsSensitiveKey("TagSecret"));
        Assert.True(Redactor.IsSensitiveKey("N2N_Community"));
    }
}
