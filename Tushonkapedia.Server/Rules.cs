using SPTarkov.Server.Core.Models.Enums;
using System.Text.Json;
using System.Text.Json.Nodes;
using SPTarkov.Server.Core.Models.Common;
using SPTarkov.Server.Core.Models.Spt.Config;
using SPTarkov.Server.Core.Models.Spt.Tables;
using SPTarkov.Server.Core.Services.Server;
using Slot = SPTarkov.Server.Core.Models.Eft.Common.Tables.Slot;
using TemplateItem = SPTarkov.Server.Core.Models.Eft.Common.Tables.TemplateItem;

namespace Tushonkapedia.Server;

// The server's settings that a player feels, one page each: insurance, the flea market, repairs, ammo and armour,
// skills, levels, health, Fence and scav karma, achievements, prestige, events, the Cultist Circle
// and containers. Every figure is read from SPT's tables and configs as they are now, after every mod has changed
// them, so a mod that changes insurance or the flea shows here as it plays.
public sealed partial class ItemWiki
{
    // what the rule pages read, handed over by the route (the item tables don't need them)
    public sealed record RuleSources(GlobalTable Globals, InsuranceConfig Insurance, RagfairConfig Ragfair, RepairConfig Repair,
        TraderConfig Trader, HideoutConfig Hideout, SeasonalEventConfig Seasons, SeasonalEventService SeasonService);

    public RuleSources? Rules { get; set; }

    private static readonly string AmmoNode = BaseClasses.AMMO.ToString();
    private static readonly string SecureContainerNode = BaseClasses.MOB_CONTAINER.ToString();
    private static readonly string RepairKitNode = BaseClasses.REPAIR_KITS.ToString();
    private static readonly string ItemRoot = BaseClasses.ITEM.ToString();

    public static readonly string[] RulePages =
        ["insurance", "flea", "repairs", "ammo", "armor", "skills", "levels", "health", "karma", "achievements", "prestige", "events", "circle", "containers", "traders"];

    private readonly Dictionary<string, (DateTime At, object Page)> _rules = [];
    private static readonly HashSet<string> ItemCharts = ["ammo", "armor", "containers", "achievements"];

    public object? Rule(string page, IReadOnlyDictionary<string, string> locale, string lang)
    {
        if (Rules is not { } r || !RulePages.Contains(page))
        {
            return null;
        }

        var key = page + "|" + lang;
        lock (_rules)
        {
            // the item charts come from the item table, which doesn't change while the server runs; the settings pages
            // are cheap, and a minute keeps a mod's change at runtime honest
            if (_rules.TryGetValue(key, out var cached) && (ItemCharts.Contains(page) || DateTime.UtcNow - cached.At < TimeSpan.FromMinutes(1)))
            {
                return cached.Page;
            }
        }

        var config = Node(r.Globals.Configuration) as JsonObject ?? [];
        object built = page switch
        {
            "insurance" => InsurancePage(r, config, locale),
            "flea" => FleaPage(r, config, locale),
            "repairs" => RepairsPage(r, config, locale),
            "ammo" => AmmoPage(locale),
            "armor" => ArmorPage(config, locale),
            "skills" => SkillsPage(config, locale),
            "levels" => LevelsPage(r, config, locale),
            "health" => HealthPage(config, locale),
            "karma" => KarmaPage(r, config, locale),
            "achievements" => AchievementsPage(locale),
            "prestige" => PrestigePage(locale),
            "events" => EventsPage(r),
            "circle" => CirclePage(r, locale),
            "traders" => TradersPage(r, locale),
            _ => ContainersPage(locale)
        };
        lock (_rules)
        {
            _rules[key] = (DateTime.UtcNow, built);
        }

        return built;
    }

    // ---------- helpers ----------

    private static JsonNode? Node(object? value)
    {
        if (value == null)
        {
            return null;
        }

        try
        {
            // SPT's own options: its id and enum types need its converters
            return JsonSerializer.SerializeToNode(value, value.GetType(), SPTarkov.Server.Core.Utils.JsonUtil.JsonSerializerOptionsNoIndent);
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException or InvalidOperationException)
        {
            return null;
        }
    }

    private string ItemName(string id, IReadOnlyDictionary<string, string> locale) =>
        locale.GetValueOrDefault(id + " Name") ?? (MongoId.IsValidMongoId(id) && templates.Items.TryGetValue(new MongoId(id), out var t) ? t.Name : null) ?? id;

    private string TraderNick(string id, IReadOnlyDictionary<string, string> locale) =>
        locale.GetValueOrDefault(id + " Nickname") ?? (MongoId.IsValidMongoId(id) && traders.TryGetValue(new MongoId(id), out var t) ? t?.Base?.Nickname : null) ?? id;

    // traders a player deals with: those with a loyalty table, not the hidden service ones
    private IEnumerable<(string Id, SPTarkov.Server.Core.Models.Eft.Common.Tables.TraderBase Base)> PlayerTraders() =>
        traders.Where(t => t.Value?.Base?.LoyaltyLevels is { Count: > 0 } && t.Value.Base.Nickname is { Length: > 0 } && t.Value.Base.Nickname != "caretaker")
            .Select(t => (t.Key.ToString(), t.Value!.Base!));

    private object ItemRow(string id, IReadOnlyDictionary<string, string> locale) => new
    {
        id, name = ItemName(id, locale), shortName = locale.GetValueOrDefault(id + " ShortName"), price = Math.Round(Price(id)), modded = !BaseGame(id)
    };

    // ---------- traders ----------

