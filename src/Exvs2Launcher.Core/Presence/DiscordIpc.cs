using System.Buffers.Binary;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Exvs2Launcher.Core.Presence;

/// <summary>
/// Discord's local RPC framing: little-endian int32 opcode, int32 length, UTF-8 JSON payload.
/// Opcodes: 0 handshake, 1 frame, 2 close, 3 ping, 4 pong.
/// </summary>
public static class DiscordFrame
{
    public const int Handshake = 0, Frame = 1, Close = 2, Ping = 3, Pong = 4;
    public const int MaxPayload = 64 * 1024;

    public static byte[] Encode(int opcode, string json)
    {
        var payload = Encoding.UTF8.GetBytes(json);
        var buf = new byte[8 + payload.Length];
        BinaryPrimitives.WriteInt32LittleEndian(buf.AsSpan(0, 4), opcode);
        BinaryPrimitives.WriteInt32LittleEndian(buf.AsSpan(4, 4), payload.Length);
        payload.CopyTo(buf, 8);
        return buf;
    }

    public static async Task<(int Opcode, string Json)> ReadAsync(Stream s, CancellationToken ct)
    {
        var header = new byte[8];
        await ReadExactAsync(s, header, ct).ConfigureAwait(false);
        int op = BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(0, 4));
        int len = BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(4, 4));
        if (len < 0 || len > MaxPayload) throw new InvalidDataException("Discord sent a frame with a bad length.");
        var payload = new byte[len];
        await ReadExactAsync(s, payload, ct).ConfigureAwait(false);
        return (op, Encoding.UTF8.GetString(payload));
    }

    private static async Task ReadExactAsync(Stream s, byte[] buf, CancellationToken ct)
    {
        int read = 0;
        while (read < buf.Length)
        {
            int n = await s.ReadAsync(buf.AsMemory(read), ct).ConfigureAwait(false);
            if (n == 0) throw new EndOfStreamException("Discord closed the connection.");
            read += n;
        }
    }
}

/// <summary>
/// Minimal Discord Rich Presence client over the local IPC pipe (<c>\\.\pipe\discord-ipc-0..9</c>). It only does
/// the handshake, SET_ACTIVITY and close; no OAuth, no other RPC commands. The app id is the user's own Discord
/// application id (see docs/RICH-PRESENCE.md).
/// </summary>
public sealed class DiscordIpcClient : IAsyncDisposable
{
    private readonly Func<CancellationToken, Task<Stream?>> _connect;
    private Stream? _stream;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public DiscordIpcClient(Func<CancellationToken, Task<Stream?>>? connect = null) => _connect = connect ?? ConnectPipeAsync;

    public bool Connected => _stream is not null;
    public string? LastError { get; private set; }

    public static bool IsValidClientId(string? id) =>
        !string.IsNullOrWhiteSpace(id) && id.Trim().Length is >= 15 and <= 22 && id.Trim().All(char.IsAsciiDigit);

