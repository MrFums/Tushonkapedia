using System.Text.Json;
using System.Text.Json.Nodes;
using SPTarkov.Server.Core.Models.Common;

namespace Tushonkapedia.Server;

// What an item does, for the top of its page: a gun's or part's ergonomics and recoil, ammo's damage and
// penetration, armour's class and what it costs you to wear, what a med heals, a stim's effects good and bad, food's
// energy and water, a bag's space. Read from the item's own properties in the server's table, so a mod's item or a
// mod's change to one shows as the server has it. Only the figures an item actually has are listed.
public sealed partial class ItemWiki
{
    // the item's properties as JSON, keys in lower case so the game's names ("bFirerate") and SPT's agree
    private static Dictionary<string, JsonNode?> PropsOf(object? props)
    {
        var map = new Dictionary<string, JsonNode?>(StringComparer.OrdinalIgnoreCase);
        if (props == null)
        {
            return map;
        }

        try
        {
            if (JsonSerializer.SerializeToNode(props, props.GetType()) is JsonObject obj)
            {
                foreach (var (key, value) in obj)
                {
                    map[key] = value;
                }
            }
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException or InvalidOperationException)
        {
            // a property SPT's types can't write as JSON: the page goes without figures for this item
        }

        return map;
    }

    private static double? D(Dictionary<string, JsonNode?> p, string key) =>
        p.GetValueOrDefault(key) is JsonValue v && v.TryGetValue<double>(out var d) ? d : null;

    private static string? S(Dictionary<string, JsonNode?> p, string key) =>
        p.GetValueOrDefault(key) is JsonValue v && v.TryGetValue<string>(out var s) && s.Length > 0 ? s : null;

    private static string Signed(double v, string unit = "") => $"{(v > 0 ? "+" : "")}{Math.Round(v, 2)}{unit}";

    // Conditions, stats and buffs by the server's own locale. A condition reads best as its damage name, then its
    // treatment name; a stat or buff by its own key. Whatever the locale lacks is its key split into words.
    private static string Spaced(string key) => System.Text.RegularExpressions.Regex.Replace(key, "([a-z])([A-Z])", "$1 $2");

    // a locale's all-capitals label (the game's HUD writes "HYDRATION") reads as a plain word on the page
    private static string Calm(string s) =>
        s.Count(char.IsLetter) > 3 && s.Where(char.IsLetter).All(char.IsUpper)
            ? char.ToUpperInvariant(s[0]) + s[1..].ToLowerInvariant() : s;

    private static string Named(IReadOnlyDictionary<string, string> locale, string key, params string[] prefixes)
    {
        foreach (var prefix in prefixes)
        {
            if (locale.GetValueOrDefault(prefix + key) is { Length: > 0 } s)
            {
                return Calm(s);
            }
        }

        return locale.FirstOrDefault(kv => string.Equals(kv.Key, key, StringComparison.OrdinalIgnoreCase)).Value is { Length: > 0 } any
            ? Calm(any) : Spaced(key);
    }

    private static string Condition(IReadOnlyDictionary<string, string> locale, string key) =>
        Named(locale, key, "DamageType_", "UI/HealthTreatmentName_", "");

    private static string Stat(IReadOnlyDictionary<string, string> locale, string key) =>
        Named(locale, key, "", "UI/HealthTreatmentName_");

    // Whether a stim's effect works against you. An effect with no value to judge by (a tremor, a toxin) is bad when
    // the server also puts it on you from outside: in an effect list no med, food or drink of yours carries (a blade's
    // hit, frostbite). An effect that rises in such a list beside effects that all fall (a blade's hit: more damage
    // taken, health draining) is bad when raised; any other effect is bad when it lowers something.
    private HashSet<string>? _inflicted;
    private HashSet<string>? _raisedBad;

