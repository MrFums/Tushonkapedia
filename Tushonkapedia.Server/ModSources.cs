using System.Text.Json;
using System.Text.RegularExpressions;
using SPTarkov.Server.Core.Models.Common;
using Quest = SPTarkov.Server.Core.Models.Eft.Common.Tables.Quest;

namespace Tushonkapedia.Server;

// Which mod added a quest or an item. Anything not in SPT's own database files was added by a mod; to name the
// mod, each mod folder's JSON files are searched for the id (most mods ship their quests and items as JSON). Plenty
// of mods only mention other mods' items (price tables, blacklists), so a file that defines the id beats one that
// mentions it, and among those the mod that mentions the fewest ids: the one the item is most likely to belong to.
// A mod that builds them in code can't be named this way, so it just says "a mod".
public sealed partial class ItemWiki
{
    // bigger files are loot tables, which only mention items, never add them
    private const long LargestModFile = 20_000_000;

    private HashSet<string>? _baseQuests;
    private Dictionary<string, string>? _modOf;
    private Dictionary<string, string>? _botMods;
    private Dictionary<string, HashSet<string>>? _baseZones;
    // values a mod can change on every item without it mattering to anyone reading the item's page
    private static readonly HashSet<string> QuietFields = ["LootExperience", "ExamineExperience", "ExamineTime", "CanSellOnRagfair", "CanRequireOnRagfair"];
    private JsonDocument? _baseItems;
    private JsonDocument? _baseQuestFile;

    public bool IsBaseQuest(string id)
    {
        _baseQuests ??= Ids(Path.Combine(AppContext.BaseDirectory, "SPT_Data", "database", "templates", "quests.json"));
        return _baseQuests.Count == 0 || _baseQuests.Contains(id);
    }

    // Whether a quest sends you somewhere SPT's own version of it doesn't: a mod changed it, or added it. A quest
    // that only lost objectives (Revision - Reserve on some servers has no Stryker or T-90 steps) still sends you to
    // the same places, so the keys listed for it still apply.
    public bool QuestMoved(string id, IEnumerable<string> zones)
    {
        lock (_lock)
        {
            _baseZones ??= BaseZones();
        }

        return !_baseZones.TryGetValue(id, out var own) || !zones.All(own.Contains);
    }

    private Dictionary<string, HashSet<string>> BaseZones()
    {
        var found = new Dictionary<string, HashSet<string>>();
        try
        {
            using var stream = File.OpenRead(Path.Combine(AppContext.BaseDirectory, "SPT_Data", "database", "templates", "quests.json"));
            using var json = JsonDocument.Parse(stream);
            foreach (var quest in json.RootElement.EnumerateObject())
            {
                var zones = found[quest.Name] = [];
                if (!quest.Value.TryGetProperty("conditions", out var conditions) || !conditions.TryGetProperty("AvailableForFinish", out var finish))
                {
                    continue;
                }

                foreach (var condition in finish.EnumerateArray())
                {
                    AddZones(condition, zones);
                    if (condition.TryGetProperty("counter", out var counter) && counter.TryGetProperty("conditions", out var parts))
                    {
                        foreach (var part in parts.EnumerateArray())
                        {
                            AddZones(part, zones);
                        }
                    }
                }
            }
        }
        catch (Exception ex) when (ex is IOException or JsonException)
        {
            logger.Warning($"[Tushonkapedia] Couldn't read SPT's quests, so moved objectives aren't spotted: {ex.Message}");
        }

        return found;
    }

    private static void AddZones(JsonElement condition, HashSet<string> zones)
    {
        if (condition.TryGetProperty("zoneId", out var zone) && zone.ValueKind == JsonValueKind.String && zone.GetString() is { Length: > 0 } one)
        {
            zones.Add(one);
        }

        if (condition.TryGetProperty("zoneIds", out var many) && many.ValueKind == JsonValueKind.Array)
        {
            zones.UnionWith(many.EnumerateArray().Select(z => z.ToString()).Where(z => z.Length > 0));
        }

        if (condition.TryGetProperty("conditionType", out var type) && type.GetString() == "VisitPlace" && condition.TryGetProperty("target", out var target))
        {
            zones.UnionWith(target.ValueKind == JsonValueKind.Array ? target.EnumerateArray().Select(z => z.ToString()) : [target.ToString()]);
        }
    }

