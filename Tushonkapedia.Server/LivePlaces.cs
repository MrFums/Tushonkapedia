using System.Text.Json;

namespace Tushonkapedia.Server;

// Places seen in raids: the game plugin reports, when a raid starts, where every container stands, where every
// lock a key opens is, and where every place a quest can send you is, on that map as the player's game has it (their
// map mods included). Kept in data/places-live.json, map -> what was seen, and preferred over the shipped files
// (box-places.json and friends) for the version of the map this server serves. A map the shipped files don't know
// (a new mod map) is drawn from these alone.
public sealed partial class ItemWiki
{
    public sealed class SeenMap
    {
        // container id -> [x, y, z], and its type when the game said
        public Dictionary<string, float[]> Boxes { get; set; } = [];
        public Dictionary<string, string> Types { get; set; } = [];
        // key id -> the locks it opens; quest place id -> its triggers
        public Dictionary<string, List<float[]>> Locks { get; set; } = [];
        public Dictionary<string, List<float[]>> Zones { get; set; } = [];
        // extract name -> where it is
        public Dictionary<string, List<float[]>> Exits { get; set; } = [];
        public long Seen { get; set; }
        // what the server's own tables said about the map when it was seen (PlacesPrint)
        public string? Print { get; set; }
    }

    // A short hash of what the server's tables say stands on a map: its containers (id and spot) and its extracts.
    // When it changes (a mod moves or adds containers), the plugin is asked to look at the map again; until then
    // it isn't, so a raid normally costs nothing.
    public string PlacesPrint(string map)
    {
        var location = locations.GetDictionary().Values.FirstOrDefault(l => l?.Base?.Id?.Equals(map, StringComparison.OrdinalIgnoreCase) == true);
        if (location == null)
        {
            return "";
        }

        var text = new System.Text.StringBuilder(map);
        foreach (var c in (location.StaticContainers?.Value?.StaticContainers ?? []).Select(c => c.Template?.Id + "@" + System.Text.Json.JsonSerializer.Serialize(c.Template?.Position)).OrderBy(x => x, StringComparer.Ordinal))
        {
            text.Append('|').Append(c);
        }

        foreach (var exit in (location.Base?.Exits ?? []).Select(e => e?.Name ?? "").OrderBy(x => x, StringComparer.Ordinal))
        {
            text.Append("|x:").Append(exit);
        }

        return Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(text.ToString())))[..16].ToLowerInvariant();
    }

    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, string> _placesPrints = new(StringComparer.OrdinalIgnoreCase);

    // whether the plugin should look at this map in the raid that's starting
    public bool PlacesWanted(string map)
    {
        if (string.IsNullOrWhiteSpace(map) || map.Length > 64)
        {
            return false;
        }

        var print = _placesPrints.GetOrAdd(map, PlacesPrint);
        return print.Length > 0 && SeenOn(map)?.Print != print;
    }

    private static readonly JsonSerializerOptions LiveJson = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
    private readonly Lock _liveLock = new();
    private Dictionary<string, SeenMap>? _live;

    private string LiveFile => Path.Combine(folder, "data", "places-live.json");

    private Dictionary<string, SeenMap> Live()
    {
        lock (_liveLock)
        {
            if (_live != null)
            {
                return _live;
            }

            try
            {
                var read = File.Exists(LiveFile) ? JsonSerializer.Deserialize<Dictionary<string, SeenMap>>(File.ReadAllText(LiveFile), LiveJson) : null;
                _live = new Dictionary<string, SeenMap>(read ?? [], StringComparer.OrdinalIgnoreCase);
            }
            catch (Exception ex) when (ex is IOException or JsonException)
            {
                logger.Warning($"[Tushonkapedia] Couldn't read {LiveFile}: {ex.Message}");
                _live = new(StringComparer.OrdinalIgnoreCase);
            }

            return _live;
        }
    }

    private SeenMap? SeenOn(string map) => Live().GetValueOrDefault(map);

    // What the plugin saw in one raid. The map is replaced as a whole (what a raid shows is the map as it is now);
    // only known map ids, and sane numbers, are kept. Returns how many places were taken.
    public int SavePlaces(string map, SeenMap seen)
    {
        if (string.IsNullOrWhiteSpace(map) || map.Length > 64
            || !locations.GetDictionary().Values.Any(l => l?.Base?.Id?.Equals(map, StringComparison.OrdinalIgnoreCase) == true))
        {
            return 0;
        }

        static bool Sane(float[]? p) => p is { Length: 3 } && p.All(v => float.IsFinite(v) && Math.Abs(v) < 20000);
        static bool Id(string? s) => s is { Length: > 0 and <= 64 };
        var clean = new SeenMap
        {
            Boxes = seen.Boxes.Where(b => Id(b.Key) && Sane(b.Value)).Take(20000).ToDictionary(b => b.Key, b => b.Value),
            Types = seen.Types.Where(t => Id(t.Key) && Id(t.Value)).Take(20000).ToDictionary(t => t.Key, t => t.Value),
            Locks = seen.Locks.Where(l => Id(l.Key)).Take(5000).ToDictionary(l => l.Key, l => l.Value.Where(Sane).Take(50).ToList()),
            Zones = seen.Zones.Where(z => Id(z.Key)).Take(5000).ToDictionary(z => z.Key, z => z.Value.Where(Sane).Take(50).ToList()),
            Exits = seen.Exits.Where(e => Id(e.Key)).Take(500).ToDictionary(e => e.Key, e => e.Value.Where(Sane).Take(10).ToList()),
            Seen = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            Print = PlacesPrint(map)
        };
        lock (_liveLock)
        {
            Live()[map] = clean;
            Directory.CreateDirectory(Path.GetDirectoryName(LiveFile)!);
            File.WriteAllText(LiveFile, JsonSerializer.Serialize(Live(), LiveJson));
        }

        // the maps are worked out again with these
        _boxesOn.Clear();
        return clean.Boxes.Count + clean.Locks.Count + clean.Zones.Count + clean.Exits.Count;
    }
}
