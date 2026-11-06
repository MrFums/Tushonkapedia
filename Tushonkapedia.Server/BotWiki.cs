using SPTarkov.Server.Core.Models.Common;
using SPTarkov.Server.Core.Models.Eft.Common;
using SPTarkov.Server.Core.Models.Eft.Common.Tables;
using SPTarkov.Server.Core.Models.Enums;
using Path = System.IO.Path;

namespace Tushonkapedia.Server;

// The bots: every kind the server's bot table has, mods' included (a role SPT's own files don't have is a mod's),
// grouped by what players call them, with where they spawn, how tough they are, what they carry and what killing one
// is worth. Roles that aren't enemies (traders' helpers, the BTR's gunner, event spirits) are left out.
public sealed partial class ItemWiki
{
    private HashSet<string>? _stockRoles;

    // the roles SPT ships, from its own files (SPT_Data/database/bots/types/<role>.json)
    private bool IsStockRole(string role)
    {
        _stockRoles ??= Directory.Exists(Path.Combine(AppContext.BaseDirectory, "SPT_Data", "database", "bots", "types"))
            ? Directory.GetFiles(Path.Combine(AppContext.BaseDirectory, "SPT_Data", "database", "bots", "types"), "*.json")
                .Select(f => Path.GetFileNameWithoutExtension(f)).ToHashSet(StringComparer.OrdinalIgnoreCase)
            : new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        return _stockRoles.Count == 0 || _stockRoles.Contains(role);
    }

    // the roles the server makes its PMCs as (its PMC settings), set by WikiRoute
    public Func<IEnumerable<string>>? PmcRoles { get; set; }

    // the roles the server's seasonal events spawn (their boss spawns and waves), set by WikiRoute
    public Func<IEnumerable<string>>? EventRoles { get; set; }

    private HashSet<string>? _spawned;

    // Whether anything on this server spawns the role: a map (its waves and boss spawns), a seasonal event, or the
    // PMC settings. A bot file nothing spawns (a trader's bot the game keeps for an event it doesn't run) isn't shown.
    private bool Spawned(string role)
    {
        if (_spawned == null)
        {
            var shapes = SpawnShapes();
            var set = new HashSet<string>(shapes.Waves.Concat(shapes.Leaders).Concat(shapes.Escorts), StringComparer.OrdinalIgnoreCase);
            set.UnionWith(EventRoles?.Invoke() ?? []);
            _spawned = set;
        }

        return _spawned.Contains(role) || PmcOf(role) != null;
    }

    // the bot type a PMC role is made from: the shortest bot type its name ends with (pmcBEAR is made from bear)
    private string PmcType(string pmc) => bots.Types.Keys.Where(k => pmc.EndsWith(k, StringComparison.OrdinalIgnoreCase))
        .OrderBy(k => k.Length).FirstOrDefault() ?? pmc;

    // the PMC role a role belongs to: the role itself, or the bot type one is made from (usec for pmcUSEC)
    private string? PmcOf(string role) => PmcRoles?.Invoke().FirstOrDefault(p => !string.IsNullOrEmpty(p)
        && (p.Equals(role, StringComparison.OrdinalIgnoreCase) || p.EndsWith(role, StringComparison.OrdinalIgnoreCase)));

    private sealed record Shapes(HashSet<string> Waves, HashSet<string> Leaders, HashSet<string> Escorts);
    private Shapes? _shapes;

