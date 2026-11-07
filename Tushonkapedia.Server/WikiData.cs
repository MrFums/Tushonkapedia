using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Nodes;
using SPTarkov.Server.Core.Models.Common;
using SPTarkov.Server.Core.Extensions;
using SPTarkov.Server.Core.Models.Enums;

namespace Tushonkapedia.Server;

// The wiki's index pages: every item by category, a trader's stock and loyalty levels, a map's bosses, extracts
// and loot, and the hideout. All from the server's own tables, mods included.
public sealed partial class ItemWiki
{
    // keys and keycards hang under this node in the item tree
    private static readonly string KeyNode = BaseClasses.KEY.ToString();
    private static readonly string WeaponNode = BaseClasses.WEAPON.ToString();
    private static readonly string BuiltInInsertNode = BaseClasses.BUILT_IN_INSERTS.ToString();
    private static readonly string PocketsNode = BaseClasses.POCKETS.ToString();

    private readonly Dictionary<string, object> _catalogs = [];
    private HashSet<string>? _currencies;

    // The server's money: the items it marks as a flea currency.
    public bool IsCurrency(string id) => (_currencies ??= templates.Items.Values
        .Where(t => t.Type == "Item" && t.Properties?.IsRagfairCurrency == true).Select(t => t.Id.ToString()).ToHashSet()).Contains(id);

    // each currency with the short name the server's locale gives it ("RUB")
    public Dictionary<string, string> Currencies(IReadOnlyDictionary<string, string> locale)
    {
        IsCurrency("");
        return _currencies!.ToDictionary(id => id, id => locale.GetValueOrDefault(id + " ShortName") ?? locale.GetValueOrDefault(id + " Name") ?? id);
    }

    // a trader's currency tag ("USD") as the item the server pays it in
    public static string CurrencyOf(CurrencyType? tag) => (tag ?? CurrencyType.RUB).GetCurrencyTpl().ToString();

    // every currency tag the server knows, as its item
    public static Dictionary<string, string> CurrencyTags() =>
        Enum.GetValues<CurrencyType>().ToDictionary(c => c.ToString(), c => CurrencyOf(c));
    private HashSet<string>? _reachable;
    private HashSet<string>? _startingKit;
    // what the server really gives, before counting what could fit on it
    private HashSet<string>? _given;

    // Every item the handbook lists, by category, with its price, so the page can browse them.
    public object Catalog(IReadOnlyDictionary<string, string> locale, string lang, QuestGraph graph)
    {
        lock (_catalogs)
        {
            if (_catalogs.TryGetValue(lang, out var cached))
            {
                return cached;
            }

            var categories = (templates.Handbook?.Categories ?? [])
                .Select(c => new { id = c.Id.ToString(), parent = c.ParentId?.ToString(), name = locale.GetValueOrDefault(c.Id.ToString()) is { Length: > 0 } n ? n : "Other", order = c.Order, icon = c.Icon })
                .ToList();
            var within = Reachable(graph);
            var items = Handbook()
                .Where(h => templates.Items.TryGetValue(new MongoId(h.Key), out var t) && t.Type == "Item")
                .Select(h => new
                {
                    id = h.Key,
                    name = locale.GetValueOrDefault(h.Key + " Name") ?? templates.Items[new MongoId(h.Key)].Name ?? h.Key,
                    shortName = locale.GetValueOrDefault(h.Key + " ShortName"),
                    category = h.Value.Category,
                    price = Math.Round(Price(h.Key)),
                    modded = !BaseGame(h.Key),
                    mod = BaseGame(h.Key) ? null : ModOf(h.Key, false),
                    // the armour's own soft inserts, which can't be taken out, and anything nothing on this server gives
                    fixedPart = templates.Items[new MongoId(h.Key)].Parent.ToString() == BuiltInInsertNode,
                    unused = within != null && Leftover(h.Key, within, locale)
                })
                .OrderBy(i => i.name)
                .ToList();
            var catalog = new { categories, items };
            // the loot isn't worked out yet: asked again once it is
            if (within != null)
            {
                _catalogs[lang] = catalog;
            }

            return catalog;
        }
    }

    private readonly Dictionary<string, object> _catalogStats = new();
    private Dictionary<string, List<string>>? _groupItems;
    private readonly Dictionary<string, object> _groups = new();

    // The server's item tree (every item sits under nodes like "ArmorPlate" or "Stock"): each node with the handbook
    // items under it. A handbook category doesn't always match how players look for things (plates sit among "gear
    // components"), so these are lists too.
    private Dictionary<string, List<string>> GroupItems()
    {
        if (_groupItems != null)
        {
            return _groupItems;
        }

        var book = Handbook();
        var map = new Dictionary<string, List<string>>();
        foreach (var (id, t) in templates.Items)
        {
            if (t.Type != "Item" || !book.ContainsKey(id.ToString()))
            {
                continue;
            }

            var seen = new HashSet<string>();
            for (var at = t.Parent.ToString(); !string.IsNullOrEmpty(at) && seen.Add(at)
                 && templates.Items.TryGetValue(new MongoId(at), out var node) && node.Type == "Node"; at = node.Parent.ToString())
            {
                if (!map.TryGetValue(at, out var list))
                {
                    map[at] = list = [];
                }

                list.Add(id.ToString());
            }
        }

        return _groupItems = map;
    }