    // What a mod changed about a game item, against SPT's own files: the plain values that differ.
    public List<string> ItemChanges(string id)
    {
        if (!BaseGame(id) || !MongoId.IsValidMongoId(id) || !templates.Items.TryGetValue(new MongoId(id), out var item))
        {
            return [];
        }

        lock (_lock)
        {
            _baseItems ??= OpenBase("items.json");
        }

        if (_baseItems == null || !_baseItems.RootElement.TryGetProperty(id, out var own) || !own.TryGetProperty("_props", out var props))
        {
            return [];
        }

        var live = PropsOf(item.Properties);
        var changed = new List<string>();
        foreach (var field in props.EnumerateObject().Where(f => !QuietFields.Contains(f.Name) && f.Value.ValueKind is JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False))
        {
            if (!live.TryGetValue(field.Name, out var now) || now == null)
            {
                continue;
            }

            var same = field.Value.ValueKind == JsonValueKind.Number
                ? Math.Abs(field.Value.GetDouble() - (double.TryParse(now.ToString(), out var n) ? n : double.NaN)) < 1e-4 * Math.Max(1, Math.Abs(field.Value.GetDouble()))
                : now.ToString().Equals(field.Value.GetBoolean().ToString(), StringComparison.OrdinalIgnoreCase);
            if (!same)
            {
                changed.Add(field.Name);
            }
        }

        return changed;
    }

    // Whether a mod changed a game quest: what it asks for or what it gives, against SPT's own files.
    public bool QuestChanged(Quest live)
    {
        lock (_lock)
        {
            _baseQuestFile ??= OpenBase("quests.json");
        }

        if (_baseQuestFile == null || !_baseQuestFile.RootElement.TryGetProperty(live.Id.ToString(), out var own))
        {
            return false;
        }

        var wanted = own.TryGetProperty("conditions", out var conditions) && conditions.TryGetProperty("AvailableForFinish", out var finish)
            ? finish.EnumerateArray().Select(c => $"{(c.TryGetProperty("id", out var i) ? i.ToString() : "")}={(c.TryGetProperty("value", out var v) ? v.ToString() : "")}").Order().ToList() : [];
        var asked = (live.Conditions?.AvailableForFinish ?? []).Where(c => c != null).Select(c => $"{c.Id}={c.Value}").Order().ToList();
        var given = own.TryGetProperty("rewards", out var rewards) && rewards.TryGetProperty("Success", out var success) ? success.GetArrayLength() : -1;
        var gives = live.Rewards?.GetValueOrDefault("Success")?.Count ?? -1;
        return !wanted.SequenceEqual(asked) || (given >= 0 && gives >= 0 && given != gives);
    }

    private JsonDocument? OpenBase(string file)
    {
        try
        {
            using var stream = File.OpenRead(Path.Combine(AppContext.BaseDirectory, "SPT_Data", "database", "templates", file));
            return JsonDocument.Parse(stream);
        }
        catch (Exception ex) when (ex is IOException or JsonException)
        {
            logger.Warning($"[Tushonkapedia] Couldn't read SPT's {file}, so what mods change isn't marked: {ex.Message}");
            return null;
        }
    }

    // the mod folder that mentions the id first, or "" when it's modded but no folder mentions it; null for base game
    public string? ModOf(string id, bool quest)
    {
        if (quest ? IsBaseQuest(id) : BaseGame(id))
        {
            return null;
        }

        // an item whose model ships in a mod's bundle is that mod's, even when the mod adds it in code (no JSON to find)
        if (!quest && BundleMod != null && MongoId.IsValidMongoId(id) && templates.Items.TryGetValue(new MongoId(id), out var item)
            && item.Properties?.Prefab?.Path is { Length: > 0 } prefab && BundleMod(prefab) is { Length: > 0 } shipped)
        {
            return shipped;
        }

        lock (_lock)
        {
            _modOf ??= ScanMods();
        }

        return _modOf.GetValueOrDefault(id, "");
    }

