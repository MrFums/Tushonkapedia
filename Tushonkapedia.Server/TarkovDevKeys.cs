using System.Text.Json;
using SPTarkov.Common.Models.Logging;

namespace Tushonkapedia.Server;

// Keys a quest needs, from tarkov.dev (the quest data itself has no such list). Fetched in the background and
// saved to data/, so if tarkov.dev can't be reached the last ones are used. These are the live game's keys: a mod that moves a quest item behind another door isn't known.
public sealed class TarkovDevKeys(ISptLogger<WikiRoute> logger, string folder, ModConfig config)
{
    private const string Source = "https://json.tarkov.dev/{0}/tasks";
    private const string CacheFile = "tarkovdev-keys.json";

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(60) };
    private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, WriteIndented = true };

    private Saved? _saved;
    private int _fetching;

    // quest id -> keys, and quest id -> wiki page
    public sealed record Saved(string Mode, long Fetched, Dictionary<string, List<KeyEntry>> Keys, Dictionary<string, string> Wiki);

    // goes up whenever new data arrives, so the graph is built again
    public int Version { get; private set; }

    public Saved? Current => _saved;

    public string? Error { get; private set; }

    public void Start()
    {
        if (!config.TarkovDevKeys)
        {
            return;
        }

        _saved = Read();
        Version++;
        var old = _saved == null || _saved.Mode != config.TarkovDevGameMode
            || DateTimeOffset.UtcNow.ToUnixTimeSeconds() - _saved.Fetched > config.RefreshHours * 3600;
        if (old && config.UseInternet)
        {
            Task.Run(Fetch);
        }
    }

    private void Fetch()
    {
        if (Interlocked.Exchange(ref _fetching, 1) == 1)
        {
            return;
        }

        try
        {
            var mode = config.TarkovDevGameMode;
            using var stream = Http.GetStreamAsync(string.Format(Source, mode)).GetAwaiter().GetResult();
            using var json = JsonDocument.Parse(stream);
            var keys = new Dictionary<string, List<KeyEntry>>();
            var wiki = new Dictionary<string, string>();

            foreach (var task in json.RootElement.GetProperty("data").GetProperty("tasks").EnumerateObject())
            {
                var id = task.Name;
                var list = new List<KeyEntry>();
                if (task.Value.TryGetProperty("wikiLink", out var link) && link.ValueKind == JsonValueKind.String)
                {
                    wiki[id] = link.GetString()!;
                }

                // per objective: each inner list is one key, any of which will do
                if (task.Value.TryGetProperty("objectives", out var objectives))
                {
                    foreach (var objective in objectives.EnumerateArray())
                    {
                        if (!objective.TryGetProperty("requiredKeys", out var required) || required.ValueKind != JsonValueKind.Array)
                        {
                            continue;
                        }

                        var map = objective.TryGetProperty("maps", out var maps) && maps.ValueKind == JsonValueKind.Array && maps.GetArrayLength() > 0
                            ? maps[0].GetString()
                            : null;
                        foreach (var alternatives in required.EnumerateArray())
                        {
                            var ids = Strings(alternatives);
                            if (ids.Count > 0)
                            {
                                list.Add(new KeyEntry { Keys = ids, Map = map, Objective = objective.GetProperty("id").GetString() });
                            }
                        }
                    }
                }

                // per quest, for keys not tied to one objective
                if (task.Value.TryGetProperty("neededKeys", out var needed) && needed.ValueKind == JsonValueKind.Array)
                {
                    foreach (var entry in needed.EnumerateArray())
                    {
                        var ids = entry.TryGetProperty("keys", out var k) ? Strings(k) : [];
                        var map = entry.TryGetProperty("map", out var m) && m.ValueKind == JsonValueKind.String ? m.GetString() : null;
                        if (ids.Count > 0 && !list.Any(e => e.Keys.Intersect(ids).Any()))
                        {
                            list.Add(new KeyEntry { Keys = ids, Map = map });
                        }
                    }
                }

                if (list.Count > 0)
                {
                    keys[id] = list;
                }
            }

            if (wiki.Count < 100)
            {
                throw new InvalidDataException($"only {wiki.Count} quests came back");
            }

            _saved = new Saved(mode, DateTimeOffset.UtcNow.ToUnixTimeSeconds(), keys, wiki);
            Directory.CreateDirectory(Path.Combine(folder, "data"));
            File.WriteAllText(Path.Combine(folder, "data", CacheFile), JsonSerializer.Serialize(_saved, Json));
            Error = null;
            Version++;
            logger.Info($"[Tushonkapedia] Keys from tarkov.dev ({mode}): {keys.Count} quests.");
        }
        catch (Exception ex)
        {
            Error = ex.GetBaseException().Message;
            logger.Warning($"[Tushonkapedia] Couldn't get keys from tarkov.dev ({Error}), keeping the last ones.");
        }
        finally
        {
            Interlocked.Exchange(ref _fetching, 0);
        }
    }

    private Saved? Read()
    {
        try
        {
            // what was fetched last, else the copy the mod ships with
            var file = Path.Combine(folder, "data", CacheFile);
            var text = File.Exists(file) ? File.ReadAllText(file) : PackFiles.In(folder).Text(CacheFile);
            return text != null ? JsonSerializer.Deserialize<Saved>(text, Json) : null;
        }
        catch (Exception ex)
        {
            logger.Warning($"[Tushonkapedia] Saved tarkov.dev keys couldn't be read: {ex.Message}");
            return null;
        }
    }

    private static List<string> Strings(JsonElement array)
    {
        return array.ValueKind == JsonValueKind.Array
            ? array.EnumerateArray().Where(e => e.ValueKind == JsonValueKind.String).Select(e => e.GetString()!).ToList()
            : [];
    }
}

