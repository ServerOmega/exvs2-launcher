using Exvs2Launcher.Core.Identity;

namespace Exvs2Launcher.Core.Tests;

public class IdentityTests
{
    [Fact]
    public void Serial_has_four_safe_characters()
    {
        var gen = new IdentityGenerator();
        for (int i = 0; i < 500; i++)
        {
            var s = gen.NewSerial();
            Assert.True(IdentityFormat.IsValidSerial(s), s);
            Assert.All(s, c => Assert.Contains(c, IdentityGenerator.SerialAlphabet));
            Assert.DoesNotContain('0', s); Assert.DoesNotContain('O', s); Assert.DoesNotContain('l', s);
        }
    }

    [Fact]
    public void Serial_avoids_taken_values_case_insensitively()
    {
        // Deterministic RNG: first draws spell "AAAA", then "aaaa", then "BBBB".
        var seq = new Queue<int>(new[] { 0, 0, 0, 0, 24, 24, 24, 24, 1, 1, 1, 1 });
        var gen = new IdentityGenerator(_ => seq.Dequeue());
        Assert.Equal("BBBB", gen.NewSerial(new[] { "aaaa" }));
    }

    [Fact]
    public void Card_has_20_digits_and_32_uppercase_hex()
    {
        var gen = new IdentityGenerator();
        var seen = new HashSet<string>();
        for (int i = 0; i < 500; i++)
        {
            var c = gen.NewCard();
            Assert.True(IdentityFormat.IsValidAccessCode(c.AccessCode));
            Assert.True(IdentityFormat.IsValidChipId(c.ChipId));
            Assert.NotEqual('0', c.AccessCode[0]);
            Assert.True(seen.Add(c.ChipId));
        }
    }

    [Fact]
    public void Card_prefix_is_kept()
    {
        var c = new IdentityGenerator().NewCard("99");
        Assert.StartsWith("99", c.AccessCode);
        Assert.Equal(20, c.AccessCode.Length);
    }

    [Theory]
    [InlineData("9a")]
    [InlineData("1234567890123456789")]
    public void Bad_prefix_is_rejected(string prefix) =>
        Assert.Throws<ArgumentException>(() => new IdentityGenerator().NewCard(prefix));

    [Fact]
    public void Card_ToString_never_contains_values()
    {
        var c = new IdentityGenerator().NewCard();
        var s = c.ToString();
        Assert.DoesNotContain(c.AccessCode, s);
        Assert.DoesNotContain(c.ChipId, s);
        Assert.DoesNotContain(c.ChipId[..6], s);
    }

    [Theory]
    [InlineData("12345678901234567890", true)]
    [InlineData("1234567890123456789", false)]
    [InlineData("1234567890123456789a", false)]
    public void Access_code_format(string v, bool ok) => Assert.Equal(ok, IdentityFormat.IsValidAccessCode(v));

    [Theory]
    [InlineData("0123456789ABCDEF0123456789ABCDEF", true)]
    [InlineData("0123456789abcdef0123456789abcdef", false)]
    [InlineData("0123456789ABCDEF0123456789ABCDE", false)]
    public void Chip_format(string v, bool ok) => Assert.Equal(ok, IdentityFormat.IsValidChipId(v));

    [Fact]
    public void Mask_never_shows_characters()
    {
        Assert.Equal("(empty)", Secrets.Mask(""));
        Assert.Equal("(set, 20 chars)", Secrets.Mask("12345678901234567890"));
    }
}