    // Item-tree groups worth a list of their own: two or more items, not the same items as a handbook category or as
    // another group. Named by the server's locale, else the node's own name split into words.
    public object Groups(IReadOnlyDictionary<string, string> locale, string lang)
    {
        lock (_groups)
        {
            if (_groups.TryGetValue(lang, out var cached))
            {
                return cached;
            }

            var book = Handbook();
            // the lists already offered, by their items: a group that is nearly one of them (the item tree's "Ammo" is
            // the handbook's "Rounds" plus one stray round) adds nothing
            var taken = book.GroupBy(h => h.Value.Category).Select(g => g.Select(h => h.Key).ToHashSet()).ToList();
            bool Same(HashSet<string> a, ICollection<string> b) => a.Count(b.Contains) >= 0.95 * Math.Max(a.Count, b.Count);
            // a group named like a handbook category would show the same name twice
            var cats = templates.Handbook?.Categories ?? [];
            var catNames = cats.Select(c => locale.GetValueOrDefault(c.Id.ToString())).OfType<string>().ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var c in cats)
            {
                var inside = new HashSet<string> { c.Id.ToString() };
                for (var grew = true; grew;)
                {
                    grew = false;
                    foreach (var k in cats)
                    {
                        if (k.ParentId?.ToString() is { } parent && inside.Contains(parent) && inside.Add(k.Id.ToString()))
                        {
                            grew = true;
                        }
                    }
                }

                taken.Add(book.Where(h => inside.Contains(h.Value.Category)).Select(h => h.Key).ToHashSet());
            }

            var list = new List<object>();
            // the smallest groups first, so of two with the same items the one nearer the items is kept
            foreach (var (node, ids) in GroupItems().OrderBy(g => g.Value.Count))
            {
                var raw = templates.Items[new MongoId(node)].Name ?? node;
                var name = locale.GetValueOrDefault(node + " Name") is { Length: > 0 } n ? n
                    : locale.GetValueOrDefault(node) is { Length: > 0 } m ? m : Spaced(raw);
                var set = ids.ToHashSet();
                if (ids.Count < 2 || catNames.Contains(name) || taken.Any(t => Same(t, set)))
                {
                    continue;
                }

                taken.Add(set);
                list.Add(new { id = node, name, count = ids.Count });
            }

