using System.Security.Cryptography;

namespace Exvs2Launcher.Core.Files;

/// <summary>
/// Namco nbamsavdat "svd1" container used by <c>29/testmode_pcb.json</c>.
/// Layout (little-endian): magic "svd1", u32 version, u64 body length, SHA-1(header[0..16] || body), then the body.
/// Bytes past the declared length are trailing and are not hashed. Version 1 is the only version this code will write.
/// </summary>
public sealed class Svd1Container
{
    public const int HeaderLength = 16;
    public const int HashLength = 20;
    public const int PrefixLength = HeaderLength + HashLength;
    public const int SupportedVersion = 1;
    public static readonly byte[] Magic = "svd1"u8.ToArray();

    public int Version { get; }
    public byte[] Body { get; }
    public byte[] Trailing { get; }
    public byte[] StoredDigest { get; }
    public byte[] ComputedDigest { get; }
    public bool ChecksumOk { get; }

    private Svd1Container(int version, byte[] body, byte[] trailing, byte[] stored, byte[] computed)
    {
        Version = version;
        Body = body;
        Trailing = trailing;
        StoredDigest = stored;
        ComputedDigest = computed;
        ChecksumOk = stored.AsSpan().SequenceEqual(computed);
    }

    public static bool TryParse(ReadOnlySpan<byte> data, out Svd1Container? file, out string? error)
    {
        file = null;
        error = null;
        if (data.Length < PrefixLength)
        {
            error = "The file is too small to be an svd1 container.";
            return false;
        }
        if (!data[..4].SequenceEqual(Magic))
        {
            error = "This is not an svd1 file (the header magic is missing or different). The file was left unchanged.";
            return false;
        }
        int version = BitConverter.ToInt32(data[4..8]);
        uint lo = BitConverter.ToUInt32(data[8..12]);
        uint hi = BitConverter.ToUInt32(data[12..16]);
        ulong length = lo | ((ulong)hi << 32);
        if (length > (ulong)(data.Length - PrefixLength))
        {
            error = "The svd1 header length is larger than the file. The file was left unchanged.";
            return false;
        }
        if (length > int.MaxValue)
        {
            error = "The svd1 body is too large for this launcher to handle. The file was left unchanged.";
            return false;
        }
        int bodyLen = (int)length;
        var header = data[..HeaderLength].ToArray();
        var stored = data[HeaderLength..PrefixLength].ToArray();
        var body = data[PrefixLength..(PrefixLength + bodyLen)].ToArray();
        var trailing = data[(PrefixLength + bodyLen)..].ToArray();
        var computed = Digest(header, body);
        file = new Svd1Container(version, body, trailing, stored, computed);
        return true;
    }

    public static byte[] Pack(int version, ReadOnlySpan<byte> body, ReadOnlySpan<byte> trailing)
    {
        if (version != SupportedVersion)
            throw new InvalidOperationException("Refusing to write svd1 version " + version + " (only version " + SupportedVersion + " is supported).");
        var header = new byte[HeaderLength];
        Magic.CopyTo(header, 0);
        BitConverter.TryWriteBytes(header.AsSpan(4), version);
        ulong length = (ulong)body.Length;
        BitConverter.TryWriteBytes(header.AsSpan(8), (uint)(length & 0xFFFFFFFF));
        BitConverter.TryWriteBytes(header.AsSpan(12), (uint)(length >> 32));
        var digest = Digest(header, body);
        var result = new byte[PrefixLength + body.Length + trailing.Length];
        header.CopyTo(result, 0);
        digest.CopyTo(result, HeaderLength);
        body.CopyTo(result.AsSpan(PrefixLength));
        trailing.CopyTo(result.AsSpan(PrefixLength + body.Length));
        return result;
    }

    /// <summary>Re-wrap the same body and trailing. Fails if the version is not the one we can write.</summary>
    public bool TryPack(out byte[]? bytes, out string? error)
    {
        bytes = null;
        if (Version != SupportedVersion)
        {
            error = "This testmode file uses svd1 version " + Version +
                    ", which this launcher cannot safely rewrite. The file was left unchanged.";
            return false;
        }
        if (!ChecksumOk)
        {
            error = "The svd1 checksum does not match SHA-1(header + body). This launcher will not rewrite a file with an unknown or invalid checksum. The file was left unchanged.";
            return false;
        }
        error = null;
        bytes = Pack(Version, Body, Trailing);
        return true;
    }

    public static string Describe(Svd1Container file) =>
        "svd1 version=" + file.Version +
        " body=" + file.Body.Length +
        " trailing=" + file.Trailing.Length +
        " sha1=" + Convert.ToHexString(file.StoredDigest).ToLowerInvariant() +
        (file.ChecksumOk ? " (ok)" : " (mismatch)");

    private static byte[] Digest(ReadOnlySpan<byte> header, ReadOnlySpan<byte> body)
    {
        var buf = new byte[header.Length + body.Length];
        header.CopyTo(buf);
        body.CopyTo(buf.AsSpan(header.Length));
        return SHA1.HashData(buf);
    }
}
