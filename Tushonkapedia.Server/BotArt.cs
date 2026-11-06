using System.Text.Json;
using System.Text.Json.Nodes;
using SPTarkov.Common.Models.Logging;
using Path = System.IO.Path;

namespace Tushonkapedia.Server;

// Bots' pictures: tarkov.dev's portrait (a head shot) and poster (the full figure) for each bot it knows, by the
// game's role name. Its list comes from its maps file (the "mobs" in it), fetched once a week and cut down to
// data/bots/bots.json; each picture is fetched the first time it's asked for and saved in data/bots. Bots it
// doesn't know (most mods' own) have none, and the page shows their initials instead. pack/bots/chosen.json picks
// better pictures by hand for bots tarkov.dev has a poor one or none of, and goes first.
public sealed class BotArt(ISptLogger<WikiRoute> logger, string folder, bool online)
{
    private const string Source = "https://json.tarkov.dev/regular/maps";
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(60) };
    private readonly SemaphoreSlim _gate = new(1, 1);
    private Dictionary<string, (string Slug, string Portrait, string Poster)>? _list;
    private DateTime _tried;
    private Dictionary<string, (string Portrait, string Poster)>? _chosen;

    private string Folder => Path.Combine(folder, "data", "bots");
    // the pictures and list the mod ships with, as WebP
    private PackFiles Packed => PackFiles.In(folder);

    public sealed record Picture(byte[] Bytes, string ContentType);

    // role (any case) -> its pictures; read from the saved list, fetched again when it's over a week old
    private async Task<Dictionary<string, (string Slug, string Portrait, string Poster)>> List(CancellationToken cancellationToken)
    {
        var file = Path.Combine(Folder, "bots.json");
        if (_list != null && (File.Exists(file) && File.GetLastWriteTimeUtc(file) > DateTime.UtcNow.AddDays(-7) || DateTime.UtcNow - _tried < TimeSpan.FromMinutes(10)))
        {
            return _list;
        }

        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (_list == null && File.Exists(file))
            {
                _list = Parse(JsonNode.Parse(await File.ReadAllTextAsync(file, cancellationToken)));
            }

            if (_list == null && Packed.Text("bots/bots.json") is { } packed)
            {
                _list = Parse(JsonNode.Parse(packed));
            }

            if (online && (!File.Exists(file) || File.GetLastWriteTimeUtc(file) < DateTime.UtcNow.AddDays(-7)) && DateTime.UtcNow - _tried > TimeSpan.FromMinutes(10))
            {
                _tried = DateTime.UtcNow;
                try
                {
                    var all = JsonNode.Parse(await Http.GetStringAsync(Source, cancellationToken));
                    var mobs = all?["data"]?["mobs"] as JsonObject;
                    if (mobs != null)
                    {
                        // only what's needed: role -> slug and the two pictures
                        var cut = new JsonObject();
                        foreach (var (role, mob) in mobs)
                        {
                            cut[role] = new JsonObject
                            {
                                ["slug"] = mob?["normalizedName"]?.ToString(), ["portrait"] = mob?["imagePortraitLink"]?.ToString(),
                                ["poster"] = mob?["imagePosterLink"]?.ToString()
                            };
                        }

                        Directory.CreateDirectory(Folder);
                        await File.WriteAllTextAsync(file, cut.ToJsonString(), cancellationToken);
                        _list = Parse(cut);
                    }
                }
                catch (Exception e)
                {
                    logger.Warning($"[Tushonkapedia] Couldn't get bot pictures from tarkov.dev, trying again later: {e.Message}");
                }
            }

            return _list ??= [];
        }
        finally
        {
            _gate.Release();
        }
    }

    private static Dictionary<string, (string, string, string)> Parse(JsonNode? node)
    {
        var list = new Dictionary<string, (string, string, string)>(StringComparer.OrdinalIgnoreCase);
        foreach (var (role, mob) in node as JsonObject ?? [])
        {
            var portrait = mob?["portrait"]?.ToString() ?? "";
            // tarkov.dev's stand-in for bots it has no picture of
            if (portrait.Contains("unknown-npc"))
            {
                continue;
            }

            list[role] = (mob?["slug"]?.ToString() ?? "", portrait, mob?["poster"]?.ToString() ?? "");
        }

        return list;
    }

    // The pictures for a bot: its role, else one of its other roles, else the tarkov.dev bot whose name starts its
    // name ("Black Div" for "Black Div Assault"), so a mod's backport of a live bot gets the live bot's picture.
    public async Task<(string Portrait, string Poster)?> For(IEnumerable<string> roles, string name, CancellationToken cancellationToken)
    {
        var chosen = Chosen();
        foreach (var role in roles)
        {
            if (chosen.TryGetValue(role, out var own))
            {
                return own;
            }
        }

        var list = await List(cancellationToken);
        // the plain role before its variants (bossTagilla before bossTagillaAgro, the event one)
        foreach (var role in roles.OrderBy(r => r.Length))
        {
            if (list.TryGetValue(role, out var hit))
            {
                return (hit.Portrait, hit.Poster);
            }
        }

        var slug = System.Text.RegularExpressions.Regex.Replace(name.ToLowerInvariant(), "[^a-z0-9]+", "-").Trim('-');
        var best = list.Values.Where(v => v.Slug.Length > 2 && (slug.StartsWith(v.Slug) || slug.StartsWith(v.Slug.Replace("the-", ""))))
            .OrderByDescending(v => v.Slug.Length).FirstOrDefault();
        return best.Portrait != null ? (best.Portrait, best.Poster) : null;
    }

    // role (any case) -> the pictures picked for it, as "pack:<file>"
    private Dictionary<string, (string Portrait, string Poster)> Chosen()
    {
        if (_chosen != null)
        {
            return _chosen;
        }

        var chosen = new Dictionary<string, (string, string)>(StringComparer.OrdinalIgnoreCase);
        try
        {
            if (Packed.Text("bots/chosen.json") is { } text)
            {
                foreach (var (role, pictures) in JsonNode.Parse(text) as JsonObject ?? [])
                {
                    chosen[role] = ("pack:" + pictures?["portrait"], "pack:" + pictures?["poster"]);
                }
            }
        }
        catch (Exception e) when (e is IOException or JsonException)
        {
            logger.Warning($"[Tushonkapedia] Couldn't read the chosen bot pictures: {e.Message}");
        }

        return _chosen = chosen;
    }

    // One picture, fetched from tarkov.dev the first time and saved
    public async Task<Picture?> Get(string url, CancellationToken cancellationToken)
    {
        if (url.StartsWith("pack:"))
        {
            return Packed.Bytes("bots/" + Path.GetFileName(url[5..])) is { } own ? new Picture(own, "image/webp") : null;
        }

        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Host != "assets.tarkov.dev")
        {
            return null;
        }

        var name = Path.GetFileName(uri.AbsolutePath);
        var type = name.EndsWith(".webp") ? "image/webp" : name.EndsWith(".jpg") ? "image/jpeg" : "image/png";
        var file = Path.Combine(Folder, name);
        if (File.Exists(file))
        {
            return new Picture(await File.ReadAllBytesAsync(file, cancellationToken), type);
        }

        // shipped as it came (a JPEG poster, a WebP portrait) or, for a PNG, as a lossless WebP
        if (Packed.Bytes("bots/" + name) is { } original)
        {
            return new Picture(original, type);
        }

        if (Packed.Bytes("bots/" + Path.GetFileNameWithoutExtension(name) + ".webp") is { } packed)
        {
            return new Picture(packed, "image/webp");
        }

        if (!online)
        {
            return null;
        }

        try
        {
            var bytes = await Http.GetByteArrayAsync(uri, cancellationToken);
            Directory.CreateDirectory(Folder);
            await File.WriteAllBytesAsync(file, bytes, cancellationToken);
            return new Picture(bytes, type);
        }
        catch (Exception e)
        {
            logger.Warning($"[Tushonkapedia] Couldn't get the bot picture {name}: {e.Message}");
            return null;
        }
    }
}
