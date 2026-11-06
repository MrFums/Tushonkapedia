using System.Text.Json.Nodes;
using SPTarkov.Server.Core.Models.Common;

namespace Tushonkapedia.Server;

// A med's, food's or drink's page, laid out as the wiki lays out medical items: how much it heals and how fast, what
// it treats and at what cost, what eating or drinking it does, a stim's effects in the order they start, and every
// other item of the same kind side by side. All from the server's own item table and globals, so a mod's meds, stims,
// food and drink read like any other. What counts is the item's shape: anything the server gives a use time to.
public sealed partial class ItemWiki
{
    // how long it takes to use: a med's own figure, else a food's or drink's
    private static double? UseTime(Dictionary<string, JsonNode?> p) =>
        D(p, "medUseTime") is > 0 and var m ? m : D(p, "foodUseTime") is > 0 and var f ? f : null;

    private static List<(string effect, string label, double value)> Health(Dictionary<string, JsonNode?> p, IReadOnlyDictionary<string, string> locale) =>
        p.GetValueOrDefault("effects_health") is JsonObject h
            ? h.Select(e => (e.Key, Stat(locale, e.Key), e.Value?["value"]?.GetValue<double>() ?? 0)).Where(e => e.Item3 != 0).ToList()
            : [];

    private static List<object> Treats(Dictionary<string, JsonNode?> p, IReadOnlyDictionary<string, string> locale) =>
        p.GetValueOrDefault("effects_damage") is JsonObject dmg
            ? dmg.Select(e => (object)new
            {
                effect = e.Key,
                label = Condition(locale, e.Key),
                cost = e.Value?["cost"]?.GetValue<double>() is > 0 and var c ? c : (double?)null,
                duration = e.Value?["duration"]?.GetValue<double>() is > 0 and var d ? d : (double?)null,
                // an effect with a duration and no HP cost holds the condition off for a while instead of curing it
                stops = (e.Value?["duration"]?.GetValue<double>() ?? 0) > 0 && (e.Value?["cost"]?.GetValue<double>() ?? 0) <= 0
            }).ToList()
            : [];

    public object? MedsFor(string id, IReadOnlyDictionary<string, string> locale, Dictionary<string, string> names)
    {
        if (!MongoId.IsValidMongoId(id) || !templates.Items.TryGetValue(new MongoId(id), out var med) || med.Properties == null
            || UseTime(PropsOf(med.Properties)) == null)
        {
            return null;
        }

        var p = PropsOf(med.Properties);
        string Name(string item) => names[item] = locale.GetValueOrDefault(item + " Name") is { Length: > 0 } n ? n
            : templates.Items.TryGetValue(new MongoId(item), out var t) ? t.Name ?? item : item;

        var parent = med.Parent.ToString();
        // the kind is the parent node's name in the server's locale, else its own name in the item tree split into words
        var kind = locale.GetValueOrDefault(parent + " Name") is { Length: > 0 } ln ? ln
            : templates.Items.TryGetValue(med.Parent, out var pt) && pt.Name is { Length: > 0 } pn ? Spaced(pn) : null;

        var health = Health(p, locale).Select(e => new { e.effect, e.label, e.value }).ToList();

        var buffs = new List<object>();
        if (S(p, "StimulatorBuffs") is { } buffName && Buffs().GetValueOrDefault(buffName) is JsonArray list)
        {
            foreach (var buff in list.OfType<JsonObject>())
            {
                var type = buff["BuffType"]?.ToString() ?? "";
                var value = buff["Value"]?.GetValue<double>() ?? 0;
                var skill = buff["SkillName"]?.ToString();
                buffs.Add(new
                {
                    type,
                    // a skill buff by the skill's name; any other rate is an amount each second
                    label = type == "SkillRate" && !string.IsNullOrEmpty(skill) ? Stat(locale, skill) + " skill"
                        : Stat(locale, type) + (type.EndsWith("Rate", StringComparison.Ordinal) ? " per second" : ""),
                    value,
                    delay = buff["Delay"]?.GetValue<double>() ?? 0,
                    duration = buff["Duration"]?.GetValue<double>() ?? 0,
                    chance = buff["Chance"]?.GetValue<double>() ?? 1,
                    absolute = buff["AbsoluteValue"] is JsonValue av && av.TryGetValue<bool>(out var abs) && abs,
                    bad = BadBuff(type, value)
                });
            }
        }

        // every item of the same kind the server knows (and has a name for), and the server gives a use time, biggest first
        var siblings = ItemsUnder(parent)
            .Where(x => locale.ContainsKey(x + " Name"))
            .Select(x => (id: x, p: PropsOf(templates.Items[new MongoId(x)].Properties)))
            .Where(x => UseTime(x.p) != null)
            .Select(x => new
            {
                x.id, name = Name(x.id),
                hp = D(x.p, "MaxHpResource") is > 0 and var hp ? hp : (double?)null,
                rate = D(x.p, "hpResourceRate") is > 0 and var r ? r : (double?)null,
                useTime = UseTime(x.p),
                // food and drink: how much is in it, and what all of it does
                amount = D(x.p, "foodUseTime") is > 0 && D(x.p, "MaxResource") is > 0 and var amt ? amt : (double?)null,
                health = Health(x.p, locale).Select(e => new { e.effect, e.label, e.value }).ToList(),
                treats = Treats(x.p, locale),
                // the mod that added it, by the server's own record; "" when it can't tell which
                mod = BaseGame(x.id) ? null : ModOf(x.id, false) ?? "",
                self = x.id == id
            })
            .OrderByDescending(x => x.hp ?? 0).ThenByDescending(x => x.health.Sum(h => h.value)).ThenBy(x => x.name)
            .ToList();

        return new
        {
            kind,
            hp = D(p, "MaxHpResource") is > 0 and var total ? total : (double?)null,
            rate = D(p, "hpResourceRate") is > 0 and var rate ? rate : (double?)null,
            useTime = UseTime(p),
            // eaten or drunk rather than applied, by the server's own figure for it
            food = D(p, "foodUseTime") is > 0,
            amount = D(p, "foodUseTime") is > 0 && D(p, "MaxResource") is > 0 and var amount ? amount : (double?)null,
            treats = Treats(p, locale),
            health,
            buffs,
            siblings
        };
    }
}
