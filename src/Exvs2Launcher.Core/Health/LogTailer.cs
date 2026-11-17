using System.Text;

namespace Exvs2Launcher.Core.Health;

/// <summary>
/// Follows the newest <c>log-*.txt</c> in a folder without locking it (the card server keeps it open). The first poll
/// starts at the end of the file (old lines only fill the context buffer); later polls return complete new lines.
/// When the server rolls over to a new day's file, the tailer moves to it from the start.
/// </summary>
public sealed class LogTailer
{
    private readonly Func<string?> _currentFile;
    private readonly int _contextSize;
    private readonly LinkedList<string> _recent = new();
    private string? _file;
    private long _pos;
    private string _partial = "";

    public LogTailer(Func<string?> currentFile, int contextSize = 200)
    {
        _currentFile = currentFile;
        _contextSize = Math.Max(1, contextSize);
    }

    public static LogTailer ForFolder(string logsDir, int contextSize = 200) => new(() =>
    {
        try
        {
            if (!Directory.Exists(logsDir)) return null;
            return new DirectoryInfo(logsDir).GetFiles("log-*.txt").OrderByDescending(f => f.LastWriteTimeUtc).FirstOrDefault()?.FullName;
        }
        catch (Exception) { return null; }
    }, contextSize);

    public string? CurrentFile => _file;

    /// <summary>The last lines seen (oldest first), for saving context around an event.</summary>
    public IReadOnlyList<string> Recent(int count) => _recent.Skip(Math.Max(0, _recent.Count - count)).ToList();

    public IReadOnlyList<string> Poll()
    {
        var file = _currentFile();
        if (file is null) return Array.Empty<string>();
        bool first = _file is null;
        if (!string.Equals(file, _file, StringComparison.OrdinalIgnoreCase))
        {
            _file = file;
            _partial = "";
            _pos = 0;
            if (first)
            {
                // Attach at the end; remember the tail as context only.
                var tail = ReadFrom(file, Math.Max(0, Length(file) - 64 * 1024), out var end, skipFirstPartial: true);
                foreach (var l in tail) Remember(l);
                _pos = end;
                return Array.Empty<string>();
            }
        }
        long len = Length(file);
        if (len < _pos) { _pos = 0; _partial = ""; } // truncated / replaced
        if (len == _pos) return Array.Empty<string>();
        var lines = ReadFrom(file, _pos, out var newEnd, skipFirstPartial: false);
        _pos = newEnd;
        foreach (var l in lines) Remember(l);
        return lines;
    }

    private void Remember(string line)
    {
        _recent.AddLast(line);
        while (_recent.Count > _contextSize) _recent.RemoveFirst();
    }

    private static long Length(string file)
    {
        try { return new FileInfo(file).Length; } catch (Exception) { return 0; }
    }

    private List<string> ReadFrom(string file, long start, out long end, bool skipFirstPartial)
    {
        var result = new List<string>();
        end = start;
        try
        {
            using var fs = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            fs.Seek(start, SeekOrigin.Begin);
            var max = Math.Min(fs.Length - start, 4 * 1024 * 1024);
            var buf = new byte[max];
            int read = 0, n;
            while (read < max && (n = fs.Read(buf, read, (int)max - read)) > 0) read += n;
            end = start + read;
            var text = _partial + Encoding.UTF8.GetString(buf, 0, read);
            _partial = "";
            var parts = text.Split('\n');
            for (int i = 0; i < parts.Length; i++)
            {
                if (i == parts.Length - 1) { _partial = parts[i]; break; }
                if (i == 0 && skipFirstPartial && start > 0) continue;
                result.Add(parts[i].TrimEnd('\r'));
            }
            if (skipFirstPartial) _partial = "";
            if (skipFirstPartial && parts.Length > 0) end -= Encoding.UTF8.GetByteCount(parts[^1]);
        }
        catch (Exception) { }
        return result;
    }
}
