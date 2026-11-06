using System.Text.Json.Nodes;
using SPTarkov.Server.Core.Models.Common;

namespace Tushonkapedia.Server;

// A gun's page, laid out as the wiki lays out a weapon: what kind of gun it is, the rounds it fires and the
// magazine it comes with, each mod slot with the parts that go in it, and the builds (presets) the server knows.
// All from the server's own item table and globals, so a mod's guns and slots read like any other.
public sealed partial class ItemWiki
{
    private Dictionary<string, List<string>>? _treeChildren;

    // the items under a node of the item tree (or the item itself when it's one)
    private IEnumerable<string> ItemsUnder(string node)
    {
        if (_treeChildren == null)
        {
            var children = new Dictionary<string, List<string>>();
            foreach (var (key, item) in templates.Items)
            {
                var parent = item.Parent.ToString();
                if (!children.TryGetValue(parent, out var list))
                {
                    children[parent] = list = [];
                }

                list.Add(key.ToString());
            }

            _treeChildren = children;
        }

        var stack = new Stack<string>([node]);
        var seen = new HashSet<string>();
        while (stack.Count > 0)
        {
            var at = stack.Pop();
            if (!seen.Add(at))
            {
                continue;
            }

            if (MongoId.IsValidMongoId(at) && templates.Items.TryGetValue(new MongoId(at), out var t) && t.Type == "Item")
            {
                yield return at;
            }

            foreach (var child in _treeChildren.GetValueOrDefault(at) ?? [])
            {
                stack.Push(child);
            }
        }
    }

    // the game's slot names said plainly ("mod_pistol_grip" is "Pistol grip")
    private static string SlotName(string? raw)
    {
        var s = (raw ?? "").ToLowerInvariant();
        s = s.StartsWith("mod_") ? s[4..] : s;
        s = System.Text.RegularExpressions.Regex.Replace(s, "_\\d+$", "");
        var said = s switch
        {
            "reciever" or "receiver" => "Receiver",
            "sight_rear" => "Rear sight",
            "sight_front" => "Front sight",
            "charge" => "Charging handle",
            "tactical" => "Tactical device",
            "mount" => "Mount",
            "nvg" => "Night vision",
            "patron_in_weapon" => "Chamber",
            "launcher" => "Underbarrel launcher",
            "bipod" => "Bipod",
            _ => s.Replace('_', ' ')
        };
        return said.Length > 0 ? char.ToUpperInvariant(said[0]) + said[1..] : "Slot";
    }


    public object? WeaponFor(string id, IReadOnlyDictionary<string, string> locale, Dictionary<string, string> names)
    {
        if (!IsWeapon(id) || !templates.Items.TryGetValue(new MongoId(id), out var gun) || gun.Properties == null)
        {
            return null;
        }

        string Name(string item) => names[item] = locale.GetValueOrDefault(item + " Name") is { Length: > 0 } n ? n
            : templates.Items.TryGetValue(new MongoId(item), out var t) ? t.Name ?? item : item;

        var p = PropsOf(gun.Properties);
        var props = gun.Properties;

        List<string> Allowed(IEnumerable<SPTarkov.Server.Core.Models.Eft.Common.Tables.SlotFilter>? filters) =>
            (filters ?? []).SelectMany(f => f?.Filter ?? []).SelectMany(a => ItemsUnder(a.ToString()))
                .Distinct().Where(x => x != id).OrderBy(Name).ToList();