    private bool BadBuff(string type, double value)
    {
        if (_inflicted == null || _raisedBad == null)
        {
            var yours = templates.Items.Values.Select(t => PropsOf(t.Properties)).Where(p => UseTime(p) != null)
                .Select(p => S(p, "StimulatorBuffs")).OfType<string>().ToHashSet(StringComparer.OrdinalIgnoreCase);
            var lists = Buffs().Where(b => !yours.Contains(b.Key))
                .Select(b => (b.Value as JsonArray ?? []).OfType<JsonObject>()
                    .Select(x => (Type: x["BuffType"]?.ToString(), Value: x["Value"]?.GetValue<double>() ?? 0))
                    .Where(x => x.Type != null).ToList())
                .ToList();
            _inflicted = lists.SelectMany(l => l).Where(x => x.Value == 0).Select(x => x.Type!).ToHashSet(StringComparer.OrdinalIgnoreCase);
            _raisedBad = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var list in lists)
            {
                foreach (var up in list.Where(x => x.Value > 0).Select(x => x.Type!).Distinct())
                {
                    var others = list.Where(x => x.Value != 0 && !string.Equals(x.Type, up, StringComparison.OrdinalIgnoreCase)).ToList();
                    if (others.Count > 0 && others.All(x => x.Value < 0))
                    {
                        _raisedBad.Add(up);
                    }
                }
            }
        }

        if (value != 0)
        {
            return _raisedBad.Contains(type) ? value > 0 : value < 0;
        }

