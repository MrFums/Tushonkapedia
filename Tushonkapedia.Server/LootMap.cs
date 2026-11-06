using System.Collections.Concurrent;
using SPTarkov.Server.Core.Models.Common;
using Location = SPTarkov.Server.Core.Models.Eft.Common.Location;
using Vector3 = SPTarkov.Server.Core.Models.Eft.Common.Vector3;
using LooseLoot = SPTarkov.Server.Core.Models.Eft.Common.LooseLoot;

namespace Tushonkapedia.Server;

// Where on a map an item can spawn, for the item page's loot map. Read from the map's own loose loot, so modded maps
// and mods that move loot draw as the server has them. Without a drawing of the map, every loot spot and spawn point
// on it is drawn faintly, and together they show its buildings and roads; this item's spots go on top.
//
// Containers: the server's tables name each one but leave its position at 0, 0, 0, so their places come from
// box-places.json, read out of the game's map scenes (see BoxesOn).
//
// SPT parses a map's whole loose loot file again on every read (most of a second and a few hundred MB that .NET keeps
// afterwards), so this is worked out in the loot index's one read and saved to data/loot-maps.bin as plain arrays:
// about 15 MB for 1.8 million item-and-spot pairs. A normal start only loads that file.
public sealed partial class ItemWiki
{
    private const string MapsFile = "loot-maps.bin";
    private const int MapsFormat = 1;

    // One map's loot: per loose point its place and the chance it's used in a raid; per item, the points it can be at
    // and its share of each.
    private sealed record MapLoot(string Key, int[] Outline, float[] X, float[] Y, float[] Z, float[] Chosen,
        Dictionary<string, (int[] Points, float[] Shares)> Items, Dictionary<string, int> Containers);

    private ConcurrentDictionary<string, MapLoot> _mapLoot = new(StringComparer.OrdinalIgnoreCase);

    private MapLoot MapLootOf(Location location, LooseLoot? loose)
    {
        var key = location.Base!.Id!;
        var seen = new HashSet<(int, int)>();
        var outline = new List<int>();
        void Mark(Vector3 p)
        {
            var at = ((int)MathF.Round(p.X), (int)MathF.Round(p.Z));
            if (seen.Add(at))
            {
                outline.Add(at.Item1);
                outline.Add(at.Item2);
            }
        }

        var xs = new List<float>();
        var ys = new List<float>();
        var zs = new List<float>();
        var chosenAt = new List<float>();
        var byItem = new Dictionary<string, (List<int> Points, List<float> Shares)>();
        void Add(Vector3 p, double chosen, Dictionary<string, double> shares)
        {
            var at = xs.Count;
            xs.Add(p.X);
            ys.Add(p.Y);
            zs.Add(p.Z);
            chosenAt.Add((float)Math.Min(1, chosen));
            foreach (var (item, share) in shares)
            {
                if (!byItem.TryGetValue(item, out var list))
                {
                    byItem[item] = list = ([], []);
                }

                list.Points.Add(at);
                list.Shares.Add((float)share);
            }
        }

        var spawnpoints = loose?.Spawnpoints?.ToList() ?? [];
        var multiplier = Multiplier(locationConfig.LooseLootMultiplier, key);
        var mean = loose?.SpawnpointCount?.Mean ?? spawnpoints.Count;
        var sum = spawnpoints.Sum(s => s.Probability ?? 0);
        foreach (var point in spawnpoints)
        {
            if (point.Template?.Position is not { } p)
            {
                continue;
            }

            Mark(p);
            // a spawn point's items by their key; a mod may list one key twice, the first one counts
            var items = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var i in point.Template.Items ?? [])
            {
                if (i.ComposedKey != null)
                {
                    items.TryAdd(i.ComposedKey, i.Template.ToString());
                }
            }
            var weights = (point.ItemDistribution ?? []).Where(d => d.ComposedKey?.Key != null && items.ContainsKey(d.ComposedKey.Key)).ToList();
            var total = weights.Sum(w => w.RelativeProbability ?? 0);
            var chosen = sum > 0 ? Math.Min(1, mean * (point.Probability ?? 0) / sum) * multiplier : 0;
            if (total <= 0 || chosen <= 0)
            {
                continue;
            }

            var shares = new Dictionary<string, double>();
            foreach (var weight in weights)
            {
                var item = items[weight.ComposedKey!.Key!];
                shares[item] = shares.GetValueOrDefault(item) + (weight.RelativeProbability ?? 0) / total;
            }

            Add(p, chosen, shares);
        }

