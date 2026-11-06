using System.Text.Json.Nodes;
using SPTarkov.Server.Core.Models.Common;

namespace Tushonkapedia.Server;

// A container's page, laid out as the wiki lays out cases, bags and rigs: room inside against room taken, what it
// takes and what it won't, what it can itself go in, and every other item of the same kind side by side. Anything the
// server gives grids counts. All from the server's own item table, so a mod's containers and filters read like any
// other.
public sealed partial class ItemWiki
{
    private sealed record Filters(List<(HashSet<string> take, HashSet<string> bar, double w, double h)> Grids, double Cells, int Parts);

    // every container's grid filters, read once: a grid with no list of what it takes takes anything
    private Dictionary<string, Filters>? _filters;

    private Dictionary<string, Filters> AllFilters()
    {
        if (_filters != null)
        {
            return _filters;
        }

        var map = new Dictionary<string, Filters>();
        foreach (var (key, item) in templates.Items)
        {
            if (item.Properties?.Grids?.Any() != true)
            {
                continue;
            }

            var grids = new List<(HashSet<string>, HashSet<string>, double, double)>();
            var cells = 0.0;
            foreach (var g in (Node(item.Properties) as JsonObject)?["Grids"] as JsonArray ?? [])
            {
                var gp = g?["_props"] ?? g?["Props"];
                var gw = Num(gp?["cellsH"]) ?? Num(gp?["CellsH"]) ?? 0;
                var gh = Num(gp?["cellsV"]) ?? Num(gp?["CellsV"]) ?? 0;
                cells += gw * gh;
                var take = new HashSet<string>();
                var bar = new HashSet<string>();
                foreach (var f in (gp?["filters"] ?? gp?["Filters"]) as JsonArray ?? [])
                {
                    foreach (var x in (f?["Filter"] ?? f?["filter"]) as JsonArray ?? [])
                    {
                        if (x?.ToString() is { Length: > 0 } s)
                        {
                            take.Add(s);
                        }
                    }

                    foreach (var x in (f?["ExcludedFilter"] ?? f?["excludedFilter"]) as JsonArray ?? [])
                    {
                        if (x?.ToString() is { Length: > 0 } s)
                        {
                            bar.Add(s);
                        }
                    }
                }

                grids.Add((take, bar, gw, gh));
            }

            if (cells > 0)
            {
                map[key.ToString()] = new Filters(grids, cells, grids.Count);
            }
        }

        return _filters = map;
    }

    // A kind's tree name can be terse ("Port. container"); when every item of that kind sits on one shelf of the
    // server's handbook, the shelf's own name ("Secure containers") says it plainer
    private Dictionary<string, string?>? _shelf;
    private string? ShelfName(string kind, IReadOnlyDictionary<string, string> locale)
    {
        _shelf ??= Handbook()
            .SelectMany(e => Lineage(e.Key).Skip(1).Select(node => (node, shelf: e.Value.Category)))
            .GroupBy(x => x.node)
            .ToDictionary(g => g.Key, g => g.Select(x => x.shelf).Distinct().Count() == 1 ? g.First().shelf : null);
        return _shelf.GetValueOrDefault(kind) is { } shelf && locale.GetValueOrDefault(shelf) is { Length: > 0 } name ? name : null;
    }

    // the item and every node above it in the item tree
    private List<string> Lineage(string id)
    {
        var line = new List<string>();
        if (!MongoId.IsValidMongoId(id) || !templates.Items.TryGetValue(new MongoId(id), out var at))
        {
            return line;
        }

        for (var steps = 0; at != null && steps < 20; at = templates.Items.GetValueOrDefault(at.Parent), steps++)
        {
            line.Add(at.Id.ToString());
        }

        return line;
    }

    // whether some grid of the container lets the item in, by the item's own id or a kind above it, and is big
    // enough for it either way round
    private static bool Takes(Filters f, List<string> line, double w, double h) =>
        f.Grids.Any(g => (g.take.Count == 0 || line.Any(g.take.Contains)) && !line.Any(g.bar.Contains)
            && ((g.w >= w && g.h >= h) || (g.w >= h && g.h >= w)));

