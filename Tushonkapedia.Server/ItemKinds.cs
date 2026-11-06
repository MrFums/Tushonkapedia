using System.Collections;
using SPTarkov.Server.Core.Models.Common;

namespace Tushonkapedia.Server;

// For testing the item page: one line for each section an item page can get (armour, inside, what it does, fits on
// ...), with a few items that get it, each from a different handbook category. Built from the same calls the item
// page uses, so a mod's new kind of item shows up too. Items players never meet are left out. Worked out once.
public sealed partial class ItemWiki
{
    private readonly Lock _kindsLock = new();
    private List<object>? _kinds;

    // a section's data counts when it has something in it
    private static bool Has(object? value) => value switch
    {
        null => false,
        bool b => b,
        string s => s.Length > 0,
        IEnumerable e => e.Cast<object?>().Any(),
        _ => true
    };

    public List<object> Kinds(IReadOnlyDictionary<string, string> locale, string lang, QuestGraph graph)
    {
        lock (_kindsLock)
        {
            if (_kinds != null)
            {
                return _kinds;
            }

            var rows = new List<(string[] Sections, string? Category, string Id, string Name, bool Base, bool Named)>();
            foreach (var (mongo, template) in templates.Items)
            {
                var id = mongo.ToString();
                if (!Exists(id) || Hidden(id, graph))
                {
                    continue;
                }

                var props = template.Properties;
                var names = new Dictionary<string, string>();
                var sections = new List<string>();
                void Add(string key, bool has)
                {
                    if (has)
                    {
                        sections.Add(key);
                    }
                }

                try
                {
                    Add("armor", Has(ArmorFor(id, locale, lang)));
                    Add("grids", (props?.Grids ?? []).Any());
                    Add("gridLayout", GridLayout(template) != null);
                    Add("med", Has(MedsFor(id, locale, names)));
                    Add("weapon", Has(WeaponFor(id, locale, names)));
                    Add("ammo", Has(AmmoFor(id, locale, names)));
                    Add("unpacks", Has(Unpacks(id, locale, names)));
                    Add("fitsOn", Has(FitsOn(id, locale, names)));
                    Add("opens", Has(KeyOpens(id, locale)));
                    Add("uses", props?.MaximumNumberOfUsage > 0);
                    Add("questItem", props?.QuestItem == true);
                    Add("isCase", IsCase(id));
                }
                catch (Exception ex)
                {
                    logger.Warning($"[Tushonkapedia] Couldn't sort {id} for the test list: {ex.Message}");
                    continue;
                }

                var named = locale.GetValueOrDefault(id + " Name") is { Length: > 0 } n && n.Any(char.IsLetterOrDigit) ? n : null;
                rows.Add((sections.ToArray(), Category(id, locale), id, named ?? template.Name ?? id, BaseGame(id), named != null));
            }

            // the items with no section of their own count as one more line
            var lines = rows
                .SelectMany(r => r.Sections.Length > 0 ? r.Sections.Select(x => (Section: x, Row: r)) : [(Section: "", Row: r)])
                .GroupBy(x => x.Section)
                .Select(g => (object)new
                {
                    section = g.Key.Length > 0 ? g.Key : null,
                    count = g.Count(),
                    // the server's own named items first, so the example is one every player has; one per category
                    examples = g.Select(x => x.Row)
                        .OrderByDescending(r => r.Base).ThenByDescending(r => r.Named).ThenBy(r => r.Name, StringComparer.CurrentCulture)
                        .DistinctBy(r => r.Category ?? "").Take(3)
                        .Select(r => new { id = r.Id, name = r.Name, category = r.Category, modded = !r.Base })
                })
                .ToList();

            // kept once the loot is read, since only then can it tell which items nothing gives
            if (_loot == null)
            {
                return lines;
            }

            _kinds = lines;
            return _kinds;
        }
    }
}