    // the mod folder whose bundles hold this model path, from SPT's own bundle loader; null when no mod ships it
    public Func<string, string?>? BundleMod { get; set; }

    // The mod folder with a bot file named after this role, or null. Mods ship a bot as <role>.json, whatever the mod.
    public string? BotMod(string role)
    {
        lock (_lock)
        {
            _botMods ??= ScanBotFiles();
        }

        // a family of bots can share one file: blackDiv.json for blackdivbreacher
        return _botMods.GetValueOrDefault(role)
            ?? _botMods.Where(f => f.Key.Length >= 6 && role.StartsWith(f.Key, StringComparison.OrdinalIgnoreCase)).OrderByDescending(f => f.Key.Length).Select(f => f.Value).FirstOrDefault();
    }

    private Dictionary<string, string> ScanBotFiles()
    {
        var found = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var mods = Path.GetDirectoryName(folder.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        if (mods == null || !Directory.Exists(mods))
        {
            return found;
        }

        var self = Path.GetFullPath(folder).TrimEnd(Path.DirectorySeparatorChar);
        foreach (var mod in Directory.GetDirectories(mods).OrderBy(d => d, StringComparer.OrdinalIgnoreCase))
        {
            if (Path.GetFullPath(mod).TrimEnd(Path.DirectorySeparatorChar).Equals(self, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            foreach (var file in Directory.EnumerateFiles(mod, "*.json*", SearchOption.AllDirectories))
            {
                found.TryAdd(Path.GetFileNameWithoutExtension(file), Path.GetFileName(mod));
            }
        }

        return found;
    }

    private Dictionary<string, string> ScanMods()
    {
        var found = new Dictionary<string, string>();
        var mods = Path.GetDirectoryName(folder.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        if (mods == null || !Directory.Exists(mods))
        {
            return found;
        }

        // mod folder -> id -> whether it defines it
        var seen = new Dictionary<string, Dictionary<string, bool>>();
        var wanted = templates.Quests.Keys.Select(k => k.ToString()).Where(id => !IsBaseQuest(id))
            .Concat(templates.Items.Keys.Select(k => k.ToString()).Where(id => !BaseGame(id)))
            .ToHashSet();
        var self = Path.GetFullPath(folder).TrimEnd(Path.DirectorySeparatorChar);
        foreach (var mod in Directory.GetDirectories(mods).OrderBy(d => d, StringComparer.OrdinalIgnoreCase))
        {
            if (Path.GetFullPath(mod).TrimEnd(Path.DirectorySeparatorChar).Equals(self, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var name = Path.GetFileName(mod);
            var here = seen[name] = [];
            try
            {
                foreach (var file in Directory.EnumerateFiles(mod, "*.json*", SearchOption.AllDirectories))
                {
                    if (new FileInfo(file).Length > LargestModFile)
                    {
                        continue;
                    }

                    var text = File.ReadAllText(file);
                    foreach (Match match in HexId().Matches(text))
                    {
                        if (wanted.Contains(match.Value))
                        {
                            here[match.Value] = here.GetValueOrDefault(match.Value) || Defines(text, match.Index);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                logger.Warning($"[Tushonkapedia] Couldn't look through {name} for its quests and items: {ex.Message}");
            }
        }

        // a mod that mentions most of what's modded is a table about items, not where they come from
        var owners = seen.Where(m => wanted.Count < 100 || m.Value.Count * 2 <= wanted.Count).OrderBy(m => m.Value.Count).ToList();
        foreach (var id in wanted)
        {
            var owner = owners.FirstOrDefault(m => m.Value.GetValueOrDefault(id)).Key ?? owners.FirstOrDefault(m => m.Value.ContainsKey(id)).Key;
            if (owner != null)
            {
                found[id] = owner;
            }
        }

        // An item a mod builds in code on a game model has no file and no bundle to go by. When every named quest and
        // item that uses it (asks for it, gives it, takes it in a slot or a chamber) is one mod's, it's that mod's.
        var users = new Dictionary<string, HashSet<string>>();
        void Uses(string? mod, object? what)
        {
            if (string.IsNullOrEmpty(mod) || what == null)
            {
                return;
            }

            string text;
            try
            {
                text = JsonSerializer.Serialize(what);
            }
            catch (Exception)
            {
                return;
            }

            foreach (Match match in HexId().Matches(text))
            {
                if (wanted.Contains(match.Value) && !found.ContainsKey(match.Value))
                {
                    (users.TryGetValue(match.Value, out var set) ? set : users[match.Value] = []).Add(mod);
                }
            }
        }

        foreach (var (key, quest) in templates.Quests)
        {
            var mod = found.GetValueOrDefault(key.ToString());
            Uses(mod, quest.Conditions);
            Uses(mod, (quest.Rewards ?? []).SelectMany(r => r.Value ?? []).SelectMany(r => r?.Items ?? []).Select(i => i.Template.ToString()).ToList());
        }

        foreach (var (key, item) in templates.Items)
        {
            var mod = found.GetValueOrDefault(key.ToString()) ?? (BundleMod != null && item.Properties?.Prefab?.Path is { Length: > 0 } p ? BundleMod(p) : null);
            var props = item.Properties;
            if (mod == null || props == null)
            {
                continue;
            }

            var slots = (props.Slots ?? []).Concat(props.Chambers ?? []).Concat(props.Cartridges ?? [])
                .SelectMany(s => s?.Properties?.Filters ?? []).SelectMany(f => f?.Filter ?? []).Select(f => f.ToString());
            var grids = (props.Grids ?? []).SelectMany(g => g?.Properties?.Filters ?? []).SelectMany(f => f?.Filter ?? []).Select(f => f.ToString());
            Uses(mod, slots.Concat(grids).ToList());
        }

        foreach (var (id, by) in users.Where(u => u.Value.Count == 1))
        {
            found[id] = by.First();
        }

        return found;
    }

    // Whether the id at this place in a JSON file names what's being defined: the key of an object, or an "_id" value.
    private static bool Defines(string text, int at)
    {
        var after = at + 24;
        if (after < text.Length && text[after] == '"')
        {
            var next = after + 1;
            while (next < text.Length && char.IsWhiteSpace(text[next]))
            {
                next++;
            }

            if (next < text.Length && text[next] == ':')
            {
                next++;
                while (next < text.Length && char.IsWhiteSpace(text[next]))
                {
                    next++;
                }

                if (next < text.Length && text[next] == '{')
                {
                    return true;
                }
            }
        }

        return IdField().IsMatch(text.AsSpan(Math.Max(0, at - 24), Math.Min(at, 24)));
    }

    private HashSet<string> Ids(string file)
    {
        var set = new HashSet<string>();
        try
        {
            using var stream = File.OpenRead(file);
            using var json = JsonDocument.Parse(stream);
            foreach (var entry in json.RootElement.EnumerateObject())
            {
                set.Add(entry.Name);
            }
        }
        catch (Exception ex)
        {
            logger.Warning($"[Tushonkapedia] Couldn't read {Path.GetFileName(file)} from SPT, so modded entries aren't marked: {ex.Message}");
        }

        return set;
    }

    [GeneratedRegex("\"(_id|id|newId|newItemId|itemId)\"\\s*:\\s*\"$", RegexOptions.IgnoreCase)]
    private static partial Regex IdField();

    [GeneratedRegex("(?<![0-9a-f])[0-9a-f]{24}(?![0-9a-f])")]
    private static partial Regex HexId();
}