    public async Task<bool> ConnectAsync(string clientId, CancellationToken ct)
    {
        if (!IsValidClientId(clientId)) { LastError = "The Discord application id must be the long number from the Discord developer portal."; return false; }
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await CloseCoreAsync().ConfigureAwait(false);
            var s = await _connect(ct).ConfigureAwait(false);
            if (s is null) { LastError = "Discord is not running (no IPC pipe found)."; return false; }
            var hello = new JsonObject { ["v"] = 1, ["client_id"] = clientId.Trim() };
            await s.WriteAsync(DiscordFrame.Encode(DiscordFrame.Handshake, hello.ToJsonString()), ct).ConfigureAwait(false);
            await s.FlushAsync(ct).ConfigureAwait(false);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(5));
            var (op, json) = await DiscordFrame.ReadAsync(s, timeout.Token).ConfigureAwait(false);
            if (op == DiscordFrame.Close || !json.Contains("\"READY\"", StringComparison.Ordinal))
            {
                LastError = "Discord refused the connection: " + ErrorText(json);
                await s.DisposeAsync().ConfigureAwait(false);
                return false;
            }
            _stream = s;
            LastError = null;
            return true;
        }
        catch (Exception ex) when (ex is IOException or TimeoutException or OperationCanceledException or InvalidDataException or UnauthorizedAccessException)
        {
            LastError = ex.Message;
            await CloseCoreAsync().ConfigureAwait(false);
            return false;
        }
        finally { _gate.Release(); }
    }

    /// <summary>Sets (or with null clears) the activity. Returns false when Discord is gone; reconnect later.</summary>
    public async Task<bool> SetActivityAsync(JsonObject? activity, int pid, CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_stream is null) return false;
            var cmd = BuildSetActivity(activity, pid, Guid.NewGuid().ToString("N"));
            await _stream.WriteAsync(DiscordFrame.Encode(DiscordFrame.Frame, cmd), ct).ConfigureAwait(false);
            await _stream.FlushAsync(ct).ConfigureAwait(false);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(5));
            while (true)
            {
                var (op, json) = await DiscordFrame.ReadAsync(_stream, timeout.Token).ConfigureAwait(false);
                if (op == DiscordFrame.Ping)
                {
                    await _stream.WriteAsync(DiscordFrame.Encode(DiscordFrame.Pong, json), ct).ConfigureAwait(false);
                    continue;
                }
                if (op == DiscordFrame.Close) { LastError = "Discord closed the connection: " + ErrorText(json); await CloseCoreAsync().ConfigureAwait(false); return false; }
                if (json.Contains("\"ERROR\"", StringComparison.Ordinal)) { LastError = "Discord: " + ErrorText(json); return false; }
                return true;
            }
        }
        catch (Exception ex) when (ex is IOException or OperationCanceledException or InvalidDataException or ObjectDisposedException)
        {
            LastError = ex.Message;
            await CloseCoreAsync().ConfigureAwait(false);
            return false;
        }
        finally { _gate.Release(); }
    }

    public static string BuildSetActivity(JsonObject? activity, int pid, string nonce)
    {
        var args = new JsonObject { ["pid"] = pid };
        args["activity"] = activity?.DeepClone();
        return new JsonObject { ["cmd"] = "SET_ACTIVITY", ["args"] = args, ["nonce"] = nonce }.ToJsonString();
    }

    public async ValueTask DisposeAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try { await CloseCoreAsync().ConfigureAwait(false); }
        finally { _gate.Release(); }
    }

    private async Task CloseCoreAsync()
    {
        var s = _stream;
        _stream = null;
        if (s is null) return;
        try { await s.WriteAsync(DiscordFrame.Encode(DiscordFrame.Close, "{}")).ConfigureAwait(false); } catch (Exception) { }
        try { await s.DisposeAsync().ConfigureAwait(false); } catch (Exception) { }
    }

    private static string ErrorText(string json)
    {
        try
        {
            var n = JsonNode.Parse(json);
            var msg = n?["data"]?["message"]?.GetValue<string>() ?? n?["message"]?.GetValue<string>();
            if (!string.IsNullOrWhiteSpace(msg)) return msg!;
        }
        catch (Exception) { }
        return json.Length > 200 ? json[..200] : json;
    }

    private static async Task<Stream?> ConnectPipeAsync(CancellationToken ct)
    {
        if (!OperatingSystem.IsWindows()) return null;
        for (int i = 0; i < 10; i++)
        {
            var pipe = new NamedPipeClientStream(".", "discord-ipc-" + i, PipeDirection.InOut, PipeOptions.Asynchronous);
            try
            {
                await pipe.ConnectAsync(300, ct).ConfigureAwait(false);
                return pipe;
            }
            catch (Exception ex) when (ex is TimeoutException or IOException or UnauthorizedAccessException)
            {
                await pipe.DisposeAsync().ConfigureAwait(false);
            }
        }
        return null;
    }
}