    // How the maps spawn each role: in the maps' waves, leading a spawn of its own, or along with another role's.
    private Shapes SpawnShapes()
    {
        if (_shapes != null)
        {
            return _shapes;
        }

        var shapes = new Shapes(new(StringComparer.OrdinalIgnoreCase), new(StringComparer.OrdinalIgnoreCase), new(StringComparer.OrdinalIgnoreCase));
        foreach (var location in locations.GetDictionary().Values)
        {
            foreach (var wave in location?.Base?.Waves ?? [])
            {
                if (wave?.WildSpawnType?.ToString() is { Length: > 0 } w)
                {
                    shapes.Waves.Add(w);
                }
            }

            foreach (var spawn in location?.Base?.BossLocationSpawn ?? [])
            {
                if (string.IsNullOrEmpty(spawn?.BossName))
                {
                    continue;
                }

                shapes.Leaders.Add(spawn.BossName);
                // a spawn may name an escort type and send none of it
                foreach (var (escort, amount) in new[] { (spawn.BossEscortType, spawn.BossEscortAmount?.ToString()) }
                             .Concat((spawn.Supports ?? []).Select(x => (x?.BossEscortType, x?.BossEscortAmount?.ToString()))))
                {
                    var some = (amount ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                        .Any(n => double.TryParse(n, System.Globalization.CultureInfo.InvariantCulture, out var x) && x > 0);
                    if (some && !string.IsNullOrEmpty(escort) && !escort.Equals(spawn.BossName, StringComparison.OrdinalIgnoreCase))
                    {
                        shapes.Escorts.Add(escort);
                    }
                }
            }
        }

        return _shapes = shapes;
    }

    // What sort of bot a role is, from the server's own data, as (key, the locale's word for it):
    //   PMC: a role the server makes its PMCs as (pmcUSEC), or the bot type one is made from (usec, the end of it);
    //   Scav: a role the maps' waves spawn;
    //   a role with its own label in the locale ("ScavRole/PmcBot" is "Raider"), grouped by that label;
    //   Boss: a role that leads spawns on the maps; guard: one that comes along with another role's spawn;
    //   other: none of these (event bots, mods' bots the maps don't spawn).
    private (string Key, string? Name) Kind(string role, IReadOnlyDictionary<string, string> locale)
    {
        var pmcs = PmcRoles?.Invoke().Where(r => !string.IsNullOrEmpty(r)).ToList() ?? [];
        if (pmcs.Any(p => p.Equals(role, StringComparison.OrdinalIgnoreCase) || p.EndsWith(role, StringComparison.OrdinalIgnoreCase)))
        {
            return ("pmc", Label(locale, "Pmc"));
        }

        var shapes = SpawnShapes();
        if (shapes.Waves.Contains(role))
        {
            return ("scav", Label(locale, "Scav"));
        }

        var own = locale.FirstOrDefault(e => e.Key.StartsWith("ScavRole/", StringComparison.Ordinal)
            && e.Key["ScavRole/".Length..].Equals(role, StringComparison.OrdinalIgnoreCase)).Value?.Trim();
        if (own is { Length: > 0 } && own.Any(char.IsLetter))
        {
            return ("label:" + own, own);
        }

        if (shapes.Leaders.Contains(role))
        {
            return ("boss", Label(locale, "ScavRole/Boss"));
        }

        return shapes.Escorts.Contains(role) ? ("guard", Label(locale, "ScavRole/Follower")) : ("other", null);
    }

    private static string? Label(IReadOnlyDictionary<string, string> locale, string key) =>
        locale.GetValueOrDefault(key)?.Trim() is { Length: > 0 } v ? v : null;

    // the order kinds are listed in: PMCs, Scavs, the labelled ones, bosses, guards, the rest
    private static int KindOrder(string key) => key switch
    {
        "pmc" => 0, "scav" => 1, "boss" => 3, "guard" => 4, "other" => 5, _ => 2
    };

    // The bot table's keys are lower case; the maps' spawn lists spell the roles as the game does ("blackDivAssault"),
    // which the names are made from.
    private Dictionary<string, string>? _casing;

    private string BotName(string role, IReadOnlyDictionary<string, string> locale)
    {
        if (_casing == null)
        {
            var casing = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var location in locations.GetDictionary().Values)
            {
                foreach (var spawn in location?.Base?.BossLocationSpawn ?? [])
                {
                    foreach (var name in new[] { spawn.BossName, spawn.BossEscortType }.Concat((spawn.Supports ?? []).Select(x => x.BossEscortType)))
                    {
                        if (!string.IsNullOrEmpty(name) && name.Any(char.IsUpper))
                        {
                            casing.TryAdd(name, name);
                        }
                    }
                }
            }

            foreach (var value in Enum.GetNames<WildSpawnType>())
            {
                casing.TryAdd(value, value);
            }

            _casing = casing;
        }

        return BotRoles.Name(_casing.GetValueOrDefault(role) ?? Split(role), locale);
    }

    private HashSet<string>? _words;

    // A lower-case role no spawn list spells out ("blackdivlead"), cut into the words the other roles are made of
    // ("black", "Div") and capitalised as they are: "blackDivLead". What's left over is the last word.
    private string Split(string role)
    {
        _words ??= _casing!.Values.SelectMany(n => System.Text.RegularExpressions.Regex.Split(n, "(?<=[a-z])(?=[A-Z])"))
            .Where(w => w.Length > 1).ToHashSet();
        var parts = new List<string>();
        var at = 0;
        while (at < role.Length)
        {
            var word = _words.Where(w => string.Compare(role, at, w, 0, w.Length, StringComparison.OrdinalIgnoreCase) == 0)
                .OrderByDescending(w => w.Length).FirstOrDefault();
            if (word == null)
            {
                parts.Add(char.ToUpperInvariant(role[at]) + role[(at + 1)..]);
                break;
            }

            parts.Add(parts.Count == 0 ? word : char.ToUpperInvariant(word[0]) + word[1..]);
            at += word.Length;
        }

        return parts.Count > 1 ? string.Concat(parts) : role;
    }