    // Every trader side by side: what they sell, how often they restock, what services they offer and what each
    // loyalty level needs
    private object TradersPage(RuleSources r, IReadOnlyDictionary<string, string> locale)
    {
        var offers = Trades().Offers;
        return PlayerTraders().Select(t =>
        {
            var mine = offers.Where(o => o.Trader == t.Id).ToList();
            var restock = r.Trader.UpdateTime?.FirstOrDefault(u => u.TraderId.ToString() == t.Id)?.Seconds;
            return new
            {
                id = t.Id,
                name = TraderNick(t.Id, locale),
                location = TraderWhere(t.Id, locale),
                // the server makes this one's stock as it goes, so it has no fixed offers to count
                randomStock = t.Id == Fence,
                avatar = t.Base.Avatar,
                currency = t.Base.Currency?.ToString(),
                offers = mine.Count,
                barters = mine.Count(o => o.Barter.Count > 0 && o.Currency == null),
                questLocked = mine.Count(o => o.Quest != null),
                restock = restock == null ? null : new { min = restock.Min, max = restock.Max },
                insures = t.Base.Insurance?.Availability == true,
                repairs = t.Base.Repair?.Availability == true,
                heals = t.Base.Medic == true,
                levels = t.Base.LoyaltyLevels!.Select((l, i) => new { level = i + 1, minLevel = l.MinLevel, standing = l.MinStanding, sales = l.MinSalesSum }).ToList()
            };
        }).ToList();
    }

    // ---------- insurance ----------

    private object InsurancePage(RuleSources r, JsonObject config, IReadOnlyDictionary<string, string> locale)
    {
        var ins = config["Insurance"];
        return new
        {
            traders = PlayerTraders().Where(t => t.Base.Insurance?.Availability == true).Select(t => new
            {
                id = t.Id,
                name = TraderNick(t.Id, locale),
                minHours = t.Base.Insurance!.MinReturnHour,
                maxHours = t.Base.Insurance.MaxReturnHour,
                storageHours = t.Base.Insurance.MaxStorageTime,
                minPayment = t.Base.Insurance.MinPayment,
                returnChance = r.Insurance.ReturnChancePercent.TryGetValue(new MongoId(t.Id), out var c) ? c : (double?)null,
                excluded = (t.Base.Insurance.ExcludedCategory ?? []).Select(x => ItemName(x.ToString(), locale)).ToList(),
                levels = t.Base.LoyaltyLevels!.Select((l, i) => new { level = i + 1, coef = l.InsurancePriceCoefficient }).ToList()
            }).ToList(),
            returnOverrideSeconds = r.Insurance.ReturnTimeOverrideSeconds,
            storageOverrideSeconds = r.Insurance.StorageTimeOverrideSeconds,
            noAttachmentsTakenPercent = r.Insurance.ChanceNoAttachmentsTakenPercent,
            minAttachmentPrice = r.Insurance.MinAttachmentRoublePriceToBeTaken,
            attachmentsTaken = r.Insurance.SimulateItemsBeingTaken,
            checkSeconds = r.Insurance.RunIntervalSeconds,
            markOfUnknown = Num(ins?["CoefOfHavingMarkOfUnknown"]),
            onlyOnDeath = ins?["OnlyInDeathCase"]?.GetValue<bool>(),
            mailHours = Num(ins?["MaxStorageTimeInHour"]),
            editions = ins?["EditionSendingMessageTime"] is JsonObject eds ? eds.ToDictionary(e => e.Key, e => Num(e.Value?["multiplier"])) : null
        };
    }

    // ---------- flea market ----------

    private object FleaPage(RuleSources r, JsonObject config, IReadOnlyDictionary<string, string> locale)
    {
        var fair = config["RagFair"] as JsonObject ?? [];
        var spt = Node(r.Ragfair) as JsonObject ?? [];
        var tiers = spt["tieredFlea"] as JsonObject;
        var named = (JsonNode? map) => (map as JsonObject ?? []).Select(e => new { id = e.Key, name = ItemName(e.Key, locale), level = Num(e.Value) })
            .OrderBy(x => x.level).ThenBy(x => x.name).ToList();
        var banned = templates.Items.Where(t => t.Value.Type == "Item" && t.Value.Properties?.CanSellOnRagfair == false && Handbook().ContainsKey(t.Key.ToString()))
            .Select(t => t.Key.ToString()).ToList();
        return new
        {
            enabled = fair["enabled"]?.GetValue<bool>(),
            minLevel = Num(fair["minUserLevel"]),
            foundInRaidOnly = fair["isOnlyFoundInRaidAllowed"]?.GetValue<bool>(),
            tax = new { community = Num(fair["communityTax"]), item = Num(fair["communityItemTax"]), requirement = Num(fair["communityRequirementTax"]), multiplier = Num(spt["offerListingTaxMultiplier"]) },
            offerHours = Num(fair["offerDurationTimeInHour"]),
            renewHours = Num(fair["maxRenewOfferTimeInHour"]),
            renewPricePerHour = Num(fair["renewPricePerHour"]),
            unsoldStorageHours = Num(fair["yourOfferDidNotSellMaxStorageTimeInHour"]),
            soldStorageHours = Num(fair["youSellOfferMaxStorageTimeInHour"]),
            rating = new { up = Num(fair["ratingIncreaseCount"]), down = Num(fair["ratingDecreaseCount"]), upPer = Num(fair["ratingSumForIncrease"]), downPer = Num(fair["ratingSumForDecrease"]) },
            offerSlots = (fair["maxActiveOfferCount"] as JsonArray ?? []).Select(s => new { from = Num(s?["from"]), to = Num(s?["to"]), count = Num(s?["count"]) }).ToList(),
            limits = (fair["ItemRestrictions"] as JsonArray ?? []).Select(x => x?["TemplateId"]?.ToString() ?? "").Where(id => id.Length > 0)
                .Select((id, i) => new { id, name = ItemName(id, locale), max = Num((fair["ItemRestrictions"] as JsonArray)![i]?["MaxFlea"]) }).ToList(),
            sell = spt["sell"],
            purchasesFoundInRaid = spt["dynamic"]?["purchasesAreFoundInRaid"]?.GetValue<bool>(),
            tiered = tiers?["enabled"]?.GetValue<bool>() == true ? new
            {
                items = named(tiers["unlocksTpl"]),
                ammo = tiers["ammoTiersEnabled"]?.GetValue<bool>() == true ? named(tiers["ammoTplUnlocks"]) : null,
                categories = (tiers["unlocksType"] as JsonObject ?? []).Select(e => new { id = e.Key, name = ItemName(e.Key, locale), level = Num(e.Value) }).OrderBy(x => x.level).ToList()
            } : null,
            banned = banned.Select(id => new { id, name = ItemName(id, locale) }).OrderBy(x => x.name).ToList()
        };
    }

