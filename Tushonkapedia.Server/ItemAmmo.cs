using SPTarkov.Server.Core.Models.Enums;
using System.Text.Json.Nodes;
using SPTarkov.Server.Core.Models.Common;

namespace Tushonkapedia.Server;

// A round's page, laid out as the wiki lays out ammunition: what the round does to flesh and armour, what it does
// to the gun that fires it, and every other round of the same calibre side by side. All from the server's own item
// table, so a mod's rounds and calibres read like any other.
public sealed partial class ItemWiki
{
    private static bool Flag(Dictionary<string, JsonNode?> p, string key) =>
        p.GetValueOrDefault(key) is JsonValue v && v.TryGetValue<bool>(out var b) && b;

    private static bool IsRound(Dictionary<string, JsonNode?> p) =>
        D(p, "PenetrationPower") is not null && D(p, "Damage") is not null && S(p, "Caliber") is not null;

    public object? AmmoFor(string id, IReadOnlyDictionary<string, string> locale, Dictionary<string, string> names)
    {
        if (!MongoId.IsValidMongoId(id) || !templates.Items.TryGetValue(new MongoId(id), out var round) || round.Properties == null)
        {
            return null;
        }

        var p = PropsOf(round.Properties);
        if (!IsRound(p))
        {
            return null;
        }

        string Name(string item) => names[item] = locale.GetValueOrDefault(item + " Name") is { Length: > 0 } n ? n
            : templates.Items.TryGetValue(new MongoId(item), out var t) ? t.Name ?? item : item;

        static double? Pellets(Dictionary<string, JsonNode?> ap) =>
            (D(ap, "buckshotBullets") ?? D(ap, "ProjectileCount") ?? 1) is var n and > 1 ? n : null;

        var caliber = S(p, "Caliber");

        // every round of this calibre the server knows, hardest-hitting through armour first
        // (only items under the game's Ammo node are read, so the page does not serialise the whole table)
        var siblings = ItemsUnder(BaseClasses.AMMO.ToString())
            .Select(x => (id: x, p: PropsOf(templates.Items[new MongoId(x)].Properties)))
            .Where(x => IsRound(x.p) && S(x.p, "Caliber") == caliber && locale.ContainsKey(x.id + " Name"))
            .Select(x => new
            {
                x.id, name = Name(x.id), damage = D(x.p, "Damage"), pellets = Pellets(x.p),
                pen = D(x.p, "PenetrationPower"), armor = D(x.p, "ArmorDamage"), speed = D(x.p, "InitialSpeed"),
                frag = D(x.p, "FragmentationChance"), tracer = Flag(x.p, "Tracer"), self = x.id == id
            })
            .OrderByDescending(x => x.pen ?? 0).ThenByDescending(x => x.damage ?? 0)
            .ToList();

        return new
        {
            caliber = QuestGraphBuilder.Caliber(caliber!),
            damage = D(p, "Damage"),
            pellets = Pellets(p),
            pen = D(p, "PenetrationPower"),
            armor = D(p, "ArmorDamage"),
            speed = D(p, "InitialSpeed"),
            frag = D(p, "FragmentationChance"),
            ricochet = D(p, "RicochetChance"),
            lightBleed = D(p, "LightBleedingDelta"),
            heavyBleed = D(p, "HeavyBleedingDelta"),
            stamina = D(p, "StaminaBurnPerDamage"),
            accuracy = D(p, "ammoAccr"),
            recoil = D(p, "ammoRec"),
            burn = D(p, "DurabilityBurnModificator"),
            heat = D(p, "HeatFactor"),
            misfire = D(p, "MalfMisfireChance"),
            feed = D(p, "MalfFeedChance"),
            tracer = Flag(p, "Tracer"),
            subsonic = D(p, "InitialSpeed") is > 0 and < 343,
            stack = D(p, "StackMaxSize"),
            siblings
        };
    }
}
