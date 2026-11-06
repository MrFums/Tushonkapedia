namespace Tushonkapedia.Server;

// What a map page draws on its map: where each boss can appear (the boss spawn points in the zones its spawn entry
// names, with its chance), where PMCs start, and where the extracts are. Spawns come from the server's own map
// data, so mods that move or add them are drawn; extracts from the map scenes (exit-places.json, read by
// tools/box_places.py) and from raids (the game plugin), as SPT's data has no positions for them.
public sealed partial class ItemWiki
{
    public object? MapPoints(string map, string? version, IReadOnlyDictionary<string, string> locale)
    {
        var location = locations.GetDictionary().Values.FirstOrDefault(l => l?.Base?.Id?.Equals(map, StringComparison.OrdinalIgnoreCase) == true);
        var info = location?.Base;
        if (info == null)
        {
            return null;
        }

        var spawns = (info.SpawnPointParams ?? []).Where(p => p?.Position != null).ToList();
        static bool Has(IEnumerable<string>? list, string what) => list?.Any(c => c.Equals(what, StringComparison.OrdinalIgnoreCase)) == true;

        // bosses: one mark per zone they can spawn in, in the middle of its boss spawn points, naming every boss there
        var byZone = new Dictionary<string, List<(string Name, double Chance)>>(StringComparer.OrdinalIgnoreCase);
        foreach (var spawn in info.BossLocationSpawn ?? [])
        {
            var chance = spawn.BossChance ?? 0;
            if (chance <= 0 || string.IsNullOrEmpty(spawn.BossName) || string.IsNullOrEmpty(spawn.BossZone)
                || spawn.BossName.StartsWith("pmc", StringComparison.OrdinalIgnoreCase) || !BotRoles.IsEnemy(spawn.BossName))
            {
                continue;
            }

            var name = BotName(spawn.BossName, locale);
            foreach (var zone in spawn.BossZone.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                if (!byZone.TryGetValue(zone, out var here))
                {
                    byZone[zone] = here = [];
                }

                var known = here.FindIndex(h => h.Name == name);
                if (known < 0)
                {
                    here.Add((name, chance));
                }
                else if (chance > here[known].Chance)
                {
                    here[known] = (name, chance);
                }
            }
        }

        var bosses = new List<object>();
        foreach (var (zone, who) in byZone)
        {
            var points = spawns.Where(p => Has(p.Categories, "Boss") && zone.Equals(p.BotZoneName, StringComparison.OrdinalIgnoreCase)).ToList();
            if (points.Count == 0)
            {
                continue;
            }

            bosses.Add(new
            {
                name = string.Join(", ", who.OrderByDescending(w => w.Chance).Select(w => $"{w.Name} {Math.Round(w.Chance)}%")),
                zone, points = points.Count,
                x = points.Average(p => p.Position!.Value.X), y = points.Average(p => p.Position!.Value.Y), z = points.Average(p => p.Position!.Value.Z)
            });
        }

        // where PMCs start: the spawn points with an entry point
        var pmc = spawns.Where(p => Has(p.Categories, "Player") && !string.IsNullOrEmpty(p.Infiltration))
            .Select(p => new { name = p.Infiltration, x = p.Position!.Value.X, y = p.Position!.Value.Y, z = p.Position!.Value.Z }).ToList();

        // extracts: each one's place, by name, with who can use it
        var exits = new List<object>();
        var list = location!.AllExtracts?.Any() == true ? location.AllExtracts : info.Exits ?? [];
        foreach (var exit in list.Where(e => !string.IsNullOrEmpty(e.Name)).GroupBy(e => e.Name!))
        {
            var sides = exit.Select(e => e.Side?.ToString()).ToList();
            var pmcUse = sides.Any(s => s is null or "Pmc" or "Coop" or "All");
            var scavUse = sides.Any(s => s is "Scav" or "Coop" or "All");
            foreach (var p in PlacesOn("exit-places.json", exit.Key, map, version).Take(1))
            {
                exits.Add(new { name = locale.GetValueOrDefault(exit.Key) ?? exit.Key, pmc = pmcUse, scav = scavUse, x = p[0], y = p[1], z = p[2] });
            }
        }

        return new { bosses, pmc, exits };
    }
}