    // ---------- repairs ----------

    private object RepairsPage(RuleSources r, JsonObject config, IReadOnlyDictionary<string, string> locale)
    {
        var settings = config["RepairSettings"];
        return new
        {
            traders = PlayerTraders().Where(t => t.Base.Repair?.Availability == true).Select(t => new
            {
                id = t.Id,
                name = TraderNick(t.Id, locale),
                currency = ItemName(t.Base.Repair!.Currency ?? Roubles, locale),
                quality = t.Base.Repair.Quality,
                currencyCoef = t.Base.Repair.CurrencyCoefficient,
                excluded = (t.Base.Repair.ExcludedCategory ?? []).Select(x => ItemName(x.ToString(), locale))
                    .Concat((t.Base.Repair.ExcludedIdList ?? []).Select(x => ItemName(x, locale))).ToList(),
                levels = t.Base.LoyaltyLevels!.Select((l, i) => new { level = i + 1, coef = l.RepairPriceCoefficient }).ToList()
            }).ToList(),
            priceMultiplier = r.Repair.PriceMultiplier,
            randomLoss = r.Repair.ApplyRandomizeDurabilityLoss,
            armorPointCost = Num(settings?["durabilityPointCostArmor"]),
            gunPointCost = Num(settings?["durabilityPointCostGuns"]),
            armorClassDivisor = Num(settings?["armorClassDivisor"]),
            buffLevel = Num(settings?["MinimumLevelToApplyBuff"]),
            buffPrices = settings?["ItemEnhancementSettings"] is JsonObject buffs ? buffs.ToDictionary(b => b.Key, b => Num(b.Value?["PriceModifier"])) : null,
            treatment = Node(r.Repair.WeaponTreatment),
            kits = ItemsUnder(RepairKitNode).Select(id =>
            {
                var p = PropsOf(templates.Items[new MongoId(id)].Properties);
                return new { id, name = ItemName(id, locale), points = D(p, "MaxRepairResource"), price = Math.Round(Price(id)) };
            }).OrderBy(k => k.name).ToList()
        };
    }

    // ---------- ammo and armour ----------

    private object AmmoPage(IReadOnlyDictionary<string, string> locale)
    {
        var rounds = new List<object>();
        foreach (var id in ItemsUnder(AmmoNode))
        {
            var p = PropsOf(templates.Items[new MongoId(id)].Properties);
            // ammo boxes hang elsewhere; grenades for launchers are rounds too, they just do little penetration
            // (a grenade's shrapnel is ammo too, as far as the game's concerned, but no gun fires it)
            if (DevName().IsMatch(ItemName(id, locale)) || D(p, "Damage") is not { } damage || S(p, "Caliber") is not { } caliber)
            {
                continue;
            }

            rounds.Add(new
            {
                id, name = ItemName(id, locale), shortName = locale.GetValueOrDefault(id + " ShortName"),
                caliber = Caliber(caliber), damage, pellets = D(p, "buckshotBullets") is > 1 and var b ? b : (double?)null,
                pen = D(p, "PenetrationPower"), armor = D(p, "ArmorDamage"), frag = D(p, "FragmentationChance"), speed = D(p, "InitialSpeed"),
                accuracy = D(p, "ammoAccr"), recoil = D(p, "ammoRec"), tracer = p.GetValueOrDefault("Tracer")?.GetValue<bool>() == true,
                price = Math.Round(Price(id)), modded = !BaseGame(id)
            });
        }

        return new { rounds };
    }

    // "Caliber556x45NATO" to "5.56x45 NATO": the game keeps the bore without its point
    private static string Caliber(string raw)
    {
        var s = raw.StartsWith("Caliber", StringComparison.OrdinalIgnoreCase) ? raw[7..] : raw;
        var m = System.Text.RegularExpressions.Regex.Match(s, @"^(\d+)x(\d+)(.*)$");
        if (!m.Success)
        {
            return s;
        }

        // 46 is 4.6 but 12 (gauge-sized) and 30 stay; 545 is 5.45 but 127 is 12.7; 1143 is 11.43
        var bore = m.Groups[1].Value;
        var mm = bore.Length switch
        {
            2 when bore[0] >= '4' && bore[0] <= '9' => bore[0] + "." + bore[1],
            3 when bore[0] == '1' => bore[..2] + "." + bore[2],
            3 => bore[0] + "." + bore[1..],
            4 => bore[..2] + "." + bore[2..],
            _ => bore
        };
        return $"{mm}x{m.Groups[2].Value}{(m.Groups[3].Value is { Length: > 0 } rest ? " " + rest : "")}";
    }

    // What kind of armour an item is, from the item tree rather than the handbook, so a mod's armour that never got a
    // handbook entry still has a kind. The first match walking up from the item wins: built-in inserts sit under plates.
    private static readonly (string Node, string Kind)[] ArmorKinds =
    [
        (BaseClasses.BUILT_IN_INSERTS.ToString(), "insert"), (BaseClasses.ARMOR_PLATE.ToString(), "plate"), (BaseClasses.ARMOR.ToString(), "armor"),
        (BaseClasses.VEST.ToString(), "rig"), (BaseClasses.HEADWEAR.ToString(), "helmet"), (BaseClasses.FACE_COVER.ToString(), "face"),
        (BaseClasses.VISORS.ToString(), "eyewear"), (BaseClasses.HEADPHONES.ToString(), "headset"), (BaseClasses.ARMORED_EQUIPMENT.ToString(), "part")
    ];