        foreach (var point in loose?.SpawnpointsForced ?? [])
        {
            var root = point.Template?.Items?.FirstOrDefault(i => string.IsNullOrEmpty(i.ParentId));
            if (root != null && point.Template!.Position is { } p)
            {
                Mark(p);
                Add(p, 1, new Dictionary<string, double> { [root.Template.ToString()] = 1 });
            }
        }

        // where players and bots spawn fills in the open ground between the loot
        foreach (var spawn in location.Base.SpawnPointParams ?? [])
        {
            if (spawn.Position is { } p)
            {
                Mark(p);
            }
        }

        // how many containers of each kind stand on the map (Christmas ones only in season)
        var containers = new Dictionary<string, int>();
        foreach (var container in location.StaticContainers?.Value?.StaticContainers ?? [])
        {
            var type = container.Template?.Items?.FirstOrDefault()?.Template.ToString();
            if (type != null && !(container.Template?.Id is { } placed && offSeason(placed)))
            {
                containers[type] = containers.GetValueOrDefault(type) + 1;
            }
        }

        return new MapLoot(key, outline.ToArray(), xs.ToArray(), ys.ToArray(), zs.ToArray(), chosenAt.ToArray(),
            byItem.ToDictionary(i => i.Key, i => (i.Value.Points.ToArray(), i.Value.Shares.ToArray())), containers);
    }

    private void SaveMapLoot(IEnumerable<MapLoot> maps, string fingerprint)
    {
        var file = Path.Combine(folder, "data", MapsFile);
        using (var writer = new BinaryWriter(File.Create(file + ".tmp")))
        {
            writer.Write(MapsFormat);
            writer.Write(fingerprint);
            var list = maps.ToList();
            writer.Write(list.Count);
            foreach (var map in list)
            {
                writer.Write(map.Key);
                Ints(writer, map.Outline);
                Floats(writer, map.X);
                Floats(writer, map.Y);
                Floats(writer, map.Z);
                Floats(writer, map.Chosen);
                writer.Write(map.Containers.Count);
                foreach (var (type, count) in map.Containers)
                {
                    writer.Write(type);
                    writer.Write(count);
                }

                writer.Write(map.Items.Count);
                foreach (var (item, (points, shares)) in map.Items)
                {
                    writer.Write(item);
                    Ints(writer, points);
                    Floats(writer, shares);
                }
            }
        }

        File.Move(file + ".tmp", file, true);
    }

    // the saved maps, if they were worked out from the same loot tables (same fingerprint) as now
    private Dictionary<string, MapLoot>? ReadMapLoot(string fingerprint)
    {
        try
        {
            var file = Path.Combine(folder, "data", MapsFile);
            if (!File.Exists(file))
            {
                return null;
            }

            using var reader = new BinaryReader(new BufferedStream(File.OpenRead(file), 1 << 16));
            if (reader.ReadInt32() != MapsFormat || reader.ReadString() != fingerprint)
            {
                return null;
            }

            var maps = new Dictionary<string, MapLoot>(StringComparer.OrdinalIgnoreCase);
            for (var m = reader.ReadInt32(); m > 0; m--)
            {
                var key = reader.ReadString();
                var outline = Ints(reader);
                var (x, y, z, chosen) = (Floats(reader), Floats(reader), Floats(reader), Floats(reader));
                var containers = new Dictionary<string, int>();
                for (var c = reader.ReadInt32(); c > 0; c--)
                {
                    containers[reader.ReadString()] = reader.ReadInt32();
                }

                var count = reader.ReadInt32();
                var items = new Dictionary<string, (int[], float[])>(count);
                for (var i = 0; i < count; i++)
                {
                    items[reader.ReadString()] = (Ints(reader), Floats(reader));
                }

                maps[key] = new MapLoot(key, outline, x, y, z, chosen, items, containers);
            }

            return maps;
        }
        catch (Exception ex) when (ex is IOException or EndOfStreamException or FormatException)
        {
            return null;
        }
    }

    private static void Ints(BinaryWriter writer, int[] values)
    {
        writer.Write(values.Length);
        writer.Write(System.Runtime.InteropServices.MemoryMarshal.AsBytes(values.AsSpan()));
    }

    private static void Floats(BinaryWriter writer, float[] values)
    {
        writer.Write(values.Length);
        writer.Write(System.Runtime.InteropServices.MemoryMarshal.AsBytes(values.AsSpan()));
    }

    private static int[] Ints(BinaryReader reader)
    {
        var values = new int[reader.ReadInt32()];
        reader.BaseStream.ReadExactly(System.Runtime.InteropServices.MemoryMarshal.AsBytes(values.AsSpan()));
        return values;
    }

    private static float[] Floats(BinaryReader reader)
    {
        var values = new float[reader.ReadInt32()];
        reader.BaseStream.ReadExactly(System.Runtime.InteropServices.MemoryMarshal.AsBytes(values.AsSpan()));
        return values;
    }

    // a map's name in this language, as the game's own tables have it ("Customs"), or null for no such map
    public string? LocationName(string map, IReadOnlyDictionary<string, string> locale)
    {
        foreach (var (_, location) in locations.GetDictionary())
        {
            if (location?.Base?.Id != null && location.Base.Id.Equals(map, StringComparison.OrdinalIgnoreCase))
            {
                return locale.GetValueOrDefault(location.Base.IdField + " Name") ?? location.Base.Id;
            }
        }

        return null;
    }

    // Where each container stands, read once from box-places.json beside the mod: the server's tables leave every
    // position at 0, 0, 0, so tools/box_places.py takes them from map scenes, the game's own and any a client mod
    // brings (a backported map). Per map: each container's type, and one set of positions per source of scenes
    // ("game" or a client mod's folder). A set about as big as the biggest is a whole version of the map (the SPT one,
    // a 1.0 backport); a small one adds rooms to it. The whole version holding the most of this server's containers is
    // the one it serves, and the map shows it unless the viewer picks another. Containers the version lacks come from
    // the add-ons. A container no scene holds goes undrawn.
    private sealed class BoxPlaces
    {
        public Dictionary<string, string> Types { get; init; } = [];
        public Dictionary<string, Dictionary<string, float[]>> Sets { get; init; } = [];
    }

    private sealed record MapBoxes((string Type, float X, float Y, float Z)[] Boxes, string? Version, string[] Versions);

    private Dictionary<string, BoxPlaces>? _boxPlaces;
    private readonly ConcurrentDictionary<string, MapBoxes> _boxesOn = new(StringComparer.OrdinalIgnoreCase);

    private MapBoxes BoxesOn(string map, string? version = null)
    {
        return _boxesOn.GetOrAdd(map + "|" + version, _ =>
        {
            if (_boxPlaces == null)
            {
                var file = Path.Combine(folder, "box-places.json");
                try
                {
                    var read = File.Exists(file) ? System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, BoxPlaces>>(File.ReadAllText(file), Json) : null;
                    _boxPlaces = new Dictionary<string, BoxPlaces>(read ?? [], StringComparer.OrdinalIgnoreCase);
                }
                catch (Exception ex) when (ex is IOException or System.Text.Json.JsonException)
                {
                    logger.Warning($"[Tushonkapedia] Couldn't read {file}: {ex.Message}");
                    _boxPlaces = new(StringComparer.OrdinalIgnoreCase);
                }
            }

            var location = locations.GetDictionary().Values.FirstOrDefault(l => l?.Base?.Id?.Equals(map, StringComparison.OrdinalIgnoreCase) == true);
            var here = (location?.StaticContainers?.Value?.StaticContainers ?? [])
                .Select(c => (Id: c.Template?.Id, Type: c.Template?.Items?.FirstOrDefault()?.Template.ToString()))
                .Where(c => c.Id != null && c.Type != null && !offSeason(c.Id))
                .ToList();
            // what raids on this map showed: preferred for the version the server serves
            var seen = SeenOn(map);
            if (location == null || !_boxPlaces.TryGetValue(map, out var places) || places.Sets.Count == 0)
            {
                if (location == null || seen == null)
                {
                    return new MapBoxes([], null, []);
                }

                var types = here.ToDictionary(c => c.Id!, c => c.Type!);
                return new MapBoxes(seen.Boxes.Where(b => !offSeason(b.Key) && (types.ContainsKey(b.Key) || seen.Types.ContainsKey(b.Key)))
                    .Select(b => (types.GetValueOrDefault(b.Key) ?? seen.Types[b.Key], b.Value[0], b.Value[1], b.Value[2])).ToArray(), null, []);
            }

            var biggest = places.Sets.Values.Max(s => s.Count);
            // whole versions, the served one first
            var whole = places.Sets.Where(s => s.Value.Count * 2 >= biggest)
                .OrderByDescending(s => here.Count(c => s.Value.ContainsKey(c.Id!))).Select(s => s.Key).ToArray();
            var addOns = places.Sets.Where(s => !whole.Contains(s.Key)).Select(s => s.Value).ToList();
            var shown = version != null && whole.Contains(version) ? version : whole[0];
            var sets = addOns.Prepend(places.Sets[shown]).ToList();

            var list = new List<(string, float, float, float)>();
            void Add(string id, string type)
            {
                if (shown == whole[0] && seen != null && seen.Boxes.TryGetValue(id, out var live))
                {
                    list.Add((type, live[0], live[1], live[2]));
                }
                else if (sets.Select(s => s.GetValueOrDefault(id)).FirstOrDefault(x => x is { Length: 3 }) is { } p)
                {
                    list.Add((type, p[0], p[1], p[2]));
                }
            }

            if (shown == whole[0])
            {
                // the version this server serves: its own containers
                foreach (var (id, type) in here)
                {
                    Add(id!, type!);
                }
            }
            else
            {
                // another version: the containers its scenes hold, as the file has their types
                var live = here.ToDictionary(c => c.Id!, c => c.Type!);
                foreach (var id in sets.SelectMany(s => s.Keys).Distinct())
                {
                    if (!offSeason(id) && (places.Types.GetValueOrDefault(id) ?? live.GetValueOrDefault(id)) is { } type)
                    {
                        Add(id, type);
                    }
                }
            }

            return new MapBoxes(list.ToArray(), shown, whole.Length > 1 ? whole : []);
        });
    }

    // The outline of SPT's own version of a map: every loose loot point and spawn point in its files, and where its
    // containers stand. Worked out once; the loose loot file (tens of MB) is read as a stream and let go.
    private readonly ConcurrentDictionary<string, int[]> _stockOutline = new(StringComparer.OrdinalIgnoreCase);

    private int[] StockOutline(string map) => _stockOutline.GetOrAdd(map, key =>
    {
        var seen = new HashSet<(int, int)>();
        var outline = new List<int>();
        void Mark(double x, double z)
        {
            if (seen.Add(((int)Math.Round(x), (int)Math.Round(z))))
            {
                outline.Add((int)Math.Round(x));
                outline.Add((int)Math.Round(z));
            }
        }

        foreach (var b in BoxesOn(key, "game").Boxes)
        {
            Mark(b.X, b.Z);
        }

        foreach (var p in Stock(key, "base.json")?["SpawnPointParams"] as System.Text.Json.Nodes.JsonArray ?? [])
        {
            if (Num(p?["Position"]?["x"]) is { } x && Num(p?["Position"]?["z"]) is { } z)
            {
                Mark(x, z);
            }
        }

        foreach (var root in new[] { AppContext.BaseDirectory, Directory.GetCurrentDirectory() })
        {
            var path = Path.Combine(root, "SPT_Data", "database", "locations", key.ToLowerInvariant(), "looseLoot.json");
            if (!File.Exists(path))
            {
                continue;
            }

            try
            {
                // every "Position": { "x", "y", "z" } in the file, without building the whole document
                var reader = new System.Text.Json.Utf8JsonReader(File.ReadAllBytes(path));
                double? x = null;
                var inPosition = 0;
                string? property = null;
                while (reader.Read())
                {
                    switch (reader.TokenType)
                    {
                        case System.Text.Json.JsonTokenType.PropertyName:
                            property = reader.GetString();
                            break;
                        case System.Text.Json.JsonTokenType.StartObject when property == "Position":
                            inPosition = reader.CurrentDepth;
                            x = null;
                            break;
                        case System.Text.Json.JsonTokenType.EndObject when inPosition == reader.CurrentDepth:
                            inPosition = 0;
                            break;
                        case System.Text.Json.JsonTokenType.Number when inPosition > 0 && property == "x":
                            x = reader.GetDouble();
                            break;
                        case System.Text.Json.JsonTokenType.Number when inPosition > 0 && property == "z" && x is { } px:
                            Mark(px, reader.GetDouble());
                            break;
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or System.Text.Json.JsonException)
            {
                logger.Warning($"[Tushonkapedia] Couldn't read {path}: {ex.Message}");
            }

            break;
        }

        return outline.ToArray();
    });

    // Places read from the map scenes by tools/box_places.py, each file read once: key-places.json (every door or lock
    // a key opens) and zone-places.json (every place a quest sends you). id -> map -> source of scenes -> [x, y, z] of
    // each. A map shows the places in the version it shows (see BoxesOn), or of any source when that version has none
    // (a mod's added rooms).
    private readonly ConcurrentDictionary<string, Dictionary<string, Dictionary<string, Dictionary<string, float[][]>>>> _places = new();

    private Dictionary<string, Dictionary<string, Dictionary<string, float[][]>>> Places(string name) => _places.GetOrAdd(name, _ =>
    {
        var file = Path.Combine(folder, name);
        try
        {
            var read = File.Exists(file) ? System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, Dictionary<string, Dictionary<string, float[][]>>>>(File.ReadAllText(file)) : null;
            return new Dictionary<string, Dictionary<string, Dictionary<string, float[][]>>>(read ?? [], StringComparer.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is IOException or System.Text.Json.JsonException)
        {
            logger.Warning($"[Tushonkapedia] Couldn't read {file}: {ex.Message}");
            return new(StringComparer.OrdinalIgnoreCase);
        }
    });

    private Dictionary<string, Dictionary<string, Dictionary<string, float[][]>>> KeyPlaces() => Places("key-places.json");

    private float[][] PlacesOn(string file, string id, string map, string? version = null)
    {
        var seen = SeenOn(map);
        var live = file switch
        {
            "key-places.json" => seen?.Locks.GetValueOrDefault(id),
            "exit-places.json" => seen?.Exits.GetValueOrDefault(id),
            _ => seen?.Zones.GetValueOrDefault(id)
        };
        if (live is { Count: > 0 } && BoxesOn(map, version).Version == BoxesOn(map).Version)
        {
            return live.ToArray();
        }

        if (!Places(file).TryGetValue(id, out var maps) || maps.FirstOrDefault(m => m.Key.Equals(map, StringComparison.OrdinalIgnoreCase)).Value is not { } sources)
        {
            return [];
        }

        var shown = BoxesOn(map, version).Version;
        return shown != null && sources.TryGetValue(shown, out var own) ? own : sources.Values.SelectMany(v => v).ToArray();
    }

    private float[][] LocksOf(string key, string map, string? version = null) => PlacesOn("key-places.json", key, map, version);

    // how close, along the ground and up or down, a lock has to be to a quest place to count as near it
    private const double NearLock = 15;
    private const double NearLockHeight = 3;

    // Every place a file has for an id on any map: the shipped ones and those seen in raids.
    private IEnumerable<(string Map, float[] At)> AllPlaces(string file, string id)
    {
        foreach (var (map, sources) in Places(file).GetValueOrDefault(id) ?? [])
        {
            foreach (var at in sources.Values.SelectMany(p => p))
            {
                yield return (map, at);
            }
        }

        foreach (var (map, seen) in Live())
        {
            foreach (var at in (file == "key-places.json" ? seen.Locks : seen.Zones).GetValueOrDefault(id) ?? [])
            {
                yield return (map, at);
            }
        }
    }

    public List<string> ZoneMaps(IEnumerable<string> zones) =>
        zones.SelectMany(z => AllPlaces("zone-places.json", z)).Select(p => p.Map).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

    // The keys with a lock close to any of these quest places, nearest first.
    public List<(string Key, double Metres)> LocksNear(IReadOnlyList<string> zones)
    {
        var places = zones.SelectMany(z => AllPlaces("zone-places.json", z)).ToList();
        if (places.Count == 0)
        {
            return [];
        }

        var best = new Dictionary<string, double>();
        foreach (var key in KeyPlaces().Keys.Concat(Live().Values.SelectMany(m => m.Locks.Keys)).Distinct())
        {
            foreach (var (map, at) in AllPlaces("key-places.json", key))
            {
                foreach (var place in places.Where(p => p.Map.Equals(map, StringComparison.OrdinalIgnoreCase) && Math.Abs(p.At[1] - at[1]) <= NearLockHeight))
                {
                    var metres = Math.Sqrt(Math.Pow(place.At[0] - at[0], 2) + Math.Pow(place.At[2] - at[2], 2));
                    if (metres <= NearLock && metres < best.GetValueOrDefault(key, double.MaxValue))
                    {
                        best[key] = metres;
                    }
                }
            }
        }

        return best.OrderBy(b => b.Value).Select(b => (b.Key, b.Value)).ToList();
    }

    // a key's maps: where its locks are, by the server's map ids, with how many locks on each
    public object[] KeyOpens(string key, IReadOnlyDictionary<string, string> locale)
    {
        if (!IsKey(key))
        {
            return [];
        }

        var mapIds = (KeyPlaces().GetValueOrDefault(key)?.Keys ?? Enumerable.Empty<string>())
            .Concat(Live().Where(m => m.Value.Locks.ContainsKey(key)).Select(m => m.Key))
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        return mapIds
            .Select(m => locations.GetDictionary().Values.FirstOrDefault(l => l?.Base?.Id?.Equals(m, StringComparison.OrdinalIgnoreCase) == true)?.Base)
            .Where(b => b?.Id != null)
            .Select(b => (Id: b!.Id!, Count: LocksOf(key, b.Id!).Length, Name: locale.GetValueOrDefault(b.IdField + " Name") ?? b.Name ?? b.Id!))
            .Where(m => m.Count > 0)
            .Select(m => (object)new { id = m.Id, name = m.Name, locks = m.Count })
            .ToArray();
    }

    // Every key's id, for tools/box_places.py to look for on the doors in the map scenes.
    public string[] KeyIds() => templates.Items.Keys.Select(k => k.ToString()).Where(IsKey).ToArray();

    // Every map's containers (id -> type) as the server has them after every mod has loaded, for
    // tools/box_places.py to look for in the map scenes.
    public Dictionary<string, Dictionary<string, string>> BoxIds()
    {
        var ids = new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var (_, location) in locations.GetDictionary())
        {
            if (location?.Base?.Id is { } key)
            {
                ids[key] = (location.StaticContainers?.Value?.StaticContainers ?? [])
                    .Where(c => c.Template?.Id != null && c.Template.Items?.FirstOrDefault() != null)
                    .DistinctBy(c => c.Template!.Id)
                    .ToDictionary(c => c.Template!.Id!, c => c.Template!.Items!.First().Template.ToString());
            }
        }

        return ids;
    }

    // the versions of a map the viewer can pick between ("game" is the SPT one; anything else a mod's, the 1.0 one)
    private static object? VersionsOf(MapBoxes m) =>
        m.Versions.Length < 2 ? null : m.Versions.Select(v => new { id = v, name = v == "game" ? "SPT" : "1.0", on = v == m.Version }).OrderBy(v => v.name);

    // This item's loose spots on one map (none for no item: the map page shows the map alone), each with the chance
    // it's there in a raid, and the containers that can hold it, placed where they stand. With box (container types,
    // comma separated) instead of an item: where those containers stand. Null until the loot index is ready.
    public object? LootMap(string id, string map, IReadOnlyDictionary<string, string> locale, string box, string? version, object? art, string door = "", string zone = "")
    {
        var types = box.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Take(8).ToHashSet();
        if ((id != "" && !MongoId.IsValidMongoId(id)) || types.Any(t => !MongoId.IsValidMongoId(t)) || string.IsNullOrWhiteSpace(map) || map.Length > 64
            || !_mapLoot.TryGetValue(map, out var loot))
        {
            return null;
        }

        // SPT's own version of a map a mod has replaced: tarkov.dev's drawing is of the 1.0 map, but the ground is the
        // same, so it's still drawn (an outline of loot dots alone is unreadable); the page says so. The item's loose
        // spots aren't worked out for it.
        var stock = StockVersion(loot.Key, version);
        var outline = stock ? StockOutline(loot.Key) : loot.Outline;

        // where a quest sends you: the places (zones) its objective names, on this map
        if (zone != "")
        {
            var places = zone.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Take(8)
                .SelectMany(z => PlacesOn("zone-places.json", z, loot.Key, version))
                .Select(p => new { x = Math.Round(p[0], 1), y = Math.Round(p[1], 1), z = Math.Round(p[2], 1), name = "The place", chance = (double?)null })
                .ToList();
            return new { map = loot.Key, outline, spots = Array.Empty<object>(), containers = Array.Empty<object>(), boxes = places, zones = true,
                versions = VersionsOf(BoxesOn(loot.Key, version)), stock, art };
        }

        // what a key opens: its locks on this map
        if (door != "" && IsKey(door))
        {
            var locks = LocksOf(door, loot.Key, version)
                .Select(p => new { x = Math.Round(p[0], 1), y = Math.Round(p[1], 1), z = Math.Round(p[2], 1), name = "What it opens", chance = (double?)null })
                .ToList();
            return new { map = loot.Key, outline, spots = Array.Empty<object>(), containers = Array.Empty<object>(), boxes = locks, locks = true,
                versions = VersionsOf(BoxesOn(loot.Key, version)), stock, art };
        }

        if (types.Count > 0)
        {
            var name = locale.GetValueOrDefault(types.First() + " Name") ?? types.First();
            var shown = BoxesOn(loot.Key, version);
            var placedBoxes = shown.Boxes.Where(b => types.Contains(b.Type))
                .Select(b => new { x = Math.Round(b.X, 1), y = Math.Round(b.Y, 1), z = Math.Round(b.Z, 1), name, chance = (double?)null })
                .ToList();
            return new { map = loot.Key, outline, spots = Array.Empty<object>(), containers = Array.Empty<object>(), boxes = placedBoxes, versions = VersionsOf(shown), stock, art };
        }

        // the map alone: its bosses, PMC starts and extracts on it
        if (id == "")
        {
            return new { map = loot.Key, outline, spots = Array.Empty<object>(), containers = Array.Empty<object>(), boxes = Array.Empty<object>(),
                points = MapPoints(loot.Key, version, locale), versions = VersionsOf(BoxesOn(loot.Key, version)), stock, art };
        }

        var spots = new List<object>();
        if (!stock && loot.Items.TryGetValue(id, out var at))
        {
            for (var i = 0; i < at.Points.Length; i++)
            {
                var p = at.Points[i];
                spots.Add(new { x = Math.Round(loot.X[p], 1), y = Math.Round(loot.Y[p], 1), z = Math.Round(loot.Z[p], 1), chance = Math.Round(Math.Min(1, loot.Chosen[p] * at.Shares[i]), 4) });
            }
        }

        // the kinds of container whose loot this item is in, from the loot index (already worked out)
        var spot = _loot?.Items.GetValueOrDefault(id)?.FirstOrDefault(s => s.Location.Equals(loot.Key, StringComparison.OrdinalIgnoreCase));
        // each container that can hold it, with the copies one holds in a raid on average (its own spawn chance in)
        var holds = (spot?.Containers ?? []).Where(c => c.Key != "forced" && loot.Containers.GetValueOrDefault(c.Key) > 0)
            .ToDictionary(c => c.Key, c => c.Value / loot.Containers[c.Key]);
        var placed = BoxesOn(loot.Key, version);
        var boxes = placed.Boxes.Where(b => holds.ContainsKey(b.Type))
            .Select(b => new { x = Math.Round(b.X, 1), y = Math.Round(b.Y, 1), z = Math.Round(b.Z, 1), name = locale.GetValueOrDefault(b.Type + " Name") ?? b.Type, chance = (double?)Math.Round(Math.Min(1, holds[b.Type]), 4) })
            .ToList();
        var drawn = BoxesOn(loot.Key).Boxes.Select(b => b.Type).ToHashSet();

        // the rest, not drawn: kinds this map has no positions for (a mod's), and set containers
        var containers = (spot?.Containers.Keys ?? Enumerable.Empty<string>())
            .Where(type => !drawn.Contains(type))
            .Select(type => type == "forced"
                ? new { name = "Always in a set container", count = 1 }
                : new { name = locale.GetValueOrDefault(type + " Name") ?? type, count = loot.Containers.GetValueOrDefault(type) })
            .Where(c => c.count > 0)
            .OrderByDescending(c => c.count)
            .ToList();

        return new { map = loot.Key, outline, spots, containers, boxes, versions = VersionsOf(placed), stock, art };
    }
}
