using System.Collections.Concurrent;
using System.Runtime;
using System.Text.Json;
using SPTarkov.Common.Models.Logging;
using SPTarkov.Server.Core.Models.Common;
using SPTarkov.Server.Core.Extensions;
using SPTarkov.Server.Core.Models.Enums;
using TemplateItem = SPTarkov.Server.Core.Models.Eft.Common.Tables.TemplateItem;
using Location = SPTarkov.Server.Core.Models.Eft.Common.Location;
using StaticContainerData = SPTarkov.Server.Core.Models.Eft.Common.StaticContainerData;
using SPTarkov.Server.Core.Models.Spt.Config;
using SPTarkov.Server.Core.Models.Spt.Tables;

namespace Tushonkapedia.Server;

// Everything the page knows about an item: where it spawns, who sells it, what crafts it, which quests give or
// want it, and what the hideout needs it for. All of it is read from the server's own tables after every mod has
// loaded, so modded items and mods that change loot or traders are included.
//
// Loot is the slow part (the loose loot of a big map is tens of MB), so it's worked out once in the background
// and saved to data/loot-index.json; the saved copy is used straight away the next time the server starts.
public sealed partial class ItemWiki(ISptLogger<WikiRoute> logger, TemplateTable templates, LocationTable locations, TradersTable traders,
    HideoutTable hideout, LocationConfig locationConfig, BotTable bots, AirdropConfig airdrops, Func<string, bool> offSeason, string folder,
    Func<object?> stimBuffs)
{
    // the server's base currency: what its handbook prices are in
    public static readonly string Roubles = CurrencyType.RUB.GetCurrencyTpl().ToString();
    private static readonly string Fence = Traders.FENCE.ToString();
    // item cases, key tools and the like hang under this node in the item tree
    private static readonly string SimpleContainer = BaseClasses.SIMPLE_CONTAINER.ToString();
    private const string LootFile = "loot-index.json";

    private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    private readonly Lock _lock = new();
    private LootIndex? _loot;
    private Trade? _trade;
    private HashSet<string>? _baseGame;
    private int _building;

    // per item, per location: expected copies per raid on the ground and in each kind of container
    public sealed class LootIndex
    {
        // bumped when the sums change, so an older saved index is built again
        public const int Current = 4;
        public int Version { get; init; }
        public DateTime BuiltAt { get; init; }
        // what the loot tables were worked out from (see WikiRoute.LootFingerprint): the same next start, no rebuild
        public string Fingerprint { get; init; } = "";
        // location key -> container type -> how many of them spawn in a raid, on average
        public Dictionary<string, Dictionary<string, double>> Placed { get; init; } = [];
        // location key -> location id (for its display name)
        public Dictionary<string, string> Locations { get; init; } = [];
        public Dictionary<string, List<LootSpot>> Items { get; init; } = [];
    }

    public sealed class LootSpot
    {
        public string Location { get; init; } = "";
        public double Loose { get; set; }
        public int Points { get; set; }
        public Dictionary<string, double> Containers { get; init; } = [];
        public double Total => Loose + Containers.Values.Sum();
    }

    public bool LootReady => _loot != null;

    // ---------- loot ----------

    // Uses the saved loot index and loot maps when they were worked out from the same loot tables (the fingerprint
    // matches), and only reads SPT's loot again otherwise: that read is the mod's one heavy job.
    public void StartLoot(string fingerprint)
    {
        _loot ??= ReadLoot();
        if (Interlocked.Exchange(ref _building, 1) == 1)
        {
            return;
        }

        Task.Run(async () =>
        {
            try
            {
                var started = DateTime.UtcNow;
                if (_loot?.Fingerprint == fingerprint && ReadMapLoot(fingerprint) is { } saved)
                {
                    _mapLoot = new ConcurrentDictionary<string, MapLoot>(saved, StringComparer.OrdinalIgnoreCase);
                    logger.Info($"[Tushonkapedia] Loot index: {_loot.Items.Count} items on {_loot.Locations.Count} maps, saved (the loot tables haven't changed).");
                    await GiveBackMemory();
                    return;
                }

                var maps = new List<MapLoot>();
                var index = BuildLoot(fingerprint, maps);
                _loot = index;
                _mapLoot = new ConcurrentDictionary<string, MapLoot>(maps.ToDictionary(m => m.Key, StringComparer.OrdinalIgnoreCase), StringComparer.OrdinalIgnoreCase);
                Directory.CreateDirectory(Path.Combine(folder, "data"));
                File.WriteAllText(Path.Combine(folder, "data", LootFile), JsonSerializer.Serialize(index, Json));
                SaveMapLoot(maps, fingerprint);
                logger.Info($"[Tushonkapedia] Loot index: {index.Items.Count} items on {index.Locations.Count} maps ({(DateTime.UtcNow - started).TotalSeconds:0}s).");
                await GiveBackMemory();
            }
            catch (Exception ex)
            {
                logger.Warning($"[Tushonkapedia] Couldn't work out where items spawn: {ex.Message} ({ex.StackTrace?.Split('\n').FirstOrDefault(l => l.Contains("Tushonkapedia"))?.Trim()})");
            }
            finally
            {
                Interlocked.Exchange(ref _building, 0);
            }
        });
    }

    // Reading every map's loot leaves a GB or more of garbage (reading the saved files, some tens of MB), and .NET
    // keeps the memory it freed for later, as do the buffers SPT borrowed for reading, which sit in .NET's shared pool
    // until they've gone unused for a minute and a full collection comes along. So: three compacting collections that
    // hand it all back to Windows, now and twice a minute or so apart, while the server starts. Each is a short pause.
    // This also gives back what SPT's own start left behind: the server ends up smaller than without the mod.
    private static async Task GiveBackMemory()
    {
        for (var round = 0; round < 3; round++)
        {
            if (round > 0)
            {
                await Task.Delay(TimeSpan.FromSeconds(70));
            }

            GCSettings.LargeObjectHeapCompactionMode = GCLargeObjectHeapCompactionMode.CompactOnce;
            GC.Collect(GC.MaxGeneration, GCCollectionMode.Aggressive, blocking: true, compacting: true);
        }
    }

    private LootIndex? ReadLoot()
    {
        try
        {
            var file = Path.Combine(folder, "data", LootFile);
            var index = File.Exists(file) ? JsonSerializer.Deserialize<LootIndex>(File.ReadAllText(file), Json) : null;
            return index?.Version == LootIndex.Current ? index : null;
        }
        catch
        {
            return null;
        }
    }

    // How many of each item a raid holds on average. Loose loot: SPT picks about `mean` spawn points, weighted by
    // their probability, then one item from each by its weights. Containers: each placed container (by its own
    // chance) gets an average number of items, each by its weights. The map's loot multipliers are applied.
    private LootIndex BuildLoot(string fingerprint, List<MapLoot> mapLoot)
    {
        var index = new LootIndex { Version = LootIndex.Current, BuiltAt = DateTime.UtcNow, Fingerprint = fingerprint };
        var byItem = new Dictionary<string, Dictionary<string, LootSpot>>();
        LootSpot Spot(string item, string location)
        {
            if (!byItem.TryGetValue(item, out var maps))
            {
                byItem[item] = maps = [];
            }

            return maps.TryGetValue(location, out var spot) ? spot : maps[location] = new LootSpot { Location = location };
        }

        foreach (var (key, location) in locations.GetDictionary())
        {
            var id = location?.Base?.Id;
            if (location?.Base == null || string.IsNullOrEmpty(id) || key.Equals("hideout", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var looseMultiplier = Multiplier(locationConfig.LooseLootMultiplier, id);
            var staticMultiplier = Multiplier(locationConfig.StaticLootMultiplier, id);
            var any = false;

            var loose = location.LooseLoot?.Value;
            // the loot map's copy, from this same read
            mapLoot.Add(MapLootOf(location, loose));
            var spawnpoints = loose?.Spawnpoints?.ToList() ?? [];
            if (spawnpoints.Count > 0)
            {
                any = true;
                var mean = loose!.SpawnpointCount?.Mean ?? spawnpoints.Count;
                var sum = spawnpoints.Sum(s => s.Probability ?? 0);
                foreach (var point in spawnpoints)
                {
                    var chosen = sum > 0 ? Math.Min(1, mean * (point.Probability ?? 0) / sum) * looseMultiplier : 0;
                    // a spawn point's items by their key; a mod may list one key twice, the first one counts
                    var items = new Dictionary<string, string>(StringComparer.Ordinal);
                    foreach (var i in point.Template?.Items ?? [])
                    {
                        if (i.ComposedKey != null)
                        {
                            items.TryAdd(i.ComposedKey, i.Template.ToString());
                        }
                    }

                    var weights = (point.ItemDistribution ?? []).Where(d => d.ComposedKey?.Key != null && items.ContainsKey(d.ComposedKey.Key)).ToList();
                    var total = weights.Sum(w => w.RelativeProbability ?? 0);
                    if (total <= 0 || chosen <= 0)
                    {
                        continue;
                    }

                    foreach (var weight in weights)
                    {
                        var spot = Spot(items[weight.ComposedKey!.Key!], id);
                        spot.Loose += chosen * (weight.RelativeProbability ?? 0) / total;
                        spot.Points++;
                    }
                }

                foreach (var point in loose.SpawnpointsForced ?? [])
                {
                    var root = point.Template?.Items?.FirstOrDefault(i => string.IsNullOrEmpty(i.ParentId));
                    if (root != null)
                    {
                        var spot = Spot(root.Template.ToString(), id);
                        spot.Loose += 1;
                        spot.Points++;
                    }
                }
            }

            var containers = location.StaticContainers?.Value;
            var odds = location.StaticLoot?.Value;
            if (containers != null && odds != null)
            {
                any = true;
                var placed = Placed(key, id, location, containers.StaticContainers ?? []);
                index.Placed[id] = placed.Where(p => p.Value >= 0.05).ToDictionary(p => p.Key, p => Math.Round(p.Value, 1));

                foreach (var (tpl, count) in placed)
                {
                    if (!odds.TryGetValue(new MongoId(tpl), out var details))
                    {
                        continue;
                    }

                    var counts = (details.ItemCountDistribution ?? []).ToList();
                    var countTotal = counts.Sum(c => c.RelativeProbability ?? 0);
                    var average = countTotal > 0 ? counts.Sum(c => (c.Count ?? 0) * (c.RelativeProbability ?? 0)) / countTotal : 0;
                    var items = (details.ItemDistribution ?? []).ToList();
                    var total = items.Sum(i => i.RelativeProbability ?? 0);
                    if (total <= 0 || average <= 0)
                    {
                        continue;
                    }

                    foreach (var item in items)
                    {
                        var spot = Spot(item.Tpl.ToString(), id);
                        spot.Containers[tpl] = spot.Containers.GetValueOrDefault(tpl) + count * average * (item.RelativeProbability ?? 0) / total * staticMultiplier;
                    }
                }

                foreach (var forced in containers.StaticForced ?? [])
                {
                    var spot = Spot(forced.ItemTpl.ToString(), id);
                    spot.Containers["forced"] = spot.Containers.GetValueOrDefault("forced") + 1;
                }
            }

            if (any)
            {
                index.Locations[id] = location.Base.IdField.ToString();
            }
        }

        foreach (var (item, maps) in byItem)
        {
            index.Items[item] = maps.Values.Where(s => s.Total > 0.0005)
                .Select(s => new LootSpot
                {
                    Location = s.Location, Loose = Math.Round(s.Loose, 4), Points = s.Points,
                    Containers = s.Containers.Where(c => c.Value > 0.0005).ToDictionary(c => c.Key, c => Math.Round(c.Value, 4))
                })
                .OrderByDescending(s => s.Total)
                .ToList();
        }

        return index;
    }

    // How many containers of each type spawn in a raid, on average, the way SPT picks them: a container with a 100%
    // chance (or of a type SPT never randomises) is always there. With container randomisation on, the rest come in
    // groups: SPT spawns between the group's min and max of them, drawn by their chances; a container in no group
    // rolls its own chance. With randomisation off, every one of them spawns.
    private Dictionary<string, double> Placed(string key, string id, Location location, IEnumerable<StaticContainerData> containers)
    {
        var settings = locationConfig.ContainerRandomisationSettings;
        var randomised = settings.Enabled && settings.Maps.Keys.Any(m => m.Equals(key, StringComparison.OrdinalIgnoreCase) || m.Equals(id, StringComparison.OrdinalIgnoreCase));
        var groups = location.Statics?.ContainersGroups;
        var members = location.Statics?.Containers;
        var placed = new Dictionary<string, double>();
        var grouped = new Dictionary<string, List<(string Type, double Chance)>>();

        foreach (var container in containers)
        {
            var type = container.Template?.Items?.FirstOrDefault()?.Template.ToString();
            // Christmas crates only stand on the maps while Christmas runs
            if (type == null || (container.Template?.Id is { } placed1 && offSeason(placed1)))
            {
                continue;
            }

            var chance = (double)(container.Probability ?? 1);
            if (chance >= 1 || container.Template?.IsAlwaysSpawn == true || settings.ContainerTypesToNotRandomise.Contains(new MongoId(type)))
            {
                placed[type] = placed.GetValueOrDefault(type) + 1;
                continue;
            }

            if (!randomised)
            {
                placed[type] = placed.GetValueOrDefault(type) + 1;
                continue;
            }

            var group = members != null && container.Template?.Id is { } cid && members.TryGetValue(cid, out var member) ? member.GroupId ?? "" : "";
            if (!grouped.TryGetValue(group, out var list))
            {
                grouped[group] = list = [];
            }

            list.Add((type, chance));
        }

        foreach (var (group, list) in grouped)
        {
            var sum = list.Sum(c => c.Chance);
            double wanted;
            if (group == "")
            {
                wanted = sum;
            }
            else if (groups != null && groups.TryGetValue(group, out var size))
            {
                var min = Math.Round((size.MinContainers ?? 0) * settings.ContainerGroupMinSizeMultiplier);
                var max = Math.Round((size.MaxContainers ?? 0) * settings.ContainerGroupMaxSizeMultiplier);
                wanted = (min + max) / 2;
            }
            else
            {
                continue;
            }

            foreach (var (type, chance) in list)
            {
                var spawns = group == "" ? chance : sum > 0 ? Math.Min(1, wanted * chance / sum) : 0;
                placed[type] = placed.GetValueOrDefault(type) + spawns;
            }
        }

        return placed;
    }

    private static double Multiplier(Dictionary<string, double>? multipliers, string location)
    {
        if (multipliers == null)
        {
            return 1;
        }

        foreach (var (key, value) in multipliers)
        {
            if (key.Equals(location, StringComparison.OrdinalIgnoreCase))
            {
                return value;
            }
        }

        return 1;
    }

    // ---------- traders, crafts, hideout ----------

    public sealed record Offer(string Trader, int Level, string? Currency, double Price, List<(string Item, double Count)> Barter, string? Quest, string? QuestState, int? Limit, double Count, string Item);

    public sealed record Craft(string Item, double Count, int Area, int AreaLevel, double Seconds, List<(string Item, double Count, bool Tool)> Inputs, string? Quest, string Id = "");

    public sealed record Upgrade(int Area, int Level, string Item, double Count, bool Fir);

    private sealed class Trade
    {
        public List<Offer> Offers { get; } = [];
        public List<Craft> Crafts { get; } = [];
        public List<Upgrade> Upgrades { get; } = [];
    }

    // Built on first use and again on request; traders' stock lists and recipes don't change while the server runs.
    private Trade Trades()
    {
        lock (_lock)
        {
            return _trade ??= BuildTrade();
        }
    }

    public void Forget()
    {
        lock (_lock)
        {
            _trade = null;
        }
    }

    private Trade BuildTrade()
    {
        var trade = new Trade();
        foreach (var (traderId, trader) in traders)
        {
            var tid = traderId.ToString();
            var assort = trader?.Assort;
            if (assort?.Items == null || tid == Fence)
            {
                continue;
            }

            // assort id -> (quest, state) that opens it
            var locks = new Dictionary<string, (string Quest, string State)>();
            foreach (var (state, entries) in trader!.QuestAssort ?? [])
            {
                foreach (var (assortId, questId) in entries ?? [])
                {
                    locks[assortId.ToString()] = (questId.ToString(), state);
                }
            }

            foreach (var item in assort.Items.Where(i => i.ParentId == "hideout"))
            {
                var aid = item.Id.ToString();
                if (!assort.BarterScheme.TryGetValue(item.Id, out var schemes) || schemes.Count == 0 || schemes[0].Count == 0)
                {
                    continue;
                }

                var scheme = schemes[0];
                var level = assort.LoyalLevelItems.TryGetValue(item.Id, out var ll) ? ll : 1;
                var cash = scheme.Count == 1 && IsCurrency(scheme[0].Template.ToString()) ? scheme[0] : null;
                var quest = locks.TryGetValue(aid, out var found) ? found : ((string, string)?)null;
                trade.Offers.Add(new Offer(
                    tid, level,
                    cash?.Template.ToString(), cash?.Count ?? 0,
                    // items are handed over whole: a fractional count in the server's table (Ref's GP coin prices) is paid rounded up
                    cash == null ? scheme.Select(b => (b.Template.ToString(), Math.Ceiling(b.Count ?? 1))).ToList() : [],
                    quest?.Item1, quest?.Item2,
                    item.Upd?.BuyRestrictionMax,
                    1,
                    item.Template.ToString()));
            }
        }

        foreach (var recipe in hideout.Production?.Recipes ?? [])
        {
            if (recipe == null)
            {
                continue;
            }

            var area = recipe.Requirements?.FirstOrDefault(r => r.Type == "Area");
            trade.Crafts.Add(new Craft(
                recipe.EndProduct.ToString(), recipe.Count ?? 1,
                (int)(recipe.AreaType ?? 0), area?.RequiredLevel ?? 1,
                recipe.ProductionTime ?? 0,
                (recipe.Requirements ?? []).Where(r => r.Type is "Item" or "Tool" && r.TemplateId != null)
                    .Select(r => (r.TemplateId!.Value.ToString(), (double)(r.Count ?? 1), r.Type == "Tool")).ToList(),
                recipe.Requirements?.FirstOrDefault(r => r.Type == "QuestComplete")?.QuestId?.ToString(), recipe.Id.ToString()));
        }

        foreach (var area in hideout.Areas ?? [])
        {
            foreach (var (level, stage) in area?.Stages ?? [])
            {
                foreach (var requirement in stage?.Requirements ?? [])
                {
                    if (requirement?.Type == "Item" && int.TryParse(level, out var lvl))
                    {
                        trade.Upgrades.Add(new Upgrade((int)(area!.Type ?? 0), lvl, requirement.TemplateId.ToString(), requirement.Count ?? 1, requirement.IsSpawnedInSession == true));
                    }
                }
            }
        }

        return trade;
    }

    // ---------- items ----------

    // ids in SPT's own item file: everything else was added by a mod
    private bool BaseGame(string id)
    {
        if (_baseGame == null)
        {
            var set = new HashSet<string>();
            try
            {
                var file = Path.Combine(AppContext.BaseDirectory, "SPT_Data", "database", "templates", "items.json");
                using var stream = File.OpenRead(file);
                using var json = JsonDocument.Parse(stream);
                foreach (var entry in json.RootElement.EnumerateObject())
                {
                    set.Add(entry.Name);
                }
            }
            catch (Exception ex)
            {
                logger.Warning($"[Tushonkapedia] Couldn't read SPT's item list, so modded items aren't marked: {ex.Message}");
            }

            _baseGame = set;
        }

        return _baseGame.Count == 0 || _baseGame.Contains(id);
    }

    public double Price(string id)
    {
        var mongo = new MongoId(id);
        if (templates.Prices.TryGetValue(mongo, out var price) && price > 0)
        {
            return price;
        }

        return HandbookPrice(id);
    }

    private Dictionary<string, (double Price, string Category)>? _handbook;

    private double HandbookPrice(string id)
    {
        Handbook();
        return _handbook!.TryGetValue(id, out var entry) ? entry.Price : 0;
    }

    private Dictionary<string, (double Price, string Category)> Handbook()
    {
        return _handbook ??= (templates.Handbook?.Items ?? [])
            .GroupBy(i => i.Id.ToString())
            .ToDictionary(g => g.Key, g => (g.First().Price ?? 0, g.First().ParentId.ToString()));
    }

    // An item case, key tool, money case ...: a container that sits in the stash. Its worth is the room it adds:
    // its grid cells less the cells it takes up. 0 for anything else.
    public int CaseSpace(string id)
    {
        if (!templates.Items.TryGetValue(new MongoId(id), out var item) || item.Properties?.Grids?.Any() != true)
        {
            return 0;
        }

        var isCase = false;
        for (var (at, steps) = (item, 0); at != null && steps < 20; at = templates.Items.GetValueOrDefault(at.Parent), steps++)
        {
            if (at.Id.ToString() == SimpleContainer)
            {
                isCase = true;
                break;
            }
        }

        if (!isCase)
        {
            return 0;
        }

        var cells = item.Properties.Grids.Sum(g => (g.Properties?.CellsH ?? 0) * (g.Properties?.CellsV ?? 0));
        return Math.Max(0, cells - (item.Properties.Width ?? 1) * (item.Properties.Height ?? 1));
    }

    public bool IsCase(string id) => CaseSpace(id) > 0;

    public bool Exists(string id) => MongoId.IsValidMongoId(id) && templates.Items.TryGetValue(new MongoId(id), out var item) && item.Type == "Item";

    public List<object> Search(string query, IReadOnlyDictionary<string, string> locale, QuestGraph graph)
    {
        query = query.Trim();
        if (query.Length < 2)
        {
            return [];
        }

        // "ak103" finds the AK-103 and "103 ak" does too: letters and digits only, and each word typed can start
        // any word of the name ("ak103" counts as ak and 103 too)
        var whole = Plain(query);
        var words = query.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Select(Plain).Where(w => w.Length > 0).ToList();
        if (whole.Length == 0)
        {
            return [];
        }

        var found = templates.Items.Values
            .Where(i => i.Type == "Item" && !Hidden(i.Id.ToString(), graph))
            .Select(i => (Item: i, Name: locale.GetValueOrDefault(i.Id + " Name") ?? i.Name ?? "", Short: locale.GetValueOrDefault(i.Id + " ShortName") ?? ""))
            .Select(x => (x.Item, x.Name, x.Short, PlainName: Plain(x.Name), PlainShort: Plain(x.Short)))
            .Select(x => (x.Item, x.Name, x.Short, x.PlainName, x.PlainShort,
                Fit: x.PlainShort == whole || x.PlainName == whole ? 0
                    : x.PlainName.Contains(whole) || x.PlainShort.Contains(whole) || x.Item.Id.ToString() == query ? 1
                    : words.Count > 1 && Words(x.Name + " " + x.Short) is var parts && words.All(w => parts.Any(p => p.StartsWith(w))) ? 2
                    : 3))
            .Where(x => x.Fit < 3)
            // the closest names, and among those the gun before its parts: "M4A1" finds the rifle, then its receivers
            .OrderBy(x => x.Fit)
            .ThenBy(x => IsWeapon(x.Item.Id.ToString()) ? 0 : 1)
            .ThenBy(x => x.PlainName.StartsWith(whole) || x.PlainShort.StartsWith(whole) ? 0 : 1)
            .ThenBy(x => x.Name.Length)
            .Take(20)
            .ToList();

        // the game gives some items the same name (a "damaged" ammo pack next to the normal one); tell those apart by
        // the words their internal names don't share
        var same = found.GroupBy(x => x.Name).Where(g => g.Count() > 1).ToDictionary(g => g.Key, g => g.Select(x => x.Item).ToList());
        // a container that stands on maps (a duffle bag) opens its own page: every kind of that name, as the container
        // page takes them ("id,id"); one entry for the name
        var placed = (_loot?.Placed.Values.SelectMany(p => p.Keys) ?? []).ToHashSet();
        var boxes = placed.GroupBy(t => locale.GetValueOrDefault(t + " Name") ?? t).ToDictionary(g => g.Key, g => string.Join(",", g.Take(8)));
        return found
            .Where(x => !placed.Contains(x.Item.Id.ToString()) || found.First(y => y.Name == x.Name && placed.Contains(y.Item.Id.ToString())).Item.Id == x.Item.Id)
            .Select(x => (object)new
            {
                id = x.Item.Id.ToString(), name = x.Name, shortName = x.Short, category = Category(x.Item.Id.ToString(), locale),
                variant = same.TryGetValue(x.Name, out var twins) && !placed.Contains(x.Item.Id.ToString()) ? Variant(x.Item, twins) : null,
                box = placed.Contains(x.Item.Id.ToString()) ? boxes.GetValueOrDefault(x.Name) : null,
                maps = placed.Contains(x.Item.Id.ToString()) ? _loot!.Placed.Count(p => p.Value.Keys.Any(t => boxes.GetValueOrDefault(x.Name)?.Contains(t) == true)) : (int?)null
            })
            .ToList();
    }

    // "item_ammo_box_545x39_120_BP_damaged" next to "item_ammo_box_545x39_120_BP" gives "damaged"
    private static string? Variant(TemplateItem item, List<TemplateItem> twins)
    {
        var others = twins.Where(t => t.Id != item.Id).SelectMany(t => (t.Name ?? "").ToLowerInvariant().Split('_')).ToHashSet();
        var own = (item.Name ?? "").ToLowerInvariant().Split('_').Where(w => w.Length > 0 && !others.Contains(w)).ToList();
        return own.Count > 0 ? string.Join(' ', own) : null;
    }

    // lower case letters and digits only, for search: "AK-103" and "ak103" are the same
    private static string Plain(string text)
    {
        return string.Concat(text.Where(char.IsLetterOrDigit)).ToLowerInvariant();
    }

    // the words of a name, each also split where letters meet digits: "AK-103 7.62x39" gives ak103, ak, 103, 762x39, 762, x, 39
    private static List<string> Words(string text)
    {
        var words = new List<string>();
        foreach (var word in text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Select(Plain).Where(w => w.Length > 0))
        {
            words.Add(word);
            words.AddRange(LettersOrDigits().Matches(word).Select(m => m.Value));
        }

        return words;
    }

    [System.Text.RegularExpressions.GeneratedRegex(@"\p{L}+|\p{N}+")]
    private static partial System.Text.RegularExpressions.Regex LettersOrDigits();

    private string? Category(string id, IReadOnlyDictionary<string, string> locale)
    {
        return Handbook().TryGetValue(id, out var entry) ? locale.GetValueOrDefault(entry.Category) : null;
    }

    // Where each of these items can be had, short: the best maps, the cheapest trader offer, and whether it's
    // crafted. For the raid planner.
    public Dictionary<string, object> Where(IEnumerable<string> ids, QuestGraph graph, IReadOnlyDictionary<string, string> locale)
    {
        var trade = Trades();
        var result = new Dictionary<string, object>();
        foreach (var id in ids.Distinct().Where(Exists).Take(400))
        {
            result[id] = new
            {
                name = locale.GetValueOrDefault(id + " Name") ?? id,
                loot = LootFor(id, locale).Take(6),
                carriedBy = CarriedBy(id, locale).Take(4),
                buy = trade.Offers.Where(o => o.Item == id).OrderBy(o => o.Quest != null).ThenBy(o => o.Level).Take(3).Select(o => OfferOut(o, locale)),
                crafted = trade.Crafts.Any(c => c.Item == id)
            };
        }

        return result;
    }

    private IEnumerable<object> LootFor(string id, IReadOnlyDictionary<string, string> locale)
    {
        var loot = _loot;
        if (loot == null || !loot.Items.TryGetValue(id, out var spots))
        {
            return [];
        }

        return spots.Select(s => (object)new
        {
            location = s.Location,
            map = MapName(loot, s.Location, locale),
            total = Math.Round(s.Total, 4),
            loose = s.Loose,
            points = s.Points,
            // each: the chance one container of this type holds it; onMap: how many of them a raid has
            containers = s.Containers.OrderByDescending(c => c.Value).Take(5)
                .Select(c => (c, onMap: loot.Placed.GetValueOrDefault(s.Location)?.GetValueOrDefault(c.Key) ?? 0))
                .Select(x => new
                {
                    id = x.c.Key, name = x.c.Key == "forced" ? "Always there" : locale.GetValueOrDefault(x.c.Key + " Name") ?? x.c.Key, perRaid = x.c.Value,
                    onMap = Math.Round(x.onMap, 1), each = x.onMap > 0 && x.c.Key != "forced" ? Math.Round(Math.Min(1, x.c.Value / x.onMap), 5) : (double?)null
                })
        });
    }

    private static string MapName(LootIndex loot, string location, IReadOnlyDictionary<string, string> locale)
    {
        var id = loot.Locations.GetValueOrDefault(location) ?? location;
        var name = locale.GetValueOrDefault(id + " Name") ?? locale.GetValueOrDefault(location) ?? location;
        // the two versions of a map share a name; the night one and the high-level one get a note
        return location switch
        {
            "factory4_night" => name.Contains("Night", StringComparison.OrdinalIgnoreCase) ? name : name + " (night)",
            "Sandbox_high" => name + " (level 21+)",
            _ => name
        };
    }

    private static object OfferOut(Offer o, IReadOnlyDictionary<string, string> locale) => new
    {
        trader = o.Trader,
        level = o.Level,
        currency = o.Currency,
        price = o.Price,
        barter = o.Barter.Select(b => new { id = b.Item, count = b.Count }),
        quest = o.Quest,
        questState = o.QuestState,
        limit = o.Limit
    };

    // One item's page.
    public object? Item(string id, QuestGraph graph, IReadOnlyDictionary<string, string> locale, string lang = "en")
    {
        if (!Exists(id))
        {
            return null;
        }

        var template = templates.Items[new MongoId(id)];
        var props = template.Properties;
        var trade = Trades();
        var names = new Dictionary<string, string>();
        string Name(string item) => names[item] = locale.GetValueOrDefault(item + " Name") ?? (templates.Items.TryGetValue(new MongoId(item), out var t) ? t.Name ?? item : item);

        var buy = trade.Offers.Where(o => o.Item == id).OrderBy(o => o.Quest != null).ThenBy(o => o.Level).ToList();
        buy.SelectMany(o => o.Barter).ToList().ForEach(b => Name(b.Item));
        var crafts = trade.Crafts.Where(c => c.Item == id).ToList();
        crafts.SelectMany(c => c.Inputs).ToList().ForEach(i => Name(i.Item));

        var fromQuests = graph.Quests.SelectMany(q => q.Rewards
                .Where(r => r.Item == id && r.Kind is "Item" or "AssortmentUnlock" or "ProductionScheme")
                .Select(r => new { quest = q.Id, kind = r.Kind, when = r.When, count = r.Count }))
            .ToList();
        var questUses = graph.Quests.SelectMany(q => q.Objectives
                .Where(o => o.Items.Contains(id))
                .Select(o => new { quest = q.Id, kind = o.Kind, count = o.Count, fir = o.Fir }))
            .Concat(graph.Quests.SelectMany(q => q.Needs
                .Where(n => n.Ids.Contains(id) && n.Kind is "key" or "wear" or "weapon" or "mods")
                .Select(n => new { quest = q.Id, kind = n.Kind, count = (double?)null, fir = false })))
            // a quest that lists the same item in several objectives (sell 50 of any of these ...) shows once
            .DistinctBy(u => (u.quest, u.kind))
            .ToList();

        var craftUses = trade.Crafts.Where(c => c.Inputs.Any(i => i.Item == id)).ToList();
        craftUses.ForEach(c => Name(c.Item));
        var barterUses = trade.Offers.Where(o => o.Barter.Any(b => b.Item == id)).ToList();
        barterUses.ForEach(o => Name(o.Item));
        var upgrades = trade.Upgrades.Where(u => u.Item == id).ToList();

        var loot = LootFor(id, locale).ToList();
        return new
        {
            id,
            name = Name(id),
            shortName = locale.GetValueOrDefault(id + " ShortName"),
            description = locale.GetValueOrDefault(id + " Description"),
            category = Category(id, locale),
            categoryId = Handbook().TryGetValue(id, out var book) ? book.Category : null,
            modded = !BaseGame(id),
            mod = ModOf(id, false),
            unused = Reachable(graph) is { } reachable && Leftover(id, reachable, locale),
            startKit = StartingKit().Contains(id),
            changed = ItemChanges(id),
            grids = (props?.Grids ?? []).Select(g => new { w = g.Properties?.CellsH, h = g.Properties?.CellsV }),
            // where each part sits in the game, when the plugin has measured its layout
            gridLayout = GridLayout(template),
            width = props?.Width,
            height = props?.Height,
            weight = props?.Weight,
            stack = props?.StackMaxSize,
            uses = props?.MaximumNumberOfUsage,
            rarity = props?.RarityPvE,
            questItem = props?.QuestItem == true,
            flea = props?.CanSellOnRagfair,
            price = Price(id),
            handbookPrice = HandbookPrice(id),
            sellTo = SellTo(id),
            isCase = IsCase(id),
            lootReady = _loot != null,
            loot,
            carriedBy = CarriedBy(id, locale),
            // a key: the maps whose doors or locks it opens, and how many there
            opens = KeyOpens(id, locale),
            stats = Stats(id, locale),
            // armour, a plate or a carrier: class, plates and what it fits in, as the armour chart has it
            armor = ArmorFor(id, locale, lang),
            // an ammo pack: the round inside and how many
            unpacks = Unpacks(id, locale, names),
            // a part or round: the guns and parts it goes on
            fitsOn = FitsOn(id, locale, names),
            // a gun: its rounds, magazine, mod slots and builds
            weapon = WeaponFor(id, locale, names),
            ammo = AmmoFor(id, locale, names),
            med = MedsFor(id, locale, names),
            // a case, bag, rig or anything with room inside: what it takes, what it fits in, and others of its kind
            container = ContainerFor(id, locale, names),
            airdrops = Airdrops(id),
            buy = buy.Select(o => OfferOut(o, locale)),
            craft = crafts.Select(c => new
            {
                area = c.Area, areaName = locale.GetValueOrDefault($"hideout_area_{c.Area}_name"), level = c.AreaLevel, count = c.Count,
                hours = Math.Round(c.Seconds / 3600, 2), quest = c.Quest,
                inputs = c.Inputs.Select(i => new { id = i.Item, count = i.Count, tool = i.Tool })
            }),
            fromQuests,
            usedIn = new
            {
                quests = questUses,
                hideout = upgrades.Select(u => new { area = u.Area, areaName = locale.GetValueOrDefault($"hideout_area_{u.Area}_name"), level = u.Level, count = u.Count, fir = u.Fir }),
                crafts = craftUses.Select(c => new { output = c.Item, count = c.Count, area = c.Area, areaName = locale.GetValueOrDefault($"hideout_area_{c.Area}_name"), level = c.AreaLevel, need = c.Inputs.First(i => i.Item == id).Count, tool = c.Inputs.First(i => i.Item == id).Tool }),
                barters = barterUses.Select(o => new { output = o.Item, trader = o.Trader, level = o.Level, need = o.Barter.First(b => b.Item == id).Count, quest = o.Quest })
            },
            items = names
        };
    }
}