    // role -> map name -> best chance a raid (bosses, guards, raiders, rogues, cultists from the boss spawn lists;
    // Scavs and snipers from the waves, which always spawn)
    private Dictionary<string, Dictionary<string, double>> SpawnMaps(IReadOnlyDictionary<string, string> locale)
    {
        var byRole = new Dictionary<string, Dictionary<string, double>>(StringComparer.OrdinalIgnoreCase);
        void Add(string? role, string map, double chance)
        {
            if (string.IsNullOrEmpty(role) || chance <= 0)
            {
                return;
            }

            if (!byRole.TryGetValue(role, out var maps))
            {
                byRole[role] = maps = [];
            }

            maps[map] = Math.Max(maps.GetValueOrDefault(map), chance);
        }

        foreach (var (key, location) in locations.GetDictionary())
        {
            var info = location?.Base;
            if (info == null || key.Equals("hideout", StringComparison.OrdinalIgnoreCase) || info.Enabled == false)
            {
                continue;
            }

            var map = locale.GetValueOrDefault(info.IdField + " Name") ?? info.Name ?? key;
            foreach (var spawn in info.BossLocationSpawn ?? [])
            {
                var chance = spawn.BossChance ?? 0;
                Add(spawn.BossName, map, chance);
                Add(spawn.BossEscortType, map, chance);
                foreach (var support in spawn.Supports ?? [])
                {
                    Add(support.BossEscortType, map, chance);
                }
            }

            foreach (var wave in info.Waves ?? [])
            {
                Add(wave.WildSpawnType?.ToString(), map, 100);
            }
        }

        return byRole;
    }

    private static double PartsTotal(BotType type) =>
        type.BotHealth?.BodyParts?.FirstOrDefault() is { } p
            ? new[] { p.Head, p.Chest, p.Stomach, p.LeftArm, p.RightArm, p.LeftLeg, p.RightLeg }.Sum(x => x?.Max ?? 0) : 0;

    private Dictionary<string, List<string>> BotGroups(IReadOnlyDictionary<string, string> locale)
    {
        var groups = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var (role, type) in bots.Types)
        {
            if (type == null || !BotRoles.IsEnemy(role) || !Spawned(role))
            {
                continue;
            }

            // a PMC role and the bot type it's made from are one bot, by the bot type's name ("BEAR")
            var name = PmcOf(role) is { } pmc ? BotName(PmcType(pmc), locale) : BotName(role, locale);
            if (!groups.TryGetValue(name, out var roles))
            {
                groups[name] = roles = [];
            }

            roles.Add(role);
        }