    private string ArmorKind(TemplateItem item)
    {
        for (var (at, steps) = (item, 0); at != null && steps < 20; at = templates.Items.GetValueOrDefault(at.Parent), steps++)
        {
            foreach (var (node, kind) in ArmorKinds)
            {
                if (at.Id.ToString() == node)
                {
                    return kind;
                }
            }
        }

        return "other";
    }

    // Armour as you wear it, every piece the server has, modded or not. Helmets, vests and rigs have their built-in
    // soft armour added in. Each plate slot lists every plate that fits it (the plates themselves are sent once, by
    // id, under "plates"), the one it comes with, and whether this server can give you each one. Weight and the
    // speed, turning and ergonomics penalties are given three ways: the shell alone, as sold (with the plates it
    // comes with), and with the best plates this server gives, since plates weigh and slow you as much as the shell.
    private object ArmorPage(JsonObject config, IReadOnlyDictionary<string, string> locale)
    {
        var materials = config["ArmorMaterials"] as JsonObject;
        var list = new List<object>();
        // an insert fits many vests: read each item's properties once
        var read = new Dictionary<MongoId, Dictionary<string, JsonNode?>>();
        Dictionary<string, JsonNode?> PropsFor(MongoId at)
        {
            if (!read.TryGetValue(at, out var found))
            {
                read[at] = found = templates.Items.TryGetValue(at, out var t) ? PropsOf(t.Properties) : [];
            }

            return found;
        }

        bool Armoured(MongoId at) => D(PropsFor(at), "armorClass") is > 0;
        List<string> Zones(Dictionary<string, JsonNode?> x) =>
            (x.GetValueOrDefault("armorColliders") as JsonArray ?? []).Concat(x.GetValueOrDefault("armorPlateColliders") as JsonArray ?? [])
            .Select(z => z?.ToString() ?? "").Where(z => z.Length > 0).Distinct().ToList();
        bool Flag(Dictionary<string, JsonNode?> x, string key) => x.GetValueOrDefault(key) is JsonValue v && v.TryGetValue<bool>(out var b) && b;
        string? Text(Dictionary<string, JsonNode?> x, string key) => x.GetValueOrDefault(key)?.ToString() is { Length: > 0 } s ? s : null;
        // a required slot with one choice is armour built into the item, not something you add
        static bool BuiltIn(Slot slot, List<MongoId> allowed) => slot.Required == true && allowed.Count == 1;
        static List<MongoId> Allowed(Slot? slot) => (slot?.Properties?.Filters ?? []).SelectMany(f => f?.Filter ?? []).ToList();

        // built-in inserts are shown inside what they're built into, never on their own
        var builtIns = new HashSet<MongoId>();
        foreach (var item in templates.Items.Values)
        {
            foreach (var slot in item.Properties?.Slots ?? [])
            {
                if (slot != null && Allowed(slot) is var allowed && BuiltIn(slot, allowed) && Armoured(allowed[0]))
                {
                    builtIns.Add(allowed[0]);
                }
            }
        }

        var plates = new Dictionary<string, object>();
        void AddPlate(MongoId at)
        {
            var key = at.ToString();
            if (plates.ContainsKey(key))
            {
                return;
            }

            var x = PropsFor(at);
            plates[key] = new
            {
                name = ItemName(key, locale), armorClass = D(x, "armorClass"), durability = D(x, "MaxDurability"), material = S(x, "ArmorMaterial"),
                weight = D(x, "Weight"), speed = D(x, "speedPenaltyPercent"), turn = D(x, "mousePenalty"), ergo = D(x, "weaponErgonomicPenalty"),
                zones = Zones(x), given = _given == null || _given.Contains(key), modded = !BaseGame(key)
            };
        }

