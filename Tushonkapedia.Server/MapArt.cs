using System.Text.Json;
using System.Text.RegularExpressions;
using SPTarkov.Common.Models.Logging;

namespace Tushonkapedia.Server;

// Drawings of the maps, for the loot map: tarkov.dev's maps (CC BY-NC-SA 4.0), an SVG for most and map tiles for a few
// (The Lab, The Labyrinth), and how each one lines up with the game's coordinates (its transform, rotation and
// bounds, from tarkov.dev's maps.json). Fetched the first time they're needed and saved in data/maps, so they're
// there next time offline.
//
// A map is matched by its English name the way tarkov.dev names its pages ("Streets of Tarkov" is streets-of-tarkov),
// so a modded map with a name tarkov.dev doesn't know simply has no drawing, and the page outlines it from its loot.
public sealed partial class MapArt(ISptLogger<WikiRoute> logger, string folder, bool online)
{
    private const string Calibration = "https://raw.githubusercontent.com/the-hideout/tarkov-dev/main/src/data/maps.json";
    private const string CalibrationFile = "maps.json";
    private const string LabDrawing = "https://assets.tarkov.dev/maps/svg/Labs.svg";
    private static readonly Dictionary<string, string> LabFloors = new() { ["Second Level"] = "Second_Level", ["Technical"] = "Technical_Level" };
    private static readonly TimeSpan Refresh = TimeSpan.FromDays(7);
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(30) };

    // A map: its tiles ("…/{z}/{x}/{y}.png", tarkov.dev's top-down picture) and/or its SVG drawing, where each lies in
    // the game's coordinates, and its floors. Bounds is the map's extent; SvgBounds where the SVG is stretched to.
    public sealed record Art(string Key, double[] Transform, double Rotation, double[][] Bounds, double[][] SvgBounds, string? Layer,
        string? Svg, string? Tiles, int TileSize, int MinZoom, int MaxZoom, List<Floor> Floors);

    // An upper floor, basement or the like, drawn over the ground level: its own tiles and/or the SVG group it is, and
    // where it is (heights, and the areas of the map they apply to; none for all of it).
    public sealed record Floor(string Name, string? SvgLayer, string? Tiles, List<Extent> Extents);

    public sealed record Extent(double Low, double High, List<double[]> Areas);

    private readonly SemaphoreSlim _tileGate = new(6);

    private readonly SemaphoreSlim _gate = new(1);
    private Dictionary<string, Art>? _maps;
    private DateTime _tried;

    private string Folder => Path.Combine(folder, "data", "maps");
    // the drawings, tiles (WebP) and layout the mod ships with, so maps are drawn with no internet
    private PackFiles Packed => PackFiles.In(folder);

    // the drawing for a map with this English name, or null
    public async Task<Art?> For(string? name, CancellationToken cancellationToken)
    {
        var maps = await Maps(cancellationToken);
        if (maps == null || string.IsNullOrWhiteSpace(name))
        {
            return null;
        }

        var key = Normalise(name);
        if (maps.TryGetValue(key, out var art))
        {
            return art;
        }

        // "Night Factory" and the like: the longest tarkov.dev name that's a whole part of it
        return maps.Values.Where(a => $"-{key}-".Contains($"-{a.Key}-")).MaxBy(a => a.Key.Length);
    }

    // one tile of a tiled map, from data/maps or tarkov.dev; null when there's none there (the edges have gaps)
    // (floor 0 is the ground level, 1 on the floors in order)
    public async Task<byte[]?> Tile(Art art, int floor, int z, int x, int y, CancellationToken cancellationToken)
    {
        var tiles = floor == 0 ? art.Tiles : floor <= art.Floors.Count ? art.Floors[floor - 1].Tiles : null;
        if (tiles == null || z < art.MinZoom || z > art.MaxZoom || x < 0 || y < 0 || x >= 1 << z + 2 || y >= 1 << z + 2)
        {
            return null;
        }

        var file = Path.Combine(Folder, art.Key, floor.ToString(), z.ToString(), $"{x}-{y}.png");
        // an empty file: tarkov.dev has no tile there
        if (File.Exists(file))
        {
            var saved = await File.ReadAllBytesAsync(file, cancellationToken);
            return saved.Length > 0 ? saved : null;
        }

        // the tiles the mod ships with
        if (Packed.Bytes($"maps/{art.Key}/{floor}/{z}/{x}-{y}.webp") is { } packed)
        {
            return packed.Length > 0 ? packed : null;
        }

        if (!online)
        {
            return null;
        }

        await _tileGate.WaitAsync(cancellationToken);
        try
        {
            using var response = await Http.GetAsync(tiles.Replace("{z}", z.ToString()).Replace("{x}", x.ToString()).Replace("{y}", y.ToString()), cancellationToken);
            var bytes = response.IsSuccessStatusCode ? await response.Content.ReadAsByteArrayAsync(cancellationToken) : [];
            if (response.IsSuccessStatusCode || (int)response.StatusCode is 403 or 404)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(file)!);
                await File.WriteAllBytesAsync(file, bytes, cancellationToken);
            }

            return bytes.Length > 0 ? bytes : null;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !cancellationToken.IsCancellationRequested)
        {
            return null;
        }
        finally
        {
            _tileGate.Release();
        }
    }

    // the map's SVG, from data/maps or tarkov.dev
    public async Task<byte[]?> Svg(Art art, CancellationToken cancellationToken)
    {
        if (art.Svg == null)
        {
            return null;
        }

        var file = Path.Combine(Folder, art.Key + ".svg");
        if (File.Exists(file))
        {
            return await File.ReadAllBytesAsync(file, cancellationToken);
        }

        if (Packed.Bytes($"maps/{art.Key}.svg") is { } packed)
        {
            return packed;
        }

        if (!online)
        {
            return null;
        }

        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (File.Exists(file))
            {
                return await File.ReadAllBytesAsync(file, cancellationToken);
            }

            var bytes = await Http.GetByteArrayAsync(art.Svg, cancellationToken);
            Directory.CreateDirectory(Folder);
            await File.WriteAllBytesAsync(file, bytes, cancellationToken);
            return bytes;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !cancellationToken.IsCancellationRequested)
        {
            logger.Warning($"[Tushonkapedia] Couldn't get the {art.Key} map drawing from tarkov.dev: {ex.GetBaseException().Message}");
            return null;
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<Dictionary<string, Art>?> Maps(CancellationToken cancellationToken)
    {
        if (_maps != null)
        {
            return _maps;
        }

        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (_maps != null)
            {
                return _maps;
            }

            var file = Path.Combine(Folder, CalibrationFile);
            var saved = File.Exists(file) ? Read(await File.ReadAllTextAsync(file, cancellationToken))
                : Packed.Text("maps/" + CalibrationFile) is { } packed ? Read(packed) : null;
            if (!online)
            {
                return _maps = saved;
            }

            // a saved copy is used as it is for a week; after that it's refreshed when tarkov.dev answers
            if (saved != null && DateTime.UtcNow - File.GetLastWriteTimeUtc(file) < Refresh)
            {
                return _maps = saved;
            }

            // offline: don't ask again for a while
            if (DateTime.UtcNow - _tried < TimeSpan.FromMinutes(10))
            {
                return saved;
            }

            _tried = DateTime.UtcNow;
            try
            {
                var text = await Http.GetStringAsync(Calibration, cancellationToken);
                var fresh = Read(text);
                if (fresh is { Count: > 0 })
                {
                    Directory.CreateDirectory(Folder);
                    await File.WriteAllTextAsync(file, text, cancellationToken);
                    logger.Info($"[Tushonkapedia] Map drawings from tarkov.dev: {fresh.Count} maps.");
                    return _maps = fresh;
                }
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !cancellationToken.IsCancellationRequested)
            {
                logger.Warning($"[Tushonkapedia] Couldn't get the map drawings' layout from tarkov.dev ({ex.GetBaseException().Message}); {(saved != null ? "using the saved one" : "maps are drawn from their loot spots")}.");
            }

            return _maps = saved;
        }
        finally
        {
            _gate.Release();
        }
    }

    // tarkov.dev's maps.json: per map, its interactive view, drawn as tiles and/or an SVG, and its floors
    private static Dictionary<string, Art>? Read(string text)
    {
        try
        {
            var maps = new Dictionary<string, Art>();
            using var doc = JsonDocument.Parse(text);
            foreach (var group in doc.RootElement.EnumerateArray())
            {
                if (!group.TryGetProperty("normalizedName", out var name) || !group.TryGetProperty("maps", out var views))
                {
                    continue;
                }

                foreach (var view in views.EnumerateArray())
                {
                    var svg = Safe(Text(view, "svgPath"), false);
                    // tarkov.dev has a drawing of the Lab that its map data no longer points to
                    var lab = svg == null && name.GetString() == "the-lab";
                    svg ??= lab ? LabDrawing : null;
                    var tiles = Safe(Text(view, "tilePath"), true);
                    var bounds = Corners(view, "bounds") ?? Corners(view, "svgBounds");
                    if (view.TryGetProperty("projection", out var projection) && projection.GetString() == "interactive"
                        && (svg != null || tiles != null) && bounds != null
                        && view.TryGetProperty("transform", out var transform) && transform.GetArrayLength() == 4)
                    {
                        var key = name.GetString()!;
                        var floors = new List<Floor>();
                        if (view.TryGetProperty("layers", out var layers) && layers.ValueKind == JsonValueKind.Array)
                        {
                            foreach (var layer in layers.EnumerateArray())
                            {
                                var floorTiles = Safe(Text(layer, "tilePath"), true);
                                var svgLayer = Text(layer, "svgLayer") ?? (lab ? LabFloors.GetValueOrDefault(Text(layer, "name") ?? "") : null);
                                if (Text(layer, "name") is { } floorName && (floorTiles != null || svgLayer != null))
                                {
                                    floors.Add(new Floor(floorName, svgLayer, floorTiles, Extents(layer)));
                                }
                            }
                        }

                        maps[key] = new Art(key,
                            transform.EnumerateArray().Select(t => t.GetDouble()).ToArray(),
                            view.TryGetProperty("coordinateRotation", out var rotation) ? rotation.GetDouble() : 0,
                            bounds, Corners(view, "svgBounds") ?? bounds,
                            Text(view, "svgLayer") ?? (lab ? "First_Level" : null),
                            svg, tiles,
                            view.TryGetProperty("tileSize", out var size) ? size.GetInt32() : 256,
                            view.TryGetProperty("minZoom", out var min) ? min.GetInt32() : 0,
                            Math.Min(8, view.TryGetProperty("maxZoom", out var max) ? max.GetInt32() : 6),
                            floors);
                        break;
                    }
                }
            }

            return maps;
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or FormatException)
        {
            return null;
        }
    }

    // only pictures from tarkov.dev's own asset host; tiles need their {z}, {x} and {y}
    private static string? Safe(string? url, bool tiles) =>
        url != null && Uri.TryCreate(url.Replace("{", "").Replace("}", ""), UriKind.Absolute, out var uri) && uri.Scheme == "https"
        && uri.Host == "assets.tarkov.dev" && (!tiles || (url.Contains("{z}") && url.Contains("{x}") && url.Contains("{y}")))
            ? url
            : null;

    // [[x, z], [x, z]]
    private static double[][]? Corners(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Array && value.GetArrayLength() == 2
            ? value.EnumerateArray().Select(b => b.EnumerateArray().Take(2).Select(c => c.GetDouble()).ToArray()).ToArray()
            : null;

    // a floor's heights, each with the areas they apply to ([[x, z], [x, z], "name"] each; none means the whole map)
    private static List<Extent> Extents(JsonElement layer)
    {
        var extents = new List<Extent>();
        if (!layer.TryGetProperty("extents", out var list) || list.ValueKind != JsonValueKind.Array)
        {
            return extents;
        }

        foreach (var extent in list.EnumerateArray())
        {
            if (!extent.TryGetProperty("height", out var height) || height.GetArrayLength() != 2)
            {
                continue;
            }

            var areas = new List<double[]>();
            if (extent.TryGetProperty("bounds", out var bounds) && bounds.ValueKind == JsonValueKind.Array)
            {
                foreach (var area in bounds.EnumerateArray())
                {
                    var corners = area.EnumerateArray().Where(c => c.ValueKind == JsonValueKind.Array).Take(2).ToList();
                    if (corners.Count == 2)
                    {
                        areas.Add([corners[0][0].GetDouble(), corners[0][1].GetDouble(), corners[1][0].GetDouble(), corners[1][1].GetDouble()]);
                    }
                }
            }

            extents.Add(new Extent(height[0].GetDouble(), height[1].GetDouble(), areas));
        }

        return extents;
    }

    private static string? Text(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static string Normalise(string name) => NonWord().Replace(name.ToLowerInvariant(), "-").Trim('-');

    [GeneratedRegex("[^a-z0-9]+")]
    private static partial Regex NonWord();
}