        return groups;
    }

    // a group's name and roles, by its id
    public (string Name, List<string> Roles)? Group(string id, IReadOnlyDictionary<string, string> locale) =>
        BotGroups(locale).Where(g => GroupId(g.Value).Equals(id, StringComparison.OrdinalIgnoreCase)).Select(g => ((string, List<string>)?)(g.Key, g.Value)).FirstOrDefault();

    // the id a group goes by: its first role, one that leads spawns on the maps before the rest
    private string GroupId(List<string> roles) => roles.OrderBy(r => SpawnShapes().Leaders.Contains(r) ? 0 : 1).ThenBy(r => r).First();

    // Every kind of bot, for the list and the search.
    public List<object> Bots(IReadOnlyDictionary<string, string> locale)
    {
        var spawns = SpawnMaps(locale);
        return BotGroups(locale).Select(g => (Group: g, Id: GroupId(g.Value), Kind: Kind(GroupId(g.Value), locale)))
            .OrderBy(b => KindOrder(b.Kind.Key)).ThenBy(b => b.Kind.Name).ThenBy(b => b.Group.Key)
            .Select(b => (object)new
            {
                id = b.Id, name = b.Group.Key, kind = b.Kind.Key, kindName = b.Kind.Name, roles = b.Group.Value,
                mod = b.Group.Value.Any(r => !IsStockRole(r)),
                modName = b.Group.Value.Where(r => !IsStockRole(r)).Select(BotMod).FirstOrDefault(m => m != null),
                health = Math.Round(PartsTotal(bots.Types[b.Id]!)),
                maps = b.Kind.Key == "pmc" ? ["Every map"] : b.Group.Value.SelectMany(r => spawns.GetValueOrDefault(r) ?? []).Select(m => m.Key).Distinct().OrderBy(m => m).ToList()
            }).ToList();
    }

    // One kind of bot: its roles, where it spawns and how often, health by body part, experience for a kill, and the
    // gear it's most often given (by the weights in its bot file).
    public object? Bot(string id, IReadOnlyDictionary<string, string> locale)
    {
        var group = BotGroups(locale).FirstOrDefault(g => g.Value.Contains(id, StringComparer.OrdinalIgnoreCase));
        if (group.Value == null || !bots.Types.TryGetValue(id, out var type) || type == null)
        {
            return null;
        }

        var names = new Dictionary<string, string>();
        string Name(string item) => names[item] = locale.GetValueOrDefault(item + " Name") ?? item;
        var spawns = SpawnMaps(locale);
        var kind = Kind(id, locale);
        var maps = group.Value.SelectMany(r => spawns.GetValueOrDefault(r) ?? []).GroupBy(m => m.Key)
            .Select(m => new { map = m.Key, chance = Math.Round(m.Max(x => x.Value)) }).OrderByDescending(m => m.chance).ThenBy(m => m.map).ToList();

        var parts = type.BotHealth?.BodyParts?.FirstOrDefault();
        var health = parts == null ? null : new[]
        {
            ("Head", parts.Head), ("Chest", parts.Chest), ("Stomach", parts.Stomach), ("Left arm", parts.LeftArm),
            ("Right arm", parts.RightArm), ("Left leg", parts.LeftLeg), ("Right leg", parts.RightLeg)
        }.Select(p => new { part = p.Item1, hp = Math.Round(p.Item2?.Max ?? 0) }).ToList();

        // every pick for a slot, as shares of its weights, the likeliest first
        List<object> Picks(IEnumerable<KeyValuePair<MongoId, double>> pool, int most)
        {
            var list = pool.ToList();
            var total = list.Sum(p => p.Value);
            return total <= 0 ? [] : list.GroupBy(p => p.Key.ToString()).Select(p => (Id: p.Key, Weight: p.Sum(x => x.Value)))
                .OrderByDescending(p => p.Weight).Take(most).Select(p => (object)new { id = p.Id, name = Name(p.Id), share = Math.Round(p.Weight * 100 / total, 1) }).ToList();
        }

        // each gear slot: how often the bot has anything there (its chance from the bot file) and what it can be
        var chances = type.BotChances?.EquipmentChances;
        // every slot the bot file fills, in its own order, by the game's own name for it ("FirstPrimaryWeapon" is "On Sling")
        string SlotName(string key) => locale.GetValueOrDefault(key)?.Trim() is { Length: > 0 } n ? n : System.Text.RegularExpressions.Regex.Replace(key, "([a-z])([A-Z])", "$1 $2");
        var gear = (type.BotInventory?.Equipment ?? []).Select(e => (Title: SlotName(e.Key.ToString()), Chance: chances?.GetValueOrDefault(e.Key.ToString()),
                Items: Picks(e.Value ?? [], 60)))
            .Where(s => s.Items.Count > 0 && s.Chance is null or > 0)
            .Select(s => (object)new { title = s.Title, chance = s.Chance, items = s.Items }).ToList();
        // what it can have on it to loot, where it keeps it
        var pools = type.BotInventory?.Items;
        var loot = new (string Title, Dictionary<MongoId, double>? Pool)[]
            { (SlotName("Pockets"), pools?.Pockets), (SlotName("TacticalVest"), pools?.TacticalVest), (SlotName("Backpack"), pools?.Backpack), (SlotName("SpecialLoot"), pools?.SpecialLoot) }
            .Select(l => (l.Title, Items: Picks(l.Pool ?? [], 40))).Where(l => l.Items.Count > 0)
            .Select(l => (object)new { title = l.Title, items = l.Items }).ToList();

        var xp = type.BotExperience?.Reward;
        return new
        {
            id, name = group.Key, kind = kind.Key, kindName = kind.Name,
            roles = group.Value,
            mod = group.Value.Any(r => !IsStockRole(r)),
            modName = group.Value.Where(r => !IsStockRole(r)).Select(BotMod).FirstOrDefault(m => m != null),
            maps = kind.Key == "pmc" ? [new { map = "Every map", chance = 100.0 }] : maps,
            health,
            healthTotal = Math.Round(PartsTotal(type)),
            // experience for a kill, by difficulty (the server's own figures; -1 means it uses the player's level)
            xp = xp == null ? null : xp.Where(r => r.Value != null).Select(r => new { difficulty = r.Key, min = r.Value.Min, max = r.Value.Max }),
            level = type.BotExperience?.Level is { } lv ? new { min = lv.Min, max = lv.Max } : null,
            gear,
            loot,
            items = names
        };
    }
}