        return _inflicted.Contains(type);
    }

    // the stimulants' effects, from the server's globals, read once
    private Dictionary<string, JsonNode?>? _buffs;

    private Dictionary<string, JsonNode?> Buffs()
    {
        if (_buffs != null)
        {
            return _buffs;
        }

        var map = new Dictionary<string, JsonNode?>(StringComparer.OrdinalIgnoreCase);
        try
        {
            if (stimBuffs() is { } buffs && JsonSerializer.SerializeToNode(buffs, buffs.GetType()) is JsonObject obj)
            {
                foreach (var (key, value) in obj)
                {
                    map[key] = value;
                }
            }
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException or InvalidOperationException)
        {
            logger.Warning($"[Tushonkapedia] Couldn't read the stimulants' effects: {ex.Message}");
        }

        return _buffs = map;
    }

    // Groups of figures: { title, rows: [{ label, value, tone }] }, tone "good" or "bad" where it's clearly one.
    private List<string>? _keptSides;

    public List<StatGroup> Stats(string id, IReadOnlyDictionary<string, string> locale)
    {
        var groups = new List<StatGroup>();
        if (!templates.Items.TryGetValue(new MongoId(id), out var template))
        {
            return groups;
        }

        var p = PropsOf(template.Properties);
        var rows = new List<StatRow>();
        void Row(string label, string? value, string tone = "", double? n = null)
        {
            if (!string.IsNullOrEmpty(value))
            {
                // n: the figure behind the text, for sorting lists by it
                rows.Add(new StatRow(label, value, tone, n));
            }
        }

        void Group(string title)
        {
            if (rows.Count > 0)
            {
                groups.Add(new StatGroup(title, rows.ToList()));
                rows.Clear();
            }
        }

        string Tone(double v, bool higherIsBetter = true) => v == 0 ? "" : (v > 0) == higherIsBetter ? "good" : "bad";

        // a gun
        if (IsWeapon(id))
        {
            Row("Caliber", S(p, "ammoCaliber") is { } cal ? QuestGraphBuilder.Caliber(cal) : null);
            Row("Fire rate", D(p, "bFirerate") is > 0 and var rate ? $"{rate:N0} rpm" : null, n: D(p, "bFirerate"));
            Row("Fire modes", p.GetValueOrDefault("weapFireType") is JsonArray modes && modes.Count > 0
                ? string.Join(", ", modes.Select(m => m?.ToString() switch { "single" => "Single", "fullauto" => "Full auto", "burst" => "Burst", "doublet" => "Double", var o => o })) : null);
            Row("Ergonomics", D(p, "Ergonomics") is { } ergo ? $"{ergo:0}" : null, n: D(p, "Ergonomics"));
            Row("Vertical recoil", D(p, "RecoilForceUp") is > 0 and var up ? $"{up:0}" : null, n: D(p, "RecoilForceUp"));
            Row("Horizontal recoil", D(p, "RecoilForceBack") is > 0 and var back ? $"{back:0}" : null, n: D(p, "RecoilForceBack"));
            Row("Effective distance", D(p, "bEffDist") is > 0 and var eff ? $"{eff:0} m" : null, n: D(p, "bEffDist"));
            Row("Muzzle velocity", D(p, "Velocity") is { } vel && vel != 0 ? Signed(vel, "%") : null, n: D(p, "Velocity"));
            Row("Sighting range", D(p, "SightingRange") is > 0 and var sight ? $"{sight:0} m" : null, n: D(p, "SightingRange"));
            Group("Weapon");
        }
        // ammo
        else if (D(p, "PenetrationPower") is { } pen && D(p, "Damage") is { } damage)
        {
            var pellets = Math.Max(1, D(p, "buckshotBullets") ?? D(p, "ProjectileCount") ?? 1);
            Row("Damage", pellets > 1 ? $"{damage:0} × {pellets:0} pellets" : $"{damage:0}", n: damage * pellets);
            Row("Penetration", $"{pen:0}", n: pen);
            Row("Armour damage", D(p, "ArmorDamage") is { } ad ? $"{ad:0}%" : null, n: D(p, "ArmorDamage"));
            Row("Speed", D(p, "InitialSpeed") is > 0 and var speed ? $"{speed:0} m/s" : null, n: D(p, "InitialSpeed"));
            Row("Fragmentation", D(p, "FragmentationChance") is > 0 and var frag ? $"{frag * 100:0}%" : null, n: (D(p, "FragmentationChance") ?? 0) * 100);
            Row("Bleeding", (D(p, "LightBleedingDelta") ?? 0) + (D(p, "HeavyBleedingDelta") ?? 0) is > 0 and var bleed ? Signed(bleed * 100, "%") : null, "good", ((D(p, "LightBleedingDelta") ?? 0) + (D(p, "HeavyBleedingDelta") ?? 0)) * 100);
            Row("Accuracy", D(p, "ammoAccr") is { } acc && acc != 0 ? Signed(acc, "%") : null, Tone(D(p, "ammoAccr") ?? 0), D(p, "ammoAccr"));
            Row("Recoil", D(p, "ammoRec") is { } rec && rec != 0 ? Signed(rec, "%") : null, Tone(D(p, "ammoRec") ?? 0, false), D(p, "ammoRec"));
            Row("Tracer", p.GetValueOrDefault("Tracer") is JsonValue t && t.TryGetValue<bool>(out var tracer) && tracer ? "Yes" : null);
            Row("Subsonic", D(p, "InitialSpeed") is > 0 and < 343 ? "Yes" : null);
            Group("Ammo");
        }
        // a weapon part: what it does to the gun
        else if (D(p, "Ergonomics") is { } ergo || D(p, "Recoil") is { } || D(p, "Accuracy") is { })
        {
            var e = D(p, "Ergonomics") ?? 0;
            Row("Ergonomics", e != 0 ? Signed(e) : null, Tone(e), e);
            var r = D(p, "Recoil") ?? 0;
            Row("Recoil", r != 0 ? Signed(r, "%") : null, Tone(r, false), r);
            var a = D(p, "Accuracy") ?? 0;
            Row("Accuracy", a != 0 ? Signed(a, "%") : null, Tone(a), a);
            var v = D(p, "Velocity") ?? 0;
            Row("Muzzle velocity", v != 0 ? Signed(v, "%") : null, Tone(v), v);
            Row("Sighting range", D(p, "SightingRange") is > 0 and var sight ? $"{sight:0} m" : null, n: D(p, "SightingRange"));
            var mags = p.GetValueOrDefault("Cartridges") is JsonArray carts && carts.FirstOrDefault() is JsonObject slot ? (double?)slot["_max_count"]?.GetValue<double>() : null;
            Row("Capacity", mags is > 0 ? $"{mags:0} rounds" : null, n: mags);
            var load = D(p, "LoadUnloadModifier") ?? 0;
            Row("Loading speed", load != 0 ? Signed(-load, "%") : null, Tone(-load), -load);
            var check = D(p, "CheckTimeModifier") ?? 0;
            Row("Check speed", check != 0 ? Signed(-check, "%") : null, Tone(-check), -check);
            Group("On the gun");
        }

        // armour and helmets
        if (D(p, "armorClass") is > 0 and var cls)
        {
            Row("Armour class", $"{cls:0}", n: cls);
            Row("Durability", D(p, "MaxDurability") is > 0 and var dur ? $"{dur:0}" : null, n: D(p, "MaxDurability"));
            Row("Material", S(p, "ArmorMaterial") is { } mat ? System.Text.RegularExpressions.Regex.Replace(mat, "([a-z])([A-Z])", "$1 $2") : null);
            Row("Covers", p.GetValueOrDefault("armorColliders") is JsonArray parts && parts.Count > 0
                ? string.Join(", ", parts.Select(x => x?.ToString()).Where(x => x != null).Select(x => locale.GetValueOrDefault("DeathInfo/" + x)?.Trim() is { Length: > 0 } z ? z : System.Text.RegularExpressions.Regex.Replace(x!, "([a-z])([A-Z])", "$1 $2")).Distinct().Take(8)) : null);
            Group("Protection");
        }

        // what wearing it costs (armour, rigs, helmets, bags)
        var speed2 = D(p, "speedPenaltyPercent") ?? 0;
        Row("Move speed", speed2 != 0 ? Signed(speed2, "%") : null, Tone(speed2), speed2);
        var turn = D(p, "mousePenalty") ?? 0;
        Row("Turn speed", turn != 0 ? Signed(turn, "%") : null, Tone(turn), turn);
        var ergoPen = D(p, "weaponErgonomicPenalty") ?? 0;
        Row("Ergonomics", ergoPen != 0 ? Signed(ergoPen, "%") : null, Tone(ergoPen), ergoPen);
        Group("While worn");

        // meds, food and drink: what using it does
        var uses = D(p, "MaxHpResource") ?? 0;
        if (p.GetValueOrDefault("effects_health") is JsonObject health || p.GetValueOrDefault("effects_damage") is JsonObject || uses > 0)
        {
            Row("Heals", uses > 0 ? $"{uses:0} HP in all" : null, "good", uses);
            Row("Heal rate", D(p, "hpResourceRate") is > 0 and var hpRate ? $"{hpRate:0} HP a second" : null, n: D(p, "hpResourceRate"));
            Row("Use time", (D(p, "medUseTime") ?? D(p, "foodUseTime")) is > 0 and var time ? $"{time:0.#} s" : null, n: D(p, "medUseTime") ?? D(p, "foodUseTime"));
            if (p.GetValueOrDefault("effects_health") is JsonObject h)
            {
                foreach (var (effect, node) in h)
                {
                    var value = node?["value"]?.GetValue<double>() ?? 0;
                    Row(Stat(locale, effect), value != 0 ? Signed(value) : null, Tone(value), value);
                }
            }

            if (p.GetValueOrDefault("effects_damage") is JsonObject dmg)
            {
                foreach (var (effect, node) in dmg)
                {
                    var cost = node?["cost"]?.GetValue<double>() ?? 0;
                    var duration = node?["duration"]?.GetValue<double>() ?? 0;
                    Row(duration > 0 && cost <= 0 ? $"Holds off {Condition(locale, effect).ToLowerInvariant()}" : $"Cures {Condition(locale, effect).ToLowerInvariant()}",
                        duration > 0 ? $"for {duration:0} s" : cost > 0 ? $"uses {cost:0} HP" : "yes", "good");
                }
            }

            Group("When used");
        }

        // a stim: each effect, when it starts and how long it lasts
        if (S(p, "StimulatorBuffs") is { } buffName && Buffs().GetValueOrDefault(buffName) is JsonArray buffs)
        {
            foreach (var buff in buffs.OfType<JsonObject>())
            {
                var type = buff["BuffType"]?.ToString() ?? "";
                var value = buff["Value"]?.GetValue<double>() ?? 0;
                var delay = buff["Delay"]?.GetValue<double>() ?? 0;
                var duration = buff["Duration"]?.GetValue<double>() ?? 0;
                var skill = buff["SkillName"]?.ToString();
                var absolute = buff["AbsoluteValue"] is JsonValue av && av.TryGetValue<bool>(out var abs) && abs;
                var bad = BadBuff(type, value);
                var label = type == "SkillRate" && !string.IsNullOrEmpty(skill) ? Stat(locale, skill) + " skill" : Stat(locale, type);
                var amount = value == 0 ? "" : absolute || Math.Abs(value) >= 1 ? Signed(value) + " " : Signed(value * 100, "% ");
                Row(label, $"{amount}{(delay > 0 ? $"after {delay:0} s, " : "")}for {duration:0} s".Trim(), bad ? "bad" : "good");
            }

            Group("Effects");
        }

        // a bag, rig or case: room inside
        if (p.GetValueOrDefault("Grids") is JsonArray grids && grids.Count > 0)
        {
            var cells = grids.OfType<JsonObject>().Sum(g => (g["_props"]?["cellsH"]?.GetValue<double>() ?? g["Props"]?["CellsH"]?.GetValue<double>() ?? 0)
                * (g["_props"]?["cellsV"]?.GetValue<double>() ?? g["Props"]?["CellsV"]?.GetValue<double>() ?? 0));
            Row("Space inside", cells > 0 ? $"{cells:0} cells{(grids.Count > 1 ? $" in {grids.Count} parts" : "")}" : null, n: cells);
            Group("Storage");
        }

        // the rules an item plays by, for items whose use the game hard-codes (an amulet, a special-slot tool):
        // where it goes, how many uses it has, and whether you keep it when you die
        var special = p.GetValueOrDefault("IsSpecialSlotOnly") is JsonValue so && so.TryGetValue<bool>(out var only) && only;
        Row("Goes in", special ? "Special slot only" : null);
        Row("Uses", D(p, "MaxUsages") is > 1 and var usages ? $"{usages:0}" : null, n: D(p, "MaxUsages"));
        // "Unlootable" means nobody can take it off a body of one of the listed sides while it sits in the listed slot,
        // so whoever wears it there keeps it when they die. No side listed: it never applies.
        var unlootable = p.GetValueOrDefault("Unlootable") is JsonValue ul && ul.TryGetValue<bool>(out var keep) && keep;
        var sides = template.Properties?.UnlootableFromSide?.Select(s => s.ToString()).ToList() ?? [];
        var keptSlot = template.Properties?.UnlootableFromSlot?.ToString();
        unlootable = unlootable && sides.Count > 0;
        if (unlootable)
        {
            var slotName = string.IsNullOrEmpty(keptSlot) ? null : Named(locale, keptSlot);
            var where = slotName == null ? "" : slotName.EndsWith("slot", StringComparison.OrdinalIgnoreCase)
                ? $", as long as it's in your {slotName.ToLowerInvariant()}" : $", as long as it's in your {slotName.ToLowerInvariant()} slot";
            // the sides it doesn't cover (a PMC's knife is lost on a scav run), named as the locale spells them
            // every side any item on this server is kept for
            var all = _keptSides ??= templates.Items.Values.SelectMany(t => t.Properties?.UnlootableFromSide ?? []).Select(x => x.ToString())
                .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            var not = all.Where(s => !sides.Contains(s, StringComparer.OrdinalIgnoreCase)).Select(s => locale.GetValueOrDefault(s.ToUpperInvariant()) ?? s).ToList();
            Row("When you die", $"You keep it{where}. Nobody can loot it off your body.{(not.Count > 0 ? $" Playing as {string.Join(" or ", not)}, you can lose it like anything else." : "")}", "good");
        }
        if (special || unlootable)
        {
            Row("Sell or insure", (p.GetValueOrDefault("IsUnsaleable") is JsonValue us && us.TryGetValue<bool>(out var unsale) && unsale ? "Can't be sold" : null)
                ?? (p.GetValueOrDefault("InsuranceDisabled") is JsonValue ins && ins.TryGetValue<bool>(out var noIns) && noIns ? "Can't be insured" : null));
        }

        Group("How it works");
        return groups;
    }

    public sealed record StatRow(string label, string value, string tone, double? n);

    public sealed record StatGroup(string title, List<StatRow> rows);

    // What an ammo pack holds: the round and how many, or null for anything else.
    public object? Unpacks(string id, IReadOnlyDictionary<string, string> locale, Dictionary<string, string> names)
    {
        // as SPT's own ItemHelper reads an ammo box
        if (!MongoId.IsValidMongoId(id) || !templates.Items.TryGetValue(new MongoId(id), out var template)
            || template.Properties?.StackSlots?.FirstOrDefault() is not { } slot)
        {
            return null;
        }

        var count = slot.MaxCount ?? 0;
        var round = slot.Properties?.Filters?.FirstOrDefault()?.Filter?.FirstOrDefault().ToString();
        if (round == null || count <= 0)
        {
            return null;
        }

        names[round] = locale.GetValueOrDefault(round + " Name") ?? round;
        return new { id = round, count };
    }
}