            _groups[lang] = list;
            return list;
        }
    }

    // an item-tree group's items, for the page's list of it (empty for anything else)
    public List<string> GroupList(string id) => GroupItems().GetValueOrDefault(id) ?? [];

    // the items of a list: a handbook category with everything below it, or an item-tree group
    private List<string> ListItems(string list)
    {
        if (GroupItems().TryGetValue(list, out var group) && !(templates.Handbook?.Categories ?? []).Any(c => c.Id.ToString() == list))
        {
            return group;
        }

        var cats = templates.Handbook?.Categories ?? [];
        var inside = new HashSet<string> { list };
        for (var grew = true; grew;)
        {
            grew = false;
            foreach (var c in cats)
            {
                if (c.ParentId?.ToString() is { } parent && inside.Contains(parent) && inside.Add(c.Id.ToString()))
                {
                    grew = true;
                }
            }
        }

        return Handbook().Where(h => inside.Contains(h.Value.Category)
            && templates.Items.TryGetValue(new MongoId(h.Key), out var t) && t.Type == "Item").Select(h => h.Key).ToList();
    }

    // The figures a category's list can show beside each item, so it can be sorted by them: ergonomics and recoil for
    // grips, class and durability for plates, space for bags. Picked from what the items themselves have: a figure
    // most of them carry and that isn't the same on all of them. Nothing is picked by name.
    public object CatalogStats(string category, IReadOnlyDictionary<string, string> locale, string lang)
    {
        lock (_catalogStats)
        {
            var key = lang + "|" + category;
            if (_catalogStats.TryGetValue(key, out var cached))
            {
                return cached;
            }

            var ids = ListItems(category);
            // each figure by its group and label, in the order the item pages list them
            var order = new List<string>();
            var labels = new Dictionary<string, (string Label, string Group)>();
            var found = new Dictionary<string, Dictionary<string, StatRow>>();
            foreach (var id in ids)
            {
                var mine = new Dictionary<string, StatRow>();
                foreach (var g in Stats(id, locale))
                {
                    foreach (var r in g.rows)
                    {
                        var k = g.title + "|" + r.label;
                        if (mine.TryAdd(k, r) && !labels.ContainsKey(k))
                        {
                            labels[k] = (r.label, g.title);
                            order.Add(k);
                        }
                    }
                }

                found[id] = mine;
            }

            // a figure that differs between the items and is short enough to sit in a column. What most of the list has
            // comes first; the free columns go to what fewer have (the few grips that change recoil), so they can be
            // sorted to the top. A figure an item doesn't list is one it doesn't change.
            var usable = order.Select(k => (Key: k, Rows: found.Values.Select(m => m.GetValueOrDefault(k)).OfType<StatRow>().ToList()))
                .Where(x => x.Rows.Count >= 2 && x.Rows.Select(r => r.value).Distinct().Count() > 1 && x.Rows.Average(r => r.value.Length) <= 24)
                .ToList();
            var most = usable.Where(x => x.Rows.Count >= ids.Count / 2.0).Select(x => x.Key).ToList();
            var some = usable.Where(x => x.Rows.Count < ids.Count / 2.0 && x.Rows.Count >= ids.Count / 20.0).OrderByDescending(x => x.Rows.Count).Select(x => x.Key);
            var chosen = most.Concat(some).Take(6).ToHashSet();
            var picked = order.Where(chosen.Contains).ToList();
            // the same label in two groups ("Ergonomics" on the gun and while worn) says which
            var columns = picked.Select(k => new
            {
                key = k,
                label = picked.Count(o => labels[o].Label == labels[k].Label) > 1 ? $"{labels[k].Label} ({labels[k].Group.ToLowerInvariant()})" : labels[k].Label
            }).ToList();
            var items = found.Where(f => picked.Any(f.Value.ContainsKey)).ToDictionary(f => f.Key, f => picked.Select(k => f.Value.GetValueOrDefault(k) is { } r
                ? new object?[] { r.value, r.n, r.tone } : null).ToList());
            var result = new { columns, items };
            _catalogStats[key] = result;
            return result;
        }
    }

    // the game's own left-behind items: nothing gives them, or their name says they're for the developers
    // An item the game has but players never meet (a developer's "DO NOT USE" round): the server's handbook doesn't
    // list it (or SPT's own item settings say it's never handed out), nothing on the server hands it out (loot, traders, crafts, bots, quests; merely fitting a gun's slot
    // doesn't count), no new character starts with it, and it doesn't stand on a map. Left out of search and the lists.
    public SPTarkov.Server.Core.Models.Spt.Config.ItemConfig? ItemSettings { get; set; }
    // seasonal items SPT keeps out of loot right now, read live because an event can start or end while it runs
    public Func<HashSet<string>>? OutOfSeason { get; set; }

    private HashSet<string>? _never;

    // the game's own leftovers: a base game item that's off the handbook or blacklisted and that nothing on this server
    // gives. A mod's item is never hidden: the mod may hand it out in ways the tables don't show.
    public bool Hidden(string id, QuestGraph graph) =>
        BaseGame(id) && (!Handbook().ContainsKey(id) || (_never ??= ItemSettings?.Blacklist.Select(x => x.ToString()).ToHashSet() ?? []).Contains(id)) && Reachable(graph) != null && _given?.Contains(id) == false && !StartingKit().Contains(id)
        && !(MongoId.IsValidMongoId(id) && templates.Items.TryGetValue(new MongoId(id), out var t) && t.Properties?.QuestItem == true)
        && _loot?.Placed.Values.Any(p => p.ContainsKey(id)) != true;

    private bool Leftover(string id, HashSet<string> reachable, IReadOnlyDictionary<string, string> locale) =>
        BaseGame(id) && (locale.GetValueOrDefault(id + " Name") is not { Length: > 0 } name || DevName().IsMatch(name));

    // what a new character can start with, on any edition
    private HashSet<string> StartingKit()
    {
        if (_startingKit == null)
        {
            var file = Path.Combine(AppContext.BaseDirectory, "SPT_Data", "database", "templates", "profiles.json");
            _startingKit = File.Exists(file) ? HexId().Matches(File.ReadAllText(file)).Select(m => m.Value).ToHashSet() : [];
        }

        return _startingKit;
    }

    [System.Text.RegularExpressions.GeneratedRegex(@"^!!!|^\(off\)|_(dev|test)$|^Key \d+$|\btest key$|Shrapnel$|^Airsoft", System.Text.RegularExpressions.RegexOptions.IgnoreCase)]
    private static partial System.Text.RegularExpressions.Regex DevName();

    // Every item something on this server gives or asks for, for spotting the game's leftovers (a mod's items can still
    // sell on the flea, so only base game items are called unused): loot, traders (Fence's random stock left out, as a mod
    // can make him sell anything), crafts, bots, quests, and what fits in the slots of any of those. Null until the
    // loot is worked out.
    private HashSet<string>? Reachable(QuestGraph graph)
    {
        if (_reachable != null || _loot == null)
        {
            return _reachable;
        }

        var trade = Trades();
        var found = _loot.Items.Keys.Where(id => LootFor(id, new Dictionary<string, string>()).Any()).ToHashSet();
        found.UnionWith(trade.Offers.Select(o => o.Item));
        found.UnionWith(trade.Crafts.Select(c => c.Item));
        found.UnionWith(Carriers().Keys);
        found.UnionWith(graph.Quests.SelectMany(q => q.Rewards).Where(r => r.Item != null).Select(r => r.Item!));
        found.UnionWith(graph.Quests.SelectMany(q => q.Objectives).SelectMany(o => o.Items));
        found.UnionWith(graph.Quests.SelectMany(q => q.Needs).Where(n => n.Kind is not ("lock" or "sell")).SelectMany(n => n.Ids));
        _given = new HashSet<string>(found);
        // quest items turn up in raid, and a new character's kit (a secure container, a pouch) comes from the profile
        found.UnionWith(templates.Items.Values.Where(t => t.Properties?.QuestItem == true).Select(t => t.Id.ToString()));
        found.UnionWith(StartingKit());

        // a part can come fitted to a gun or armour, and presets aren't walked here (pockets' slots take anything)
        foreach (var item in templates.Items.Values.Where(t => found.Contains(t.Id.ToString()) && t.Parent.ToString() != PocketsNode).ToList())
        {
            foreach (var slot in item.Properties?.Slots ?? [])
            {
                found.UnionWith((slot.Properties?.Filters ?? []).SelectMany(f => f.Filter ?? []).Select(f => f.ToString()));
            }
        }

        return _reachable = found;
    }

    // A trader: who they are, what each loyalty level needs, and everything they sell.
    public object? Trader(string id, IReadOnlyDictionary<string, string> locale)
    {
        if (!MongoId.IsValidMongoId(id) || !traders.TryGetValue(new MongoId(id), out var trader) || trader?.Base == null)
        {
            return null;
        }

        var offers = Trades().Offers.Where(o => o.Trader == id).ToList();
        var names = new Dictionary<string, string>();
        foreach (var item in offers.Select(o => o.Item).Concat(offers.SelectMany(o => o.Barter.Select(b => b.Item))))
        {
            names[item] = locale.GetValueOrDefault(item + " Name") ?? item;
        }

        return new
        {
            id,
            name = locale.GetValueOrDefault(id + " Nickname") ?? trader.Base.Nickname,
            fullName = locale.GetValueOrDefault(id + " FullName") ?? $"{trader.Base.Name} {trader.Base.Surname}".Trim(),
            location = TraderWhere(id, locale),
            description = locale.GetValueOrDefault(id + " Description"),
            avatar = trader.Base.Avatar,
            currency = trader.Base.Currency?.ToString(),
            // the server makes this trader's stock as it goes (SPT's FenceService), so there is no fixed list
            randomStock = id == Fence,
            levels = (trader.Base.LoyaltyLevels ?? []).Select((l, i) => new { level = i + 1, minLevel = l.MinLevel, minSalesSum = l.MinSalesSum, minStanding = l.MinStanding }),
            offers = offers.Select(o => new
            {
                item = o.Item, level = o.Level, currency = o.Currency, price = o.Price,
                barter = o.Barter.Select(b => new { id = b.Item, count = b.Count }),
                quest = o.Quest, questState = o.QuestState, limit = o.Limit
            }),
            items = names
        };
    }

    // where a trader is: the locale's line, except the BTR driver, who is on every map the server's BTR settings
    // name (the locale's line only knows the first one; a mod can add maps there)
    public string? TraderWhere(string id, IReadOnlyDictionary<string, string> locale) =>
        id == Traders.BTR.ToString() && BtrMaps(locale) is { Count: > 0 } maps ? string.Join(", ", maps) : locale.GetValueOrDefault(id + " Location");

    private List<string> BtrMaps(IReadOnlyDictionary<string, string> locale)
    {
        var loot = _loot ?? new LootIndex();
        var names = new List<string>();
        try
        {
            if (Rules?.Globals.Configuration is not { } config || JsonSerializer.SerializeToNode(config, config.GetType()) is not JsonObject obj)
            {
                return names;
            }

            var settings = obj.FirstOrDefault(kv => kv.Key.Equals("BTRSettings", StringComparison.OrdinalIgnoreCase)).Value as JsonObject;
            var list = settings?.FirstOrDefault(kv => kv.Key.Equals("LocationsWithBTR", StringComparison.OrdinalIgnoreCase)).Value as JsonArray;
            // only maps a player can raid, as the maps list counts them
            var maps = locations.GetDictionary().Values.Where(l => l?.Base?.Id != null && (loot.Locations.ContainsKey(l.Base.Id) || (l.Base.Exits?.Any() == true && l.Base.Enabled))).ToList();
            foreach (var entry in list ?? [])
            {
                if (entry?.ToString() is { Length: > 0 } want && maps.FirstOrDefault(l => l!.Base.Id.Equals(want, StringComparison.OrdinalIgnoreCase)) is { } map)
                {
                    var name = MapName(loot, map.Base.Id, locale);
                    names.Add(name != map.Base.Id ? name : locale.GetValueOrDefault(map.Base.IdField + " Name") ?? map.Base.Name ?? map.Base.Id);
                }
            }
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException or InvalidOperationException)
        {
        }

        return names.Distinct().ToList();
    }

    private sealed record MapBoss(string name, double? chance, string? zones, string? escorts, string? escortType);
    private sealed record MapExit(string name, double? chance, bool pmc, bool scav, string requirement, string? tip, double? seconds);

    // An extract as the page shows it. side: Pmc, Scav or Coop (both); without one, the eligible flags.
    private static MapExit Exit(IReadOnlyDictionary<string, string> locale, string name, double? chance, string? side, bool? forPmc, bool? forScav,
        string requirement, string? tipKey, double? count, string? id, double? seconds) => new(
        locale.GetValueOrDefault(name) ?? name,
        chance,
        side is null ? forPmc != false : side is "Pmc" or "Coop" or "All",
        side is null ? forScav == true : side is "Scav" or "Coop" or "All",
        requirement,
        // "Bring {0}" -> "Bring 5,000 Roubles"; a tip with no text of its own is left out
        string.IsNullOrEmpty(tipKey) || locale.GetValueOrDefault(tipKey) is not { } tip ? null
            : tip.Replace("{0}", count is > 0 && MongoId.IsValidMongoId(id ?? "") ? $"{count:N0} {locale.GetValueOrDefault(id + " Name") ?? id}" : ""),
        seconds);

    private static IEnumerable<MapBoss> Bosses(IEnumerable<MapBoss> all) =>
        all.Where(b => !string.IsNullOrEmpty(b.name) && !b.name.StartsWith("pmc", StringComparison.OrdinalIgnoreCase) && (b.chance ?? 0) > 0)
            .GroupBy(b => b.name).Select(g => g.OrderByDescending(b => b.chance).First());

    // SPT's own files for a map (SPT_Data/database/locations/<map>/<file>), read once: the SPT version of a map a mod
    // has replaced with its 1.0 one. Null if there's no such file.
    private readonly ConcurrentDictionary<string, System.Text.Json.Nodes.JsonNode?> _stock = new(StringComparer.OrdinalIgnoreCase);

    private System.Text.Json.Nodes.JsonNode? Stock(string map, string file) => _stock.GetOrAdd(map + "/" + file, _ =>
    {
        foreach (var root in new[] { AppContext.BaseDirectory, Directory.GetCurrentDirectory() })
        {
            var path = Path.Combine(root, "SPT_Data", "database", "locations", map.ToLowerInvariant(), file);
            try
            {
                if (File.Exists(path))
                {
                    using var stream = File.OpenRead(path);
                    return System.Text.Json.Nodes.JsonNode.Parse(stream);
                }
            }
            catch (Exception ex) when (ex is IOException or System.Text.Json.JsonException)
            {
                logger.Warning($"[Tushonkapedia] Couldn't read {path}: {ex.Message}");
            }
        }

        return null;
    });

    // whether this version of a map is SPT's own while the server serves a mod's: then its bosses, extracts and outline
    // come from SPT's files
    private bool StockVersion(string map, string? version) =>
        version == "game" && BoxesOn(map, version) is { Versions.Length: > 1 } boxes && boxes.Versions[0] != "game";

    private static double? Num(System.Text.Json.Nodes.JsonNode? node) =>
        node is System.Text.Json.Nodes.JsonValue v && v.TryGetValue<double>(out var d) ? d : null;

    private static string? Str(System.Text.Json.Nodes.JsonNode? node) =>
        node is System.Text.Json.Nodes.JsonValue v && v.TryGetValue<string>(out var t) ? t : node?.ToString();

    // A map: its bosses, extracts and the loot it's best for, plus the keys that spawn on it. With version "game" on a
    // map a mod has replaced: SPT's own bosses, extracts and raid settings for it (the loot stays the server's).
    public object? Map(string id, IReadOnlyDictionary<string, string> locale, string? version = null)
    {
        var location = locations.GetDictionary().Values.FirstOrDefault(l => l?.Base?.Id != null && l.Base.Id.Equals(id, StringComparison.OrdinalIgnoreCase));
        var info = location?.Base;
        if (info == null)
        {
            return null;
        }

        var stock = StockVersion(info.Id, version) ? Stock(info.Id, "base.json") : null;
        var stockExits = stock == null ? null : Stock(info.Id, "allExtracts.json") as System.Text.Json.Nodes.JsonArray ?? stock["exits"] as System.Text.Json.Nodes.JsonArray;

        var loot = _loot;
        var here = new List<(string Item, LootSpot Spot)>();
        if (loot != null)
        {
            foreach (var (item, spots) in loot.Items)
            {
                var spot = spots.FirstOrDefault(s => s.Location == info.Id);
                if (spot != null)
                {
                    here.Add((item, spot));
                }
            }
        }

        var names = new Dictionary<string, string>();
        object Entry((string Item, LootSpot Spot) x) => new
        {
            id = x.Item,
            name = names[x.Item] = locale.GetValueOrDefault(x.Item + " Name") ?? x.Item,
            perRaid = Math.Round(x.Spot.Total, 4),
            modded = !BaseGame(x.Item),
            mod = BaseGame(x.Item) ? null : ModOf(x.Item, false),
            price = Math.Round(Price(x.Item)),
            where = x.Spot.Containers.OrderByDescending(c => c.Value).Take(2)
                .Select(c => c.Key == "forced" ? "always there" : locale.GetValueOrDefault(c.Key + " Name") ?? c.Key)
                .Concat(x.Spot.Points > 0 && x.Spot.Loose > 0.001 ? ["loose"] : [])
        };

        return new
        {
            id = info.Id,
            name = MapName(loot ?? new LootIndex(), info.Id, locale) is var n && n != info.Id ? n : locale.GetValueOrDefault(info.IdField + " Name") ?? info.Name ?? info.Id,
            description = locale.GetValueOrDefault(info.IdField + " Description"),
            minutes = stock != null ? Num(stock["EscapeTimeLimitPVE"]) ?? Num(stock["EscapeTimeLimit"]) : info.EscapeTimeLimitPVE ?? info.EscapeTimeLimit,
            minPlayers = stock != null ? Num(stock["MinPlayers"]) : info.MinPlayers,
            maxPlayers = stock != null ? Num(stock["MaxPlayers"]) : info.MaxPlayers,
            level = stock != null ? Num(stock["RequiredPlayerLevelMin"]) : info.RequiredPlayerLevelMin,
            insurance = stock != null ? stock["Insurance"]?.GetValue<bool>() : info.Insurance,
            bosses = Bosses(stock != null
                ? (stock["BossLocationSpawn"] as System.Text.Json.Nodes.JsonArray ?? []).Select(b => new MapBoss(Str(b?["BossName"]) ?? "", Num(b?["BossChance"]),
                    Str(b?["BossZone"]), Str(b?["BossEscortAmount"]), Str(b?["BossEscortType"])))
                : (info.BossLocationSpawn ?? []).Select(b => new MapBoss(b.BossName ?? "", b.BossChance, b.BossZone, b.BossEscortAmount, b.BossEscortType))),
            // every extract, the scavs' too (the base only lists the PMCs'); Side says who can use it: Pmc, Scav or
            // Coop (both)
            extracts = (stockExits != null
                    ? stockExits.Select(e => Exit(locale, Str(e?["Name"]) ?? "", Num(e?["ChancePVE"]) ?? Num(e?["Chance"]), Str(e?["Side"]), null, null,
                        Str(e?["PassageRequirement"]) ?? "None", Str(e?["RequirementTip"]), Num(e?["Count"]), Str(e?["Id"]), Num(e?["ExfiltrationTimePVE"]) ?? Num(e?["ExfiltrationTime"])))
                    : (location!.AllExtracts?.Any() == true ? location.AllExtracts : info.Exits ?? [])
                        .Select(e => Exit(locale, e.Name ?? "", e.ChancePVE ?? e.Chance, e.Side, e.EligibleForPMC, e.EligibleForScav,
                            e.PassageRequirement.ToString(), e.RequirementTip, e.Count, e.Id, e.ExfiltrationTimePVE ?? e.ExfiltrationTime)))
                .Where(e => !string.IsNullOrEmpty(e.name))
                .DistinctBy(e => (e.name, e.pmc, e.scav)),
            versions = VersionsOf(BoxesOn(info.Id, version)),
            stock = stock != null,
            // what's worth the most here on average, and the rare valuable finds
            // keys have their own list below, and their price would crowd everything else out
            bestLoot = here.Where(x => !IsKey(x.Item)).OrderByDescending(x => x.Spot.Total * Price(x.Item)).Take(24).Select(Entry),
            keys = here.Where(x => IsKey(x.Item)).OrderByDescending(x => x.Spot.Total).Take(40).Select(Entry),
            lootReady = loot != null,
            items = names
        };
    }

    // A kind of container: the maps it stands on (how many a raid has, on average) and what's worth finding in one.
    // Where on the map each one stands isn't in the server's tables (their positions are 0, 0, 0). Kinds that share
    // a name (three kinds of weapon box) come as "id,id,id" and count as one. Null if no map has it.
    public object? Box(string ids, IReadOnlyDictionary<string, string> locale)
    {
        var loot = _loot;
        var tpls = ids.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Distinct().Take(8).ToList();
        if (loot == null || tpls.Count == 0 || !tpls.All(MongoId.IsValidMongoId))
        {
            return null;
        }

        double OnMap(string location) => loot.Placed.GetValueOrDefault(location) is { } placed ? tpls.Sum(t => placed.GetValueOrDefault(t)) : 0;
        var maps = loot.Placed.Keys.Select(l => (Location: l, OnMap: Math.Round(OnMap(l), 1))).Where(m => m.OnMap > 0)
            .OrderByDescending(m => m.OnMap).ToList();
        if (maps.Count == 0)
        {
            return null;
        }

        // the chance one container holds the item: its best map, as the odds hardly differ from map to map
        var inside = new List<(string Item, double Each)>();
        foreach (var (item, spots) in loot.Items)
        {
            var each = 0.0;
            foreach (var spot in spots)
            {
                var perRaid = tpls.Sum(t => spot.Containers.GetValueOrDefault(t));
                if (perRaid > 0 && OnMap(spot.Location) is > 0 and var onMap)
                {
                    each = Math.Max(each, Math.Min(1, perRaid / onMap));
                }
            }

            if (each > 0)
            {
                inside.Add((item, each));
            }
        }

        var names = new Dictionary<string, string>();
        object Entry((string Item, double Each) x) => new
        {
            id = x.Item,
            name = names[x.Item] = locale.GetValueOrDefault(x.Item + " Name") ?? x.Item,
            each = Math.Round(x.Each, 4),
            price = Math.Round(Price(x.Item))
        };

        return new
        {
            name = locale.GetValueOrDefault(tpls[0] + " Name") ?? tpls[0],
            maps = maps.Select(m => new { id = m.Location, name = MapName(loot, m.Location, locale), onMap = m.OnMap }),
            // worth the most on average (value times the odds), then the likeliest finds
            best = inside.OrderByDescending(x => x.Each * Price(x.Item)).Take(24).Select(Entry).ToList(),
            // the most valuable things it can hold, by price, leaving out the near-impossible (rarer than 1 in 5,000)
            priciest = inside.Where(x => x.Each >= 0.0002).OrderByDescending(x => Price(x.Item)).Take(24).Select(Entry).ToList(),
            common = inside.OrderByDescending(x => x.Each).Take(12).Select(Entry).ToList(),
            items = names
        };
    }

    public List<object> Maps(IReadOnlyDictionary<string, string> locale)
    {
        var loot = _loot ?? new LootIndex();
        return locations.GetDictionary().Values
            .Where(l => l?.Base?.Id != null && (loot.Locations.ContainsKey(l.Base.Id) || (l.Base.Exits?.Any() == true && l.Base.Enabled)))
            .Where(l => !l!.Base.Id.Equals("hideout", StringComparison.OrdinalIgnoreCase))
            .Select(l => (object)new
            {
                id = l!.Base.Id,
                name = MapName(loot, l.Base.Id, locale) is var n && n != l.Base.Id ? n : locale.GetValueOrDefault(l.Base.IdField + " Name") ?? l.Base.Name ?? l.Base.Id,
                minutes = l.Base.EscapeTimeLimitPVE ?? l.Base.EscapeTimeLimit,
                level = l.Base.RequiredPlayerLevelMin,
                // roubles of loot a raid holds on average, at the server's base prices
                lootValue = Math.Round(loot.Items.Sum(kv => kv.Value.Where(x => x.Location == l.Base.Id).Sum(x => x.Total) * Price(kv.Key))),
                bosses = (l.Base.BossLocationSpawn ?? []).Where(b => b.BossName?.StartsWith("boss", StringComparison.OrdinalIgnoreCase) == true && (b.BossChance ?? 0) > 0).Select(b => b.BossName).Distinct()
            })
            .ToList();
    }

    // Every hideout area: what each level needs and what it can craft.
    public object Hideout(IReadOnlyDictionary<string, string> locale)
    {
        var crafts = Trades().Crafts;
        var names = new Dictionary<string, string>();
        string Name(string item) => names[item] = locale.GetValueOrDefault(item + " Name") ?? item;

        var areas = (hideout.Areas ?? []).Where(a => a?.IsEnabled != false && a?.Type != null).Select(a =>
        {
            var type = (int)a!.Type!.Value;
            return new
            {
                type,
                // an area the locale doesn't name ("WeaponStandSecondary") reads as words
                name = locale.GetValueOrDefault($"hideout_area_{type}_name") ?? System.Text.RegularExpressions.Regex.Replace(a.Type.ToString()!, "(?<=[a-z])(?=[A-Z])", " "),
                description = locale.GetValueOrDefault($"hideout_area_{type}_description"),
                levels = (a.Stages ?? [])
                    .Where(s => int.TryParse(s.Key, out var level) && level > 0)
                    .OrderBy(s => int.Parse(s.Key))
                    .Select(s => new
                    {
                        level = int.Parse(s.Key),
                        hours = Math.Round((s.Value?.ConstructionTime ?? 0) / 3600, 1),
                        // the stash item the level gives the area (a gear rack's shelves), when it has one
                        container = s.Value?.Container?.ToString(),
                        description = string.IsNullOrEmpty(s.Value?.Description) ? null : locale.GetValueOrDefault(s.Value.Description),
                        items = (s.Value?.Requirements ?? []).Where(r => r.Type == "Item").Select(r => new { id = r.TemplateId.ToString(), name = Name(r.TemplateId.ToString()), count = r.Count ?? 1, fir = r.IsSpawnedInSession == true }),
                        areas = (s.Value?.Requirements ?? []).Where(r => r.Type == "Area").Select(r => new { area = r.AreaType, name = locale.GetValueOrDefault($"hideout_area_{r.AreaType}_name"), level = r.RequiredLevel }),
                        traders = (s.Value?.Requirements ?? []).Where(r => r.Type == "TraderLoyalty").Select(r => new { trader = r.TraderId.ToString(), level = r.LoyaltyLevel }),
                        skills = (s.Value?.Requirements ?? []).Where(r => r.Type == "Skill").Select(r => new { skill = locale.GetValueOrDefault(r.SkillName ?? "") ?? r.SkillName, level = r.SkillLevel }),
                        // what the level gives, in the game's own words ("Fuel consumption"); the free-text ones have none
                        bonuses = (s.Value?.Bonuses ?? []).Where(b => b.IsVisible != false && b.Type != null && b.Type.ToString() != "TextBonus").Select(b => new
                        {
                            type = b.Type.ToString(), text = locale.GetValueOrDefault("hideout_" + b.Type) ?? b.Type.ToString(), value = b.Value,
                            skill = b.SkillType == null ? null : locale.GetValueOrDefault(b.SkillType.ToString()!) ?? b.SkillType.ToString()
                        })
                    }),
                crafts = crafts.Where(c => c.Area == type).OrderBy(c => c.AreaLevel).ThenBy(c => Name(c.Item)).Select(c => new
                {
                    id = c.Id, item = c.Item, count = c.Count, level = c.AreaLevel, hours = Math.Round(c.Seconds / 3600, 2), quest = c.Quest,
                    inputs = c.Inputs.Select(i => new { id = i.Item, name = Name(i.Item), count = i.Count, tool = i.Tool })
                })
            };
        }).OrderBy(a => a.name).ToList();
        return new { areas, items = names };
    }

    private Dictionary<string, List<string>>? _prefabs;

    // Items with no picture of their own yet: mod items, and base-game items tarkov.dev has none for. The game
    // plugin draws these. Quest items are included, they have pages too.
    public List<string> IconsWanted(ItemIcons icons)
    {
        var book = Handbook();
        return templates.Items.Values
            .Where(t => t.Type == "Item" && (book.ContainsKey(t.Id.ToString()) || t.Properties?.QuestItem == true))
            .Select(t => t.Id.ToString())
            .Where(id => IconPrint(id) is var print && !icons.HasDrawn(id, print) && !icons.Failed(id, print))
            .ToList();
    }

    // every item the game plugin could draw, with its print: what tools/build_pack.py stores beside shipped pictures
    public Dictionary<string, string> IconPrints()
    {
        var book = Handbook();
        return templates.Items.Values
            .Where(t => t.Type == "Item" && (book.ContainsKey(t.Id.ToString()) || t.Properties?.QuestItem == true))
            .ToDictionary(t => t.Id.ToString(), t => IconPrint(t.Id.ToString()));
    }

    private Dictionary<string, string>? _shownParts;

    // What an item's picture is drawn from, as a short hash: its model, size and cell colour, and the parts it's
    // shown with (a gun's default build, each part's model too). When a mod update changes any of these, a saved or
    // shipped picture no longer matches and the game plugin draws it again. Prices and stats aren't in it: changing
    // those doesn't change the picture.
    public string IconPrint(string id)
    {
        if (!MongoId.IsValidMongoId(id) || !templates.Items.TryGetValue(new MongoId(id), out var item))
        {
            return "";
        }

        string Model(string tpl) => MongoId.IsValidMongoId(tpl) && templates.Items.TryGetValue(new MongoId(tpl), out var t) ? t.Properties?.Prefab?.Path ?? "" : "";
        _shownParts ??= ((IEnumerable<SPTarkov.Server.Core.Models.Spt.Tables.Preset>?)Rules?.Globals.ItemPresets?.Values ?? [])
            .Where(p => p.Encyclopedia != null && p.Items is { Count: > 0 })
            .GroupBy(p => p.Encyclopedia!.Value.ToString())
            .ToDictionary(g => g.Key, g => string.Join(",", g.First().Items!.Where(i => i.ParentId != null)
                .Select(i => i.Template.ToString() + ":" + Model(i.Template.ToString())).OrderBy(x => x, StringComparer.Ordinal)));
        var p = item.Properties;
        var text = $"{p?.Prefab?.Path}|{p?.Width}|{p?.Height}|{p?.BackgroundColor}|{_shownParts.GetValueOrDefault(id)}";
        return Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(text)))[..16].ToLowerInvariant();
    }

    public bool IsBaseGame(string id) => BaseGame(id);

    // A base-game item drawn from the same model, for a mod item that has no picture yet: most mod variants
    // reuse a vanilla model, so its picture is close (the colours can differ).
    public string? IconTwin(string id)
    {
        if (!MongoId.IsValidMongoId(id) || !templates.Items.TryGetValue(new MongoId(id), out var item) || item.Properties?.Prefab?.Path is not { Length: > 0 } path)
        {
            return null;
        }

        _prefabs ??= templates.Items.Values
            .Where(t => t.Type == "Item" && t.Properties?.Prefab?.Path is { Length: > 0 } && BaseGame(t.Id.ToString()))
            .GroupBy(t => t.Properties!.Prefab!.Path!)
            .ToDictionary(g => g.Key, g => g.Select(t => t.Id.ToString()).ToList());
        return _prefabs.TryGetValue(path, out var twins) ? twins.FirstOrDefault(t => t != id) : null;
    }

    // A kind of item rather than an item ("Jewelry", which a trader's rules can name): what's under it on this server.
    public object? ItemKind(string id, IReadOnlyDictionary<string, string> locale)
    {
        if (!MongoId.IsValidMongoId(id) || !templates.Items.TryGetValue(new MongoId(id), out var node) || node.Type != "Node")
        {
            return null;
        }

        var book = Handbook();
        return new
        {
            kind = true, id, name = node.Name ?? id,
            items = ItemsUnder(id).Where(book.ContainsKey).Select(i => new { id = i, name = ItemName(i, locale), price = Math.Round(Price(i)), modded = !BaseGame(i) }).OrderBy(i => i.name).ToList()
        };
    }

    // The most a trader would pay for an item, from the handbook's value and what each trader buys it at.
    public double BestTraderPrice(string id)
    {
        var best = 0.0;
        foreach (var trader in traders.Values)
        {
            var coef = trader?.Base?.LoyaltyLevels?.FirstOrDefault()?.BuyPriceCoefficient;
            if (coef != null && trader!.Base!.ItemsBuy != null)
            {
                best = Math.Max(best, HandbookPrice(id) * (100 - coef.Value) / 100);
            }
        }

        return best;
    }

    // Each trader who buys this item and what they pay at their first loyalty level, in their own money, best first.
    public List<object> SellTo(string id)
    {
        var list = new List<(double Roubles, object Row)>();
        if (!MongoId.IsValidMongoId(id))
        {
            return [];
        }

        foreach (var (key, trader) in traders)
        {
            var b = trader?.Base;
            var coef = b?.LoyaltyLevels?.FirstOrDefault()?.BuyPriceCoefficient;
            var buys = b?.ItemsBuy;
            if (b == null || coef == null || coef >= 100 || buys == null)
            {
                continue;
            }

            var never = b.ItemsBuyProhibited;
            if (never?.IdList?.Contains(new MongoId(id)) == true || (never?.Category ?? []).Any(c => IsUnder(id, c.ToString())))
            {
                continue;
            }

            if (buys.IdList?.Contains(new MongoId(id)) != true && !(buys.Category ?? []).Any(c => IsUnder(id, c.ToString())))
            {
                continue;
            }

            var currency = CurrencyOf(b.Currency);
            var roubles = HandbookPrice(id) * (100 - coef.Value) / 100;
            var rate = Math.Max(1, HandbookPrice(currency));
            list.Add((roubles, new { trader = key.ToString(), name = b.Nickname, currency, price = (int)Math.Floor(roubles / rate), roubles = (int)Math.Floor(roubles) }));
        }

        return list.OrderByDescending(x => x.Roubles).Select(x => x.Row).ToList();
    }

    public bool IsKey(string id) => IsUnder(id, KeyNode);

    public bool IsWeapon(string id) => IsUnder(id, WeaponNode);

    // whether the item hangs under this node of the item tree
    private bool IsUnder(string id, string node)
    {
        if (!MongoId.IsValidMongoId(id) || !templates.Items.TryGetValue(new MongoId(id), out var item))
        {
            return false;
        }

        for (var (at, steps) = (item, 0); at != null && steps < 20; at = templates.Items.GetValueOrDefault(at.Parent), steps++)
        {
            if (at.Id.ToString() == node)
            {
                return true;
            }
        }

        return false;
    }
}
