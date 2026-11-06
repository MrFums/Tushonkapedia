using System.Text.Json;

namespace Tushonkapedia.Server;

// Where a container's parts sit, as the game draws it. Rigs and some backpacks name a layout (RigLayoutName,
// GridLayoutName): a piece of the game's UI that places each part. The server only has the parts' sizes, so the game
// plugin opens each layout in the menu, sizes its parts from the server's grids, and sends back where each one ended
// up. Kept in data/grid-layouts.json, keyed by layout name and the part sizes (a mod can reuse a layout with other
// sizes), as [x, y, width, height] in the game's pixels for each part, in the order of the item's grids.
public sealed partial class ItemWiki
{
    private readonly Lock _layoutLock = new();
    private Dictionary<string, List<float[]>>? _layouts;

    private string LayoutFile => Path.Combine(folder, "data", "grid-layouts.json");

    private Dictionary<string, List<float[]>> Layouts()
    {
        lock (_layoutLock)
        {
            if (_layouts != null)
            {
                return _layouts;
            }

            try
            {
                var read = File.Exists(LayoutFile) ? JsonSerializer.Deserialize<Dictionary<string, List<float[]>>>(File.ReadAllText(LayoutFile)) : null;
                _layouts = new Dictionary<string, List<float[]>>(read ?? [], StringComparer.Ordinal);
            }
            catch (Exception ex) when (ex is IOException or JsonException)
            {
                logger.Warning($"[Tushonkapedia] Couldn't read {LayoutFile}: {ex.Message}");
                _layouts = new(StringComparer.Ordinal);
            }

            return _layouts;
        }
    }

    // the layout an item's parts are drawn with, and the part sizes, or null when it has none
    private static (string Name, List<int[]> Grids)? LayoutOf(SPTarkov.Server.Core.Models.Eft.Common.Tables.TemplateItem? item)
    {
        var props = item?.Properties;
        var name = props?.RigLayoutName is { Length: > 0 } rig ? rig : props?.GridLayoutName is { Length: > 0 } grid ? grid : null;
        var grids = (props?.Grids ?? []).Select(g => new[] { (int)(g.Properties?.CellsH ?? 0), (int)(g.Properties?.CellsV ?? 0) }).ToList();
        return name == null || grids.Count == 0 ? null : (name, grids);
    }

    private static string LayoutKey(string name, List<int[]> grids) => name + "|" + string.Join(",", grids.Select(g => g[0] + "x" + g[1]));

    // the layouts the plugin hasn't measured yet, each with the part sizes to give it
    public List<object> LayoutsWanted()
    {
        var saved = Layouts();
        return templates.Items.Values.Select(LayoutOf).Where(l => l != null).Select(l => l!.Value)
            .GroupBy(l => LayoutKey(l.Name, l.Grids)).Where(g => !saved.ContainsKey(g.Key))
            .Select(g => (object)new { key = g.Key, name = g.First().Name, grids = g.First().Grids }).ToList();
    }

    // what the plugin measured; only keys the server asks for, with one sane box per part the game shows, are kept. A
    // layout with fewer parts than the item has shows only the first ones in game, so fewer boxes than parts is right.
    public int SaveLayouts(Dictionary<string, List<float[]>> measured)
    {
        // many items share one layout and part sizes, so one entry per key
        var wanted = templates.Items.Values.Select(LayoutOf).Where(l => l != null).DistinctBy(l => LayoutKey(l!.Value.Name, l.Value.Grids)).ToDictionary(l => LayoutKey(l!.Value.Name, l.Value.Grids), l => l!.Value.Grids.Count, StringComparer.Ordinal);
        static bool Sane(float[]? b) => b is { Length: 4 } && b.All(v => float.IsFinite(v) && Math.Abs(v) < 5000) && b[2] > 0 && b[3] > 0;
        var saved = 0;
        lock (_layoutLock)
        {
            var all = Layouts();
            foreach (var (key, boxes) in measured)
            {
                // no boxes: the game has no such layout, so it isn't asked for again (and pages draw the plain grid)
                if (key != null && wanted.TryGetValue(key, out var count) && boxes != null && boxes.Count <= count && boxes.All(Sane))
                {
                    all[key] = boxes;
                    saved++;
                }
            }

            if (saved > 0)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(LayoutFile)!);
                File.WriteAllText(LayoutFile, JsonSerializer.Serialize(all));
            }
        }

        return saved;
    }

    // where each of the item's parts sits, [x, y, width, height] in the game's pixels, or null when not measured
    private List<float[]>? GridLayout(SPTarkov.Server.Core.Models.Eft.Common.Tables.TemplateItem? item)
        => LayoutOf(item) is { } l && Layouts().GetValueOrDefault(LayoutKey(l.Name, l.Grids)) is { Count: > 0 } boxes ? boxes : null;
}