// config.json in the mod folder
public sealed class ModConfig
{
    // get the keys each quest needs from tarkov.dev (needs internet now and then; the last ones are kept)
    public bool TarkovDevKeys { get; init; } = true;
    // regular, pve or pvp-season
    public string TarkovDevGameMode { get; init; } = "pve";
    public double RefreshHours { get; init; } = 24;
    // show item pictures, fetched from tarkov.dev once and saved in data/icons
    public bool ItemIcons { get; init; } = true;
    // draw the loot map over tarkov.dev's drawing of the map, fetched once and saved in data/maps
    public bool MapImages { get; init; } = true;
    // who may send the item pictures the game plugin draws: "lan" (this PC and the home network), "anyone" or "none"
    public string IconUploads { get; init; } = "lan";
    // let the page list profiles and show their quest progress (read-only)
    public bool ShowProfiles { get; init; } = true;
    // ask tarkov.dev for anything at all. Off: everything comes from the pictures, maps and keys the mod ships with
    // (pack/) and what it has saved, and nothing ever goes out to the internet
    public bool UseInternet { get; init; } = false;
    // who may start crafts and hideout upgrades from the page, for a character whose game is closed: "lan", "anyone" or "none"
    public string HideoutActions { get; init; } = "lan";

    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true
    };

    public static ModConfig Load(string folder, ISptLogger<WikiRoute> logger)
    {
        var file = Path.Combine(folder, "config.json");
        try
        {
            return File.Exists(file) ? JsonSerializer.Deserialize<ModConfig>(File.ReadAllText(file), Options) ?? new ModConfig() : new ModConfig();
        }
        catch (Exception ex)
        {
            logger.Warning($"[Tushonkapedia] config.json couldn't be read, using defaults: {ex.Message}");
            return new ModConfig();
        }
    }

    // keys.json: your own entries, added to (or for a quest, replacing) tarkov.dev's
    public static Dictionary<string, List<KeyEntry>> UserKeys(string folder, ISptLogger<WikiRoute> logger)
    {
        var file = Path.Combine(folder, "keys.json");
        try
        {
            if (!File.Exists(file))
            {
                return [];
            }

            var all = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(File.ReadAllText(file), Options) ?? [];
            return all.Where(e => !e.Key.StartsWith('_') && e.Value.ValueKind == JsonValueKind.Array)
                .ToDictionary(e => e.Key, e => e.Value.Deserialize<List<KeyEntry>>(Options) ?? []);
        }
        catch (Exception ex)
        {
            logger.Warning($"[Tushonkapedia] keys.json couldn't be read: {ex.Message}");
            return [];
        }
    }
}