        foreach (var (key, item) in templates.Items)
        {
            var id = key.ToString();
            if (item.Type != "Item" || builtIns.Contains(key))
            {
                continue;
            }

            var kind = ArmorKind(item);
            if (kind == "insert")
            {
                continue;
            }

            var p = PropsFor(key);
            // the armour that is always there: the item itself if it has a class, and its built-in inserts
            var parts = new List<Dictionary<string, JsonNode?>>();
            if (D(p, "armorClass") is > 0)
            {
                parts.Add(p);
            }

            var slots = new List<object>();
            var fitted = new List<Dictionary<string, JsonNode?>>();
            var best = new List<Dictionary<string, JsonNode?>>();
            foreach (var slot in item.Properties?.Slots ?? [])
            {
                var allowed = Allowed(slot);
                var armoured = allowed.Where(Armoured).ToList();
                if (slot == null || armoured.Count == 0)
                {
                    continue;
                }

                if (BuiltIn(slot, allowed))
                {
                    parts.Add(PropsFor(armoured[0]));
                    continue;
                }

                var comes = (slot.Properties?.Filters ?? []).Select(f => f?.Plate).FirstOrDefault(x => x != null && Armoured(x.Value));
                if (comes != null)
                {
                    fitted.Add(PropsFor(comes.Value));
                }

                // the best you can put in: the highest class this server gives you (or the one it comes with), lighter first
                var top = armoured.Where(a => a == comes || _given == null || _given.Contains(a.ToString()))
                    .OrderByDescending(a => D(PropsFor(a), "armorClass")).ThenBy(a => D(PropsFor(a), "Weight") ?? 0).FirstOrDefault();
                if (top != default)
                {
                    best.Add(PropsFor(top));
                }

                armoured.ForEach(AddPlate);
                slots.Add(new { name = slot.Name ?? "", comes = comes?.ToString(), best = top != default ? top.ToString() : null, fits = armoured.Select(a => a.ToString()).ToList() });
            }

            if (parts.Count == 0 && slots.Count == 0)
            {
                continue;
            }

            // the shell and its built-in inserts, then with plates added
            var always = new List<Dictionary<string, JsonNode?>> { p };
            always.AddRange(parts.Where(x => !ReferenceEquals(x, p)));
            object Worn(List<Dictionary<string, JsonNode?>> with)
            {
                var all = always.Concat(with).ToList();
                var armour = parts.Concat(with).ToList();
                return new
                {
                    armorClass = armour.Count > 0 ? armour.Max(x => D(x, "armorClass") ?? 0) : 0,
                    durability = armour.Sum(x => D(x, "MaxDurability") ?? 0),
                    zones = armour.SelectMany(Zones).Distinct().ToList(),
                    weight = Math.Round(all.Sum(x => D(x, "Weight") ?? 0), 3),
                    speed = Math.Round(all.Sum(x => D(x, "speedPenaltyPercent") ?? 0), 2),
                    turn = Math.Round(all.Sum(x => D(x, "mousePenalty") ?? 0), 2),
                    ergo = Math.Round(all.Sum(x => D(x, "weaponErgonomicPenalty") ?? 0), 2)
                };
            }

            var strongest = parts.Concat(fitted).OrderByDescending(x => D(x, "armorClass")).FirstOrDefault() ?? p;
            var material = S(strongest, "ArmorMaterial");
            var cells = item.Properties?.Grids?.Sum(g => (g.Properties?.CellsH ?? 0) * (g.Properties?.CellsV ?? 0)) ?? 0;
            list.Add(new
            {
                id, name = ItemName(id, locale), kind, modded = !BaseGame(id), price = Math.Round(Price(id)),
                material, destructibility = material != null ? Num(materials?[material]?["Destructibility"]) : null,
                heavy = Text(p, "ArmorType"), deaf = Text(p, "DeafStrength"), blunt = D(strongest, "BluntThroughput"),
                ricochet = (strongest.GetValueOrDefault("RicochetParams") as JsonObject) is { } r ? new { max = Num(r["x"]), min = Num(r["y"]), angle = Num(r["z"]) } : null,
                // every "Blocks..." setting it has on, named by the locale's name for that slot ("FaceCover" is "Face cover")
                blocks = p.Keys.Where(k => k.StartsWith("Blocks", StringComparison.Ordinal) && k.Length > 6 && Flag(p, k))
                    .Select(k => locale.GetValueOrDefault(k[6..])?.Trim() is { Length: > 0 } n ? n : System.Text.RegularExpressions.Regex.Replace(k[6..], "([a-z])([A-Z])", "$1 $2")).ToList(),
                cells = cells > 0 ? cells : (int?)null, grids = item.Properties?.Grids?.Count() ?? 0,
                builtIn = new { armorClass = parts.Count > 0 ? parts.Max(x => D(x, "armorClass") ?? 0) : 0, durability = parts.Sum(x => D(x, "MaxDurability") ?? 0), zones = parts.SelectMany(Zones).Distinct().ToList() },
                slots,
                shell = new { weight = D(p, "Weight"), speed = D(p, "speedPenaltyPercent"), turn = D(p, "mousePenalty"), ergo = D(p, "weaponErgonomicPenalty") },
                sold = Worn(fitted), best = Worn(best)
            });
        }

        return new
        {
            armor = list, plates,
            materials = materials?.ToDictionary(m => m.Key, m => new { destructibility = Num(m.Value?["Destructibility"]), repairDegradation = Num(m.Value?["MaxRepairDegradation"]) })
        };
    }

    // the armour chart as JSON, per language, so an item page can take its own row without rebuilding the chart
    private readonly Dictionary<string, JsonObject> _armorNodes = [];

    // one item's armour for its page: its row from the armour chart, the plates its slots take, and for a plate
    // the armour it fits in; null when the item is none of these
    public object? ArmorFor(string id, IReadOnlyDictionary<string, string> locale, string lang)
    {
        JsonObject? chart;
        lock (_armorNodes)
        {
            if (!_armorNodes.TryGetValue(lang, out chart))
            {
                chart = Node(Rule("armor", locale, lang)) as JsonObject;
                if (chart != null)
                {
                    _armorNodes[lang] = chart;
                }
            }
        }

        if (chart == null)
        {
            return null;
        }

        var rows = (chart["armor"] as JsonArray ?? []).OfType<JsonObject>().ToList();
        var allPlates = chart["plates"] as JsonObject ?? [];
        var row = rows.FirstOrDefault(x => x["id"]?.ToString() == id);
        var compatible = new JsonArray();
        foreach (var other in rows)
        {
            foreach (var slot in (other["slots"] as JsonArray ?? []).OfType<JsonObject>())
            {
                if ((slot["fits"] as JsonArray ?? []).Any(f => f?.ToString() == id))
                {
                    compatible.Add(new JsonObject
                    {
                        ["id"] = other["id"]?.DeepClone(), ["name"] = other["name"]?.DeepClone(), ["kind"] = other["kind"]?.DeepClone(),
                        ["slot"] = slot["name"]?.DeepClone(), ["comes"] = slot["comes"]?.ToString() == id
                    });
                }
            }
        }

        var self = allPlates[id];
        if (row == null && self == null && compatible.Count == 0)
        {
            return null;
        }

        var plates = new JsonObject();
        foreach (var fit in (row?["slots"] as JsonArray ?? []).OfType<JsonObject>().SelectMany(s => s["fits"] as JsonArray ?? []))
        {
            var key = fit?.ToString();
            if (key != null && !plates.ContainsKey(key) && allPlates[key] is { } stats)
            {
                plates[key] = stats.DeepClone();
            }
        }

        return new JsonObject
        {
            ["row"] = row?.DeepClone(), ["self"] = self?.DeepClone(), ["plates"] = plates, ["compatible"] = compatible,
            ["materials"] = chart["materials"]?.DeepClone()
        };
    }

