using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using SPTarkov.Common.Models.Logging;

namespace Tushonkapedia.Server;

// Item pictures for the page. SPT has no item icons (the game client draws them), so each one comes from:
//   data/icons/<id>.png   drawn by the game itself: the Tushonkapedia.Client plugin renders the items that have no
//                         picture yet (mostly mod items) in the main menu and sends them here, once
//   data/icons/<id>.webp  fetched from tarkov.dev the first time it's asked for, so it's there next time offline
// Items with neither are remembered in data/icons/none.txt, so tarkov.dev isn't asked again and the plugin
// knows to draw them.
//   icons/<id>.webp in pack.zip  the base-game pictures the mod ships with, so there are pictures with no internet at all
public sealed partial class ItemIcons(ISptLogger<WikiRoute> logger, string folder, bool online)
{
    private const string Source = "https://assets.tarkov.dev/{0}-icon.webp";
    private const string NoneFile = "none.txt";

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(15) };

    private readonly ConcurrentDictionary<string, bool> _missing = new();
    private readonly SemaphoreSlim _gate = new(4);
    private readonly Lock _noneLock = new();
    private int _warned;
    private bool _loaded;

    private string Folder => Path.Combine(folder, "data", "icons");
    // the pictures the mod ships with (pack.zip)
    private PackFiles Packed => PackFiles.In(folder);

    // whether tarkov.dev may be asked (useInternet in config.json)
    public bool Online => online;

    public sealed record Picture(byte[] Bytes, string ContentType);

    // the picture, or null when there is none; tarkov.dev is only asked for items it could have
    public async Task<Picture?> Get(string id, bool fetch, CancellationToken cancellationToken)
    {
        if (!ItemId().IsMatch(id))
        {
            return null;
        }

        var saved = await Saved(id, cancellationToken);
        if (saved != null || !fetch || !online || IsMissing(id))
        {
            return saved;
        }

        var file = Path.Combine(Folder, id + ".webp");
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (File.Exists(file))
            {
                return new Picture(await File.ReadAllBytesAsync(file, cancellationToken), "image/webp");
            }

            using var response = await Http.GetAsync(string.Format(Source, id), cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                MarkMissing(id);
                return null;
            }

            var bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken);
            Directory.CreateDirectory(Folder);
            await File.WriteAllBytesAsync(file, bytes, cancellationToken);
            return new Picture(bytes, "image/webp");
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !cancellationToken.IsCancellationRequested)
        {
            // offline: say so once, and show no pictures for what isn't saved yet (not remembered, it may be back)
            _missing[id] = false;
            if (Interlocked.Exchange(ref _warned, 1) == 0)
            {
                logger.Warning($"[Tushonkapedia] Couldn't get item pictures from tarkov.dev ({ex.GetBaseException().Message}); only saved ones are shown.");
            }

            return null;
        }
        finally
        {
            _gate.Release();
        }
    }

    // A picture the game itself drew: the game plugin's (data/icons/<id>.png), else one shipped in the pack that the
    // game drew (it has a .print beside it). Preferred over tarkov.dev's, so every picture on the page looks alike.
    public async Task<Picture?> Drawn(string id, CancellationToken cancellationToken)
    {
        if (!ItemId().IsMatch(id))
        {
            return null;
        }

        var png = Path.Combine(Folder, id + ".png");
        if (File.Exists(png))
        {
            return new Picture(await File.ReadAllBytesAsync(png, cancellationToken), "image/png");
        }

        return Packed.Has($"icons/{id}.print") && Packed.Bytes($"icons/{id}.webp") is { } packed ? new Picture(packed, "image/webp") : null;
    }

    private async Task<Picture?> Saved(string id, CancellationToken cancellationToken)
    {
        if (await Drawn(id, cancellationToken) is { } drawn)
        {
            return drawn;
        }

        var webp = Path.Combine(Folder, id + ".webp");
        if (File.Exists(webp))
        {
            return new Picture(await File.ReadAllBytesAsync(webp, cancellationToken), "image/webp");
        }

        return Packed.Bytes($"icons/{id}.webp") is { } packed ? new Picture(packed, "image/webp") : null;
    }

    // Whether the game has drawn a picture of the item that still matches it. The game plugin draws every item
    // without one, base-game items too (tarkov.dev's pictures only stand in until then). A picture the game drew
    // (or one shipped in the pack) has a .print file beside it: what the item looked like then (ItemWiki.IconPrint).
    // When a mod update changed the item, the print differs and it's drawn again. A drawing from before prints were
    // kept has none: the pack's print for the item, when it has one, says whether the item has changed since.
    public bool HasDrawn(string id, string print)
    {
        var packPrint = Packed.Text($"icons/{id}.print")?.Trim();
        return Current(Path.Combine(Folder, id + ".png"), print, packPrint)
               || packPrint != null && Packed.Has($"icons/{id}.webp") && (print.Length == 0 || packPrint == print);
    }

    private static string? PrintOf(string picture)
    {
        var file = Path.ChangeExtension(picture, ".print");
        return File.Exists(file) ? File.ReadAllText(file).Trim() : null;
    }

    private static bool Current(string picture, string print, string? fallback = null)
    {
        if (!File.Exists(picture))
        {
            return false;
        }

        var saved = PrintOf(picture) ?? fallback;
        return print.Length == 0 || saved == null || saved == print;
    }

    public bool IsMissing(string id)
    {
        LoadNone();
        return _missing.ContainsKey(id);
    }

    // A picture the game drew, with the item's print. Only PNGs of a sane size, and only for items with no
    // picture that matches the item as it is now (a mod update may have changed it).
    // A picture the plugin drew: only for a real item (it has a print), only a PNG of at most 512 × 512, and never
    // over a picture that is still current, so nothing sent can replace a good picture.
    public bool Save(string id, byte[] png, string print)
    {
        var file = Path.Combine(Folder, id + ".png");
        if (!ItemId().IsMatch(id) || print.Length == 0 || !IsPng(png, 512, 512)
            || Current(file, print) && File.Exists(Path.ChangeExtension(file, ".print")))
        {
            return false;
        }

        Directory.CreateDirectory(Folder);
        File.WriteAllBytes(file, png);
        if (print.Length > 0)
        {
            File.WriteAllText(Path.ChangeExtension(file, ".print"), print);
        }

        return true;
    }

    // the PNG signature, then the IHDR chunk with width and height, big-endian
    public static bool IsPng(byte[] png, int maxWidth, int maxHeight) =>
        png.Length is > 24 and <= 2_000_000
        && png.AsSpan(0, 8).SequenceEqual(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A })
        && png.AsSpan(12, 4).SequenceEqual("IHDR"u8)
        && System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(16, 4)) is var w && w > 0 && w <= maxWidth
        && System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(20, 4)) is var h && h > 0 && h <= maxHeight;

    // Items the game couldn't draw (no model, or it never finished), with the print they had and how many times: after
    // GiveUpAfter tries, not asked for again until the item changes, so the plugin doesn't retry the same ones every
    // time the game starts, while one slow load doesn't count against an item for good.
    // Delete data/icons/failed.json to have them all tried again.
    private const int GiveUpAfter = 3;
    private sealed record Try(string Print, int Count);
    private Dictionary<string, Try>? _failed;
    private readonly Lock _failedLock = new();
    private string FailedFile => Path.Combine(Folder, "failed.json");

    private Dictionary<string, Try> FailedList()
    {
        if (_failed != null)
        {
            return _failed;
        }

        try
        {
            _failed = File.Exists(FailedFile) ? System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, Try>>(File.ReadAllText(FailedFile)) : null;
        }
        catch (Exception ex) when (ex is IOException or System.Text.Json.JsonException)
        {
            _failed = null;
        }

        return _failed ??= [];
    }

    public bool Failed(string id, string print)
    {
        lock (_failedLock)
        {
            return FailedList().TryGetValue(id, out var was) && was.Print == print && was.Count >= GiveUpAfter;
        }
    }

    public void MarkFailed(IEnumerable<(string Id, string Print)> items)
    {
        lock (_failedLock)
        {
            var list = FailedList();
            var changed = false;
            foreach (var (id, print) in items)
            {
                if (ItemId().IsMatch(id) && print.Length > 0)
                {
                    list[id] = new Try(print, list.TryGetValue(id, out var was) && was.Print == print ? was.Count + 1 : 1);
                    changed = true;
                }
            }

            if (changed)
            {
                Directory.CreateDirectory(Folder);
                File.WriteAllText(FailedFile, System.Text.Json.JsonSerializer.Serialize(list));
            }
        }
    }

    private void MarkMissing(string id)
    {
        if (!_missing.TryAdd(id, true))
        {
            return;
        }

        lock (_noneLock)
        {
            Directory.CreateDirectory(Folder);
            File.AppendAllText(Path.Combine(Folder, NoneFile), id + Environment.NewLine);
        }
    }

    private void LoadNone()
    {
        if (_loaded)
        {
            return;
        }

        lock (_noneLock)
        {
            if (_loaded)
            {
                return;
            }

            var file = Path.Combine(Folder, NoneFile);
            if (File.Exists(file))
            {
                foreach (var line in File.ReadAllLines(file))
                {
                    if (ItemId().IsMatch(line.Trim()))
                    {
                        _missing.TryAdd(line.Trim(), true);
                    }
                }
            }

            _loaded = true;
        }
    }

    [GeneratedRegex("^[0-9a-f]{24}$")]
    private static partial Regex ItemId();
}