        // the rounds it chambers (or its magazines take, for a gun with no chamber slot)
        var chamber = (props.Chambers ?? []).SelectMany(c => Allowed(c?.Properties?.Filters)).Distinct().ToList();
        var defAmmo = props.DefAmmo?.ToString() ?? S(p, "defAmmo");
        var ammo = chamber.Select(a =>
        {
            var ap = templates.Items.TryGetValue(new MongoId(a), out var at) ? PropsOf(at.Properties) : [];
            var pellets = D(ap, "buckshotBullets") ?? D(ap, "ProjectileCount") ?? 1;
            return new
            {
                id = a, name = Name(a), damage = D(ap, "Damage"), pellets = pellets > 1 ? pellets : (double?)null,
                pen = D(ap, "PenetrationPower"), armor = D(ap, "ArmorDamage"), speed = D(ap, "InitialSpeed"),
                tracer = ap.GetValueOrDefault("Tracer") is JsonValue tv && tv.TryGetValue<bool>(out var tr) && tr,
                given = a == defAmmo
            };
        }).OrderByDescending(a => a.pen ?? 0).ToList();

        var slots = (props.Slots ?? []).Where(s => s != null).Select(s => new
        {
            name = SlotName(s.Name), raw = s.Name, required = s.Required == true,
            fits = Allowed(s.Properties?.Filters)
        }).Where(s => s.fits.Count > 0).ToList();

        // the builds the server keeps for this gun: the default (the one the encyclopedia shows) first
        var builds = ((IEnumerable<SPTarkov.Server.Core.Models.Spt.Tables.Preset>?)Rules?.Globals.ItemPresets?.Values ?? [])
            .Where(pr => pr.Items is { Count: > 0 } && pr.Items.Any(i => i.ParentId == null && i.Template.ToString() == id))
            .OrderByDescending(pr => pr.Encyclopedia != null)
            .ThenBy(pr => pr.Name)
            .Select(pr =>
            {
                var root = pr.Items!.First(i => i.ParentId == null).Id.ToString();
                return new
                {
                    id = pr.Id.ToString(),
                    name = locale.GetValueOrDefault(pr.Id.ToString()) is { Length: > 0 } ln ? ln : pr.Name,
                    standard = pr.Encyclopedia != null,
                    parts = pr.Items!.Where(i => i.ParentId != null && i.SlotId != "patron_in_weapon" && i.SlotId != "cartridges")
                        .Select(i => new { id = Name(i.Template.ToString()) is { } ? i.Template.ToString() : "", slot = SlotName(i.SlotId), top = i.ParentId == root })
                        .ToList()
                };
            }).Take(12).ToList();

        // the server's mastery group holding this gun: the guns that share its mastery and what the levels take
        var mastering = (Rules?.Globals.Configuration?.Mastering ?? []).FirstOrDefault(m => m?.Templates?.Any(t => t.ToString() == id) == true);
        var mastery = mastering == null ? null : new
        {
            group = mastering.Name,
            level2 = mastering.Level2,
            level3 = mastering.Level3,
            others = (mastering.Templates ?? []).Select(t => t.ToString()).Where(t => t != id && templates.Items.ContainsKey(new MongoId(t))).Distinct().Count()
        };

        var magType = props.DefMagType?.ToString() ?? S(p, "defMagType");
        return new
        {
            // the handbook shelf it sits on ("Assault rifles"), else its own class split into words
            kind = Category(id, locale) ?? (S(p, "weapClass") is { } wc ? Spaced(wc) : null),
            use = S(p, "weapUseType") switch { "primary" => "Primary", "secondary" => "Secondary (holster)", var o => o },
            caliber = S(p, "ammoCaliber") is { } cal ? QuestGraphBuilder.Caliber(cal) : null,
            defAmmo = defAmmo is { } da && templates.Items.ContainsKey(new MongoId(da)) ? (Name(da) is { } ? da : null) : null,
            defMag = magType is { } dm && templates.Items.ContainsKey(new MongoId(dm)) ? (Name(dm) is { } ? dm : null) : null,
            rate = D(p, "bFirerate"),
            ergo = D(p, "Ergonomics"),
            up = D(p, "RecoilForceUp"),
            back = D(p, "RecoilForceBack"),
            eff = D(p, "bEffDist"),
            sight = D(p, "SightingRange"),
            durability = D(p, "MaxDurability"),
            mastery,
            ammo,
            slots,
            builds
        };
    }
}