    // ---------- skills, levels, health ----------

    private static readonly HashSet<string> SkillGeneral = ["SkillProgressRate", "WeaponSkillProgressRate"];

    private object SkillsPage(JsonObject config, IReadOnlyDictionary<string, string> locale)
    {
        var all = config["SkillsSettings"] as JsonObject ?? [];
        var table = SkillEffects();
        // a value in the table: a number, or where the server keeps it
        double? Value(JsonNode? value) => value is JsonObject path
            ? Num(path["setting"]?.ToString().Split('.').Aggregate((JsonNode?)all, (at, key) => at?[key])) / (Num(path["div"]) ?? 1)
            : Num(value);
        var skills = all.Where(s => s.Value is JsonObject o && o.Count > 0).Select(s => new
        {
            id = s.Key,
            name = locale.GetValueOrDefault(s.Key) ?? s.Key,
            text = locale.GetValueOrDefault(s.Key + "Description"),
            how = locale.GetValueOrDefault(s.Key + "LevelingUpDescription"),
            settings = s.Value,
            // what levelling it gives you: the game's own wording, with the numbers the server has for it
            effects = (table?[s.Key] as JsonArray ?? []).OfType<JsonObject>().Select(e => new
            {
                text = locale.GetValueOrDefault(e["key"]?.ToString() ?? ""),
                perLevel = e["perLevel"] != null ? Value(e["perLevel"]) : Value(e["max"]) / 50,
                elite = Value(e["elite"])
            }).Where(e => e.text != null).ToList()
        }).OrderBy(s => s.name).ToList();
        return new
        {
            skills,
            rates = all.Where(s => s.Value is JsonValue).ToDictionary(s => s.Key, s => Num(s.Value)),
            pointsPerLevel = Num(config["SkillExpPerLevel"]),
            fatiguePoints = Num(config["SkillPointsBeforeFatigue"]),
            fatigueReset = Num(config["SkillFatigueReset"]),
            fatiguePerPoint = Num(config["SkillFatiguePerPoint"]),
            minEffectiveness = Num(config["SkillMinEffectiveness"]),
            freshEffectiveness = Num(config["SkillFreshEffectiveness"]),
            freshPoints = Num(config["SkillFreshPoints"]),
            atrophy = config["SkillAtrophy"]?.GetValue<bool>()
        };
    }

    private object LevelsPage(RuleSources r, JsonObject config, IReadOnlyDictionary<string, string> locale)
    {
        var exp = config["exp"];
        return new
        {
            table = (exp?["level"]?["exp_table"] as JsonArray ?? []).Select(e => Num(e?["exp"]) ?? 0).ToList(),
            fleaLevel = Num(config["RagFair"]?["minUserLevel"]),
            traders = PlayerTraders().Select(t => new
            {
                id = t.Id, name = TraderNick(t.Id, locale),
                levels = t.Base.LoyaltyLevels!.Select((l, i) => new { level = i + 1, minLevel = l.MinLevel, standing = l.MinStanding, sales = l.MinSalesSum }).ToList()
            }).ToList(),
            kill = exp?["kill"],
            raid = exp?["match_end"],
            heal = exp?["heal"],
            doors = new { open = Num(exp?["expForLockedDoorOpen"]), breach = Num(exp?["expForLockedDoorBreach"]) }
        };
    }

    // the bonuses each skill gives, as the game defines them: pack/skill-effects.json, read from its SkillManager
    private JsonNode? SkillEffects()
    {
        try
        {
            var file = Path.Combine(folder, "pack", "skill-effects.json");
            return File.Exists(file) ? JsonNode.Parse(File.ReadAllText(file)) : null;
        }
        catch (Exception ex) when (ex is IOException or System.Text.Json.JsonException)
        {
            logger.Warning($"[Tushonkapedia] Couldn't read the skill effects: {ex.Message}");
            return null;
        }
    }

    // Which items on this server treat each injury, from their own properties.
    private Dictionary<string, List<object>> Treatments(IReadOnlyDictionary<string, string> locale)
    {
        var found = new Dictionary<string, List<object>>(StringComparer.OrdinalIgnoreCase);
        var book = Handbook();
        foreach (var (key, item) in templates.Items)
        {
            var id = key.ToString();
            if (item.Type != "Item" || !book.ContainsKey(id) || !PropsOf(item.Properties).TryGetValue("effects_damage", out var effects) || effects is not JsonObject list)
            {
                continue;
            }

            foreach (var (effect, _) in list)
            {
                if (!found.TryGetValue(effect, out var items))
                {
                    found[effect] = items = [];
                }

                items.Add(new { id, name = ItemName(id, locale), modded = !BaseGame(id) });
            }
        }

        return found;
    }

    private object HealthPage(JsonObject config, IReadOnlyDictionary<string, string> locale)
    {
        var health = config["Health"];
        var parts = health?["ProfileHealthSettings"]?["BodyPartsSettings"] as JsonObject ?? [];
        var therapist = traders.FirstOrDefault(t => t.Key == Traders.THERAPIST);
        // each injury and body part by the game's own name for it, else its key split into words
        string Spaced(string k) => System.Text.RegularExpressions.Regex.Replace(k, "([a-z])([A-Z])", "$1 $2");
        string? Plain(string k) => locale.GetValueOrDefault(k) is { Length: > 0 } v && v.Any(char.IsLower) ? v.Trim() : null;
        var effectNames = (health?["Effects"] as JsonObject ?? []).ToDictionary(e => e.Key, e => locale.GetValueOrDefault("DamageType_" + e.Key)?.Trim() is { Length: > 0 } d ? d : Plain(e.Key) ?? Spaced(e.Key));
        var partNames = parts.ToDictionary(p => p.Key, p => locale.GetValueOrDefault("DeathInfo/" + p.Key)?.Trim() is { Length: > 0 } d ? d : Spaced(p.Key));
        return new
        {
            treats = Treatments(locale),
            parts = parts.Select(p => new { part = p.Key, hp = Num(p.Value?["Maximum"]), environment = Num(p.Value?["EnvironmentDamageMultiplier"]), overDamage = Num(p.Value?["OverDamageReceivedMultiplier"]) }).ToList(),
            body = health?["ProfileHealthSettings"]?["HealthFactorsSettings"],
            effects = health?["Effects"],
            effectNames,
            partNames,
            healPrice = health?["HealPrice"],
            falling = health?["Falling"],
            therapist = therapist.Value?.Base?.LoyaltyLevels?.Select((l, i) => new { level = i + 1, coef = l.HealPriceCoefficient }).ToList(),
            therapistId = therapist.Value != null ? therapist.Key.ToString() : null
        };
    }