    public object? ContainerFor(string id, IReadOnlyDictionary<string, string> locale, Dictionary<string, string> names)
    {
        var all = AllFilters();
        if (!all.TryGetValue(id, out var mine) || !templates.Items.TryGetValue(new MongoId(id), out var item))
        {
            return null;
        }

        // a kind's name from the locale, else the server's own node name split into words
        string Name(string x) => names[x] = locale.GetValueOrDefault(x + " Name") is { Length: > 0 } n ? n
            : MongoId.IsValidMongoId(x) && templates.Items.TryGetValue(new MongoId(x), out var t) && t.Name is { Length: > 0 } tn
                ? t.Type == "Node" ? Spaced(tn) : tn : x;
        bool IsKind(string x) => MongoId.IsValidMongoId(x) && templates.Items.TryGetValue(new MongoId(x), out var t) && t.Type == "Node";
        bool Known(string x) => MongoId.IsValidMongoId(x) && templates.Items.ContainsKey(new MongoId(x));
        // the top of the item tree: a node with no parent of its own
        bool IsRoot(string x) => IsKind(x) && !templates.Items.ContainsKey(templates.Items[new MongoId(x)].Parent);

        var take = mine.Grids.SelectMany(g => g.take).Where(Known).ToHashSet();
        var bar = mine.Grids.SelectMany(g => g.bar).Where(Known).ToHashSet();
        var anything = mine.Grids.Any(g => g.take.Count == 0) || take.Any(IsRoot);
        object Split(IEnumerable<string> ids) => new
        {
            kinds = ids.Where(x => IsKind(x) && !IsRoot(x)).Select(x => new { id = x, name = ShelfName(x, locale) ?? Name(x) }).OrderBy(x => x.name).ToList(),
            items = ids.Where(x => !IsKind(x)).Select(x => { Name(x); return x; }).OrderBy(x => names[x]).ToList()
        };

        var parent = item.Parent.ToString();
        var kind = locale.GetValueOrDefault(parent + " Name") is { Length: > 0 } ln ? ln
            : templates.Items.TryGetValue(item.Parent, out var pt) && pt.Name is { Length: > 0 } pn ? Spaced(pn) : null;

        // what this one can be put in: every other container a player can own (the server's handbook lists it, so
        // not a jacket or a safe on a map) whose grids let it in and have room for it
        var line = Lineage(id);
        var book = Handbook();
        double w = item.Properties?.Width ?? 1, h = item.Properties?.Height ?? 1;
        var storedIn = all.Where(c => c.Key != id && locale.ContainsKey(c.Key + " Name") && book.ContainsKey(c.Key) && Takes(c.Value, line, w, h))
            .Select(c => c.Key).ToList();
        var inKinds = storedIn.GroupBy(c => templates.Items[new MongoId(c)].Parent.ToString())
            .Select(g => new { id = g.Key, name = Name(g.Key), items = g.Select(x => { Name(x); return x; }).OrderBy(x => names[x]).ToList() })
            .OrderByDescending(g => g.items.Count).ThenBy(g => g.name).ToList();

        var siblings = ItemsUnder(parent)
            .Where(x => locale.ContainsKey(x + " Name") && all.ContainsKey(x))
            .Select(x => (id: x, f: all[x], p: templates.Items[new MongoId(x)].Properties))
            .Select(x => new
            {
                x.id, name = Name(x.id), cells = x.f.Cells, parts = x.f.Parts,
                width = x.p?.Width, height = x.p?.Height, weight = x.p?.Weight,
                mod = BaseGame(x.id) ? null : ModOf(x.id, false) ?? "",
                self = x.id == id
            })
            .OrderByDescending(x => x.cells).ThenBy(x => x.name)
            .ToList();

        return new
        {
            kind,
            cells = mine.Cells,
            anything,
            takes = anything ? null : Split(take),
            bars = Split(bar),
            storedIn = inKinds,
            siblings
        };
    }
}
