using System.Collections.Concurrent;
using System.IO.Compression;
using System.Text;

namespace Tushonkapedia.Server;

// What the mod ships with (item pictures, map tiles and drawings, bot pictures, saved lists), read in place from
// pack.zip, so installing the mod means extracting one file instead of thousands. Paths are relative to the pack,
// with forward slashes ("icons/<id>.webp"). Without pack.zip (a build from source), the pack/ folder is read instead.
internal sealed class PackFiles
{
    private static readonly ConcurrentDictionary<string, PackFiles> Opened = new(StringComparer.OrdinalIgnoreCase);

    private readonly string _dir;
    private readonly ZipArchive? _zip;
    private readonly Dictionary<string, ZipArchiveEntry> _entries = new(StringComparer.Ordinal);
    // one zip, one stream under it: entries are read one at a time
    private readonly Lock _lock = new();

    private PackFiles(string modFolder)
    {
        _dir = Path.Combine(modFolder, "pack");
        var file = Path.Combine(modFolder, "pack.zip");
        if (!File.Exists(file))
        {
            return;
        }

        try
        {
            _zip = new ZipArchive(File.OpenRead(file), ZipArchiveMode.Read);
            foreach (var entry in _zip.Entries)
            {
                var name = entry.FullName.Replace('\\', '/');
                // folders have no file to read; empty files are kept (an empty map tile means "no tile here")
                if (!name.EndsWith('/'))
                {
                    _entries[name] = entry;
                }
            }
        }
        catch (Exception e) when (e is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            // a damaged or locked pack.zip: fall back to the folder, if there is one
            Problem = e.Message;
            _zip?.Dispose();
            _zip = null;
            _entries.Clear();
        }
    }

    public static PackFiles In(string modFolder) => Opened.GetOrAdd(modFolder, folder => new PackFiles(folder));

    // why pack.zip couldn't be read, or null; said once at server start
    public string? Problem { get; }

    public bool Has(string path) => _zip != null ? _entries.ContainsKey(path) : File.Exists(Path.Combine(_dir, path));

    public byte[]? Bytes(string path)
    {
        if (_zip == null)
        {
            var file = Path.Combine(_dir, path);
            return File.Exists(file) ? File.ReadAllBytes(file) : null;
        }

        if (!_entries.TryGetValue(path, out var entry))
        {
            return null;
        }

        lock (_lock)
        {
            using var stream = entry.Open();
            var bytes = new byte[entry.Length];
            stream.ReadExactly(bytes);
            return bytes;
        }
    }

    public string? Text(string path)
    {
        if (Bytes(path) is not { } bytes)
        {
            return null;
        }

        using var reader = new StreamReader(new MemoryStream(bytes), Encoding.UTF8, true);
        return reader.ReadToEnd();
    }
}