    // ---------- Fence ----------

    private object KarmaPage(RuleSources r, JsonObject config, IReadOnlyDictionary<string, string> locale)
    {
        var fence = config["FenceSettings"];
        var trader = Node(r.Trader) as JsonObject;
        var kills = new List<(string Name, object Row)>();
        foreach (var (role, type) in bots.Types)
        {
            // set per difficulty: normal is what most bots spawn as
            var xp = Node(type?.BotExperience);
            var per = xp?["standingForKill"];
            if ((Num(per?["normal"]) ?? Num(per)) is not { } standing || standing == 0)
            {
                continue;
            }

            var name = BotName(role, locale);
            kills.Add((name, new { role, name, standing, easy = Num(per?["easy"]), hard = Num(per?["hard"]), aggressor = Num(xp?["aggressorBonus"]?["normal"]) }));
        }

        return new
        {
            levels = (fence?["Levels"] as JsonObject ?? []).Select(l => new { standing = double.TryParse(l.Key, out var s) ? s : 0, settings = l.Value })
                .OrderBy(l => l.standing).ToList(),
            pmcKillMultiplier = Num(fence?["PmcBotKillStandingMultiplier"]),
            paidExitStanding = Num(fence?["paidExitStandingNumerator"]),
            scavCooldown = Num(config["SavagePlayCooldown"]),
            karma = config["KarmaCalculationSettings"],
            standingMin = Num(trader?["fence"]?["playerRepMin"]),
            standingMax = Num(trader?["fence"]?["playerRepMax"]),
            kills = kills.OrderBy(k => k.Name).Select(k => k.Row).ToList()
        };
    }

    // ---------- achievements, prestige ----------

    // a reward as the page needs it: what kind, how much, which item or trader
    private object RewardOut(object reward, IReadOnlyDictionary<string, string> locale)
    {
        var node = Node(reward);
        var items = node?["items"] as JsonArray;
        var target = node?["target"]?.ToString();
        var tpl = items?.FirstOrDefault(i => i?["_id"]?.ToString() == target)?["_tpl"]?.ToString() ?? items?.FirstOrDefault()?["_tpl"]?.ToString();
        var count = items?.FirstOrDefault()?["upd"]?["StackObjectsCount"] is { } c ? Num(c) : null;
        var trader = node?["traderId"]?.ToString();
        return new
        {
            type = node?["type"]?.ToString(),
            value = Num(node?["value"]) ?? (node?["value"] is JsonValue v ? double.TryParse(v.ToString(), out var d) ? d : null : null),
            item = tpl, itemName = tpl != null ? ItemName(tpl, locale) : null, count,
            trader = trader is { Length: > 0 } ? TraderNick(trader, locale) : null,
            target = tpl == null && target is { Length: > 0 } ? locale.GetValueOrDefault(target) ?? locale.GetValueOrDefault(target + " Name") ?? target : null
        };
    }

    private object AchievementsPage(IReadOnlyDictionary<string, string> locale)
    {
        return new
        {
            list = (templates.Achievements ?? []).Concat(templates.CustomAchievements ?? []).Select(a => new
            {
                id = a.Id.ToString(),
                name = locale.GetValueOrDefault(a.Id + " name") ?? a.Id.ToString(),
                text = a.Hidden ? null : locale.GetValueOrDefault(a.Id + " description"),
                rarity = a.Rarity, side = a.Side, hidden = a.Hidden, image = a.ImageUrl,
                rewards = (a.Rewards ?? []).Select(x => RewardOut(x, locale)).ToList()
            }).OrderBy(a => a.rarity).ThenBy(a => a.name).ToList()
        };
    }

    private object PrestigePage(IReadOnlyDictionary<string, string> locale)
    {
        var node = Node(templates.Prestige);
        var levels = new List<object>();
        var i = 0;
        foreach (var element in node?["elements"] as JsonArray ?? [])
        {
            i++;
            var conditions = (element?["conditions"] as JsonArray ?? []).Select(c =>
            {
                var kind = c?["conditionType"]?.ToString();
                var target = c?["target"] is JsonValue one ? one.ToString() : null;
                var items = (c?["target"] as JsonArray ?? []).Select(x => x?.ToString() ?? "").Where(x => x.Length > 0).ToList();
                return new
                {
                    kind, value = Num(c?["value"]), compare = c?["compareMethod"]?.ToString(), quest = kind == "Quest" ? target : null,
                    text = locale.GetValueOrDefault(c?["id"]?.ToString() ?? "") is { Length: > 0 } t ? t : null,
                    skill = kind == "Skill" ? locale.GetValueOrDefault(target ?? "") ?? target : null,
                    area = Num(c?["areaType"]),
                    items = items.Select(x => new { id = x, name = ItemName(x, locale) }).ToList()
                };
            }).ToList();
            var rewards = (element?["rewards"] as JsonArray ?? []).Where(x => x?["type"]?.ToString() != "Stub").Select(x => new
            {
                type = x?["type"]?.ToString(), value = Num(x?["value"]),
                item = (x?["items"] as JsonArray)?.FirstOrDefault()?["_tpl"]?.ToString() is { } tpl ? new { id = tpl, name = ItemName(tpl, locale) } : null,
                target = x?["target"]?.ToString() is { Length: > 0 } t ? locale.GetValueOrDefault(t) ?? locale.GetValueOrDefault(t + " Name") : null
            }).ToList();
            levels.Add(new
            {
                level = i, image = element?["image"]?.ToString(), conditions, rewards,
                transfer = element?["transferConfigs"]
            });
        }

        return new { levels };
    }

    // ---------- events, the Cultist Circle, containers ----------

    private object EventsPage(RuleSources r)
    {
        var active = r.SeasonService.GetActiveEvents().Select(e => e.Name).ToHashSet();
        var config = Node(r.Seasons) as JsonObject;
        return new
        {
            detection = r.Seasons.EnableSeasonalEventDetection,
            events = r.Seasons.Events.Select(e => new
            {
                name = e.Name, type = e.Type.ToString(), enabled = e.Enabled, active = active.Contains(e.Name),
                start = new { day = e.StartDay, month = e.StartMonth }, end = new { day = e.EndDay, month = e.EndMonth },
                settings = Node(e.Settings)
            }).ToList(),
            bosses = config?["eventBossSpawns"] is JsonObject b ? b.ToDictionary(x => x.Key, x => (x.Value as JsonObject)?.Select(m => m.Key).ToList()) : null
        };
    }

    private object CirclePage(RuleSources r, IReadOnlyDictionary<string, string> locale)
    {
        var circle = Node(r.Hideout)?["cultistCircle"] as JsonObject ?? [];
        var names = (JsonNode? ids) => (ids as JsonArray ?? []).Select(x => x?.ToString() ?? "").Where(x => x.Length > 0).Select(x => new { id = x, name = ItemName(x, locale) }).ToList();
        return new
        {
            maxRewards = Num(circle["maxRewardItemCount"]),
            rewardValue = circle["rewardPriceMultiplierMinMax"],
            bonusChance = Num(circle["bonusChanceMultiplier"]),
            highValue = Num(circle["highValueThresholdRub"]),
            taskSeconds = Num(circle["hideoutTaskRewardTimeSeconds"]),
            craftThreshold = Num(circle["hideoutCraftSacrificeThresholdRub"]),
            times = (circle["craftTimeThresholds"] as JsonArray ?? []).Select(t => new { min = Num(t?["min"]), max = Num(t?["max"]), seconds = Num(t?["craftTimeSeconds"]) }).ToList(),
            timeOverride = Num(circle["craftTimeOverride"]),
            direct = (circle["directRewards"] as JsonArray ?? []).Select(d => new
            {
                give = names(d?["requiredItems"]), get = names(d?["reward"]), seconds = Num(d?["craftTimeSeconds"]), repeatable = d?["repeatable"]?.GetValue<bool>()
            }).ToList()
        };
    }

    private object ContainersPage(IReadOnlyDictionary<string, string> locale)
    {
        var list = new List<object>();
        foreach (var id in ItemsUnder(SecureContainerNode).Select(x => (x, true)).Concat(ItemsUnder(SimpleContainer).Select(x => (x, false))))
        {
            // SPT's own serializer: a grid's filter is a set of ids, which plain JSON can't write
            var p = new Dictionary<string, JsonNode?>(StringComparer.OrdinalIgnoreCase);
            foreach (var (k, v) in Node(templates.Items[new MongoId(id.x)].Properties) as JsonObject ?? [])
            {
                p[k] = v;
            }

            var grids = p.GetValueOrDefault("Grids") as JsonArray ?? [];
            var cells = 0.0;
            var allowed = new HashSet<string>();
            var anything = false;
            // the game's secure containers take anything bar a list; mods that add their own items to the filter
            // don't narrow that, so single items beside an exclusion list still read as "anything"
            var onlyItems = true;
            var singles = new HashSet<string>();
            var excluded = 0;
            foreach (var g in grids.OfType<JsonObject>())
            {
                var gp = g["_props"] ?? g["Props"];
                cells += (Num(gp?["cellsH"]) ?? Num(gp?["CellsH"]) ?? 0) * (Num(gp?["cellsV"]) ?? Num(gp?["CellsV"]) ?? 0);
                foreach (var f in (gp?["filters"] ?? gp?["Filters"]) as JsonArray ?? [])
                {
                    foreach (var x in (f?["Filter"] ?? f?["filter"]) as JsonArray ?? [])
                    {
                        // the root of the item tree: it takes anything; ids with no name (a mod's leftovers) aren't shown
                        if (x?.ToString() is not { Length: > 0 } fid)
                        {
                            continue;
                        }

                        if (fid == ItemRoot)
                        {
                            anything = true;
                        }
                        else
                        {
                            // a whole kind of item narrows it; single items (or ids the server doesn't know) don't
                            var node = MongoId.IsValidMongoId(fid) && templates.Items.TryGetValue(new MongoId(fid), out var t) && t.Type == "Node";
                            onlyItems &= !node;
                            if (!node)
                            {
                                singles.Add(fid);
                            }
                            else if (ItemName(fid, locale) is var name && name != fid)
                            {
                                allowed.Add(name);
                            }
                        }
                    }

                    excluded += ((f?["ExcludedFilter"] ?? f?["excludedFilter"]) as JsonArray)?.Count ?? 0;
                }
            }

            anything |= onlyItems && excluded > 0;
            if (cells == 0)
            {
                continue;
            }

            list.Add(new
            {
                id = id.x, name = ItemName(id.x, locale), secure = id.Item2, cells,
                width = D(p, "Width"), height = D(p, "Height"), weight = D(p, "Weight"),
                anything, excluded, allowed = anything ? [] : allowed.OrderBy(a => a).ToList(), singles = anything ? 0 : singles.Count, price = Math.Round(Price(id.x)), modded = !BaseGame(id.x)
            });
        }

        return new { containers = list };
    }
}
