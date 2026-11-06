using SPTarkov.Server.Core.Loaders;
using SPTarkov.Server.Core.Models.Spt.Mod;
using SPTarkov.Server.Core.Utils;
using System.Text;
using System.Security.Cryptography;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Http;
using SPTarkov.Common.Models.Logging;
using SPTarkov.DI.Annotations;
using SPTarkov.Server.Core.DI;
using SPTarkov.Server.Core.Helpers.Items;
using SPTarkov.Server.Core.Helpers.Quest;
using SPTarkov.Server.Core.Helpers.Server;
using SPTarkov.Server.Core.Models.Common;
using SPTarkov.Server.Core.Models.Spt.Config;
using SPTarkov.Server.Core.Models.Spt.Tables;
using SPTarkov.Server.Core.Servers;
using SPTarkov.Server.Core.Servers.Http;
using SPTarkov.Server.Core.Services.Locales;
using SPTarkov.Server.Core.Services.Ragfair;
using SPTarkov.Server.Core.Services.Server;

namespace Tushonkapedia.Server;

// Tushonkapedia, the server's wiki: https://<server>:6969/wiki. The page and its script come from the mod's web
// folder; the quest data comes from /wiki/api/graph, worked out from templates.Quests as the server holds it after
// every mod has loaded. It only writes a profile to start a craft or a hideout upgrade the player asked for.
//   /wiki/api/graph?lang=en[&rebuild=1]       the whole graph as JSON
//   /wiki/api/chain?id=<quest id>[&format=json] a quest's prerequisite chain, as text by default
//   /wiki/api/profiles                         profiles on this server (name, level)
//   /wiki/api/profile?id=<profile id>          one profile's quest statuses and trader levels
//   /wiki/api/item?id=<item id>                one item: loot, traders, crafts, quests, hideout
//   /wiki/api/items?q=<text>                   item search
//   /wiki/api/kinds                            every kind of item page, with examples, for testing
//   /wiki/api/where?ids=<id,id,...>            short "where to get it" for several items
//   /wiki/api/lootmap?id=<item id>&map=<map id> where on one map an item spawns, over the map's loot spots
//                   (box=<container types> instead of id: where those containers stand; version=<id>: another version of the map)
//   /wiki/api/boxids                           every map's container ids, for tools/box_places.py
//   /wiki/api/keyids                           every key's id, for tools/box_places.py
//   /wiki/api/zoneids                          every place a quest sends you, for tools/box_places.py
//                   (lootmap with zone=<ids>: where those places are on the map)
//                   (lootmap with door=<key id>: where that key's locks are on the map)
//   /wiki/api/mapsvg?map=<map id>             tarkov.dev's drawing of the map (saved in data/maps)
//   /wiki/api/maptile?map=&floor=&z=&x=&y=     one tile of tarkov.dev's top-down map, floor 0 the ground (saved in data/maps)
//   /wiki/api/flea?id=<item id>                what a buyer gets on the flea (a gun's stock build) and SPT's price for it
//   /wiki/api/catalog                          every item by handbook category
//   /wiki/api/rules?page=<name>                one of the server's rule pages: insurance, flea, repairs, ammo, armor, skills,
//                   levels, health, karma, achievements, prestige, events, circle, containers
//   /wiki/api/trader?id=, api/map?id=, api/maps, api/hideout   the wiki's other pages
//   (everything is also served under /wiki)
//   /wiki/icon/<item id>                       an item's picture (drawn by the game or from tarkov.dev, saved in data/icons)
//   /wiki/api/icons/wanted, POST api/icons/upload   items without a picture, and pictures the game plugin drew
//   /wiki/api/icons/prints                     each item's picture print (what it's drawn from), for tools/build_pack.py
//   /wiki/api/layouts/wanted, POST api/layouts/upload   container layouts not measured yet, and where the game put each part
//   /wiki/api/portrait?id=, POST api/portrait        a player's character, as the game plugin drew it
[Injectable(InjectionType.Singleton, TypePriority = OnLoadOrder.Preload)]
public class WikiRoute(ISptLogger<WikiRoute> logger, TemplateTable templates, LocationTable locations, TradersTable traders,
    HideoutTable hideout, LocationConfig locationConfig, LocaleService locales, ModHelper modHelper, SaveServer saveServer, GlobalTable globals,
    BotTable bots, AirdropConfig airdropConfig, QuestConfig questConfig, QuestHelper questHelper, SeasonalEventService seasons,
    SeasonalEventConfig seasonConfig, RagfairPriceService ragfairPrices, PresetHelper presets, IReadOnlyList<SptMod> loadedMods,
    HideoutActions hideoutActions, InsuranceConfig insuranceConfig, RagfairConfig ragfairConfig, RepairConfig repairConfig,
    TraderConfig traderConfig, HideoutConfig hideoutConfig, ItemConfig itemConfig, LocaleTable localeTable, PmcConfig pmcConfig, BundleLoader bundleLoader,
    SPTarkov.Server.Core.Services.Image.ImageRouterService images)
    : IHttpListener
{
    public const string Root = "/wiki";
    // the mod's own name works as an address too, and sends you to the page
    public const string Alias = "/tushonkapedia";

    private static readonly Dictionary<string, string> ContentTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        [".html"] = "text/html; charset=utf-8",
        // the script is .mjs: SPT refuses to load a mod folder holding any .js file (it takes it for a pre-4.0 mod)
        [".mjs"] = "text/javascript; charset=utf-8",
        [".css"] = "text/css; charset=utf-8",
        [".svg"] = "image/svg+xml",
        [".png"] = "image/png",
        [".ico"] = "image/x-icon",
        [".json"] = "application/json; charset=utf-8",
        // the page's typeface, shipped with the mod so it works offline
        [".woff2"] = "font/woff2"
    };

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private readonly Lock _lock = new();
    private readonly Dictionary<string, Cached> _cache = [];

    // The server's locale with the game's text styling taken out. Mods write names like
    // "<b><color=#ca741f>MP7A2 Endless</color></b>": the game draws those tags, a web page
    // would print them. Cached per language until the server hands back a different table.
    private readonly Lock _plainLock = new();
    private readonly Dictionary<string, (object Source, Dictionary<string, string> Plain)> _plain = [];

    private static readonly System.Text.RegularExpressions.Regex StyleTag = new(
        @"</?(b|i|u|s|color|size|align|sup|sub|mark|font|material|quad|sprite|link|style|voffset|line-height|alpha|cspace|mspace|nobr|noparse|pos|indent|margin|rotate|smallcaps|lowercase|uppercase|allcaps|width|br)(\s*=[^>]*)?\s*/?>",
        System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.Compiled);

    private Dictionary<string, string>? PlainLocale(string lang)
    {
        var raw = locales.GetLocaleDb(lang);
        if (raw == null) return null;
        lock (_plainLock)
        {
            if (_plain.TryGetValue(lang, out var hit) && ReferenceEquals(hit.Source, raw)) return hit.Plain;
            // keys in any case: SPT writes "<id> name", some mods "<id> Name", and the game reads both
            var plain = new Dictionary<string, string>(raw.Count, StringComparer.OrdinalIgnoreCase);
            foreach (var (key, value) in raw)
                plain.TryAdd(key, value != null && value.Contains('<') ? StyleTag.Replace(value, "").Trim() : value!);
            _plain[lang] = (raw, plain);
            return plain;
        }
    }
    private string? _folder;
    private ModConfig? _config;
    private TarkovDevKeys? _keys;
    private ItemIcons? _icons;
    private ItemWiki? _wiki;

    private ItemIcons Icons => _icons ??= new ItemIcons(logger, Folder, Config.UseInternet);
    private BotArt? _botArt;
    private BotArt BotPictures => _botArt ??= new BotArt(logger, Folder, Config.UseInternet);

    // bot-notes.json: a short write-up for each bot, by role; read again when the file changes
    private (DateTime At, Dictionary<string, string> Notes)? _botNotes;

    private Dictionary<string, string> BotNotes()
    {
        var file = Path.Combine(Folder, "bot-notes.json");
        var at = File.Exists(file) ? File.GetLastWriteTimeUtc(file) : DateTime.MinValue;
        if (_botNotes is { } cached && cached.At == at)
        {
            return cached.Notes;
        }

        var notes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            if (File.Exists(file))
            {
                var read = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(file),
                    new JsonSerializerOptions { ReadCommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true }) ?? [];
                foreach (var (role, text) in read)
                {
                    notes[role] = text;
                }
            }
        }
        catch (Exception e)
        {
            logger.Warning($"[Tushonkapedia] bot-notes.json couldn't be read: {e.Message}");
        }

        _botNotes = (at, notes);
        return notes;
    }
    private MapArt? _art;
    private MapArt Art => _art ??= new MapArt(logger, Folder, Config.UseInternet);

    // tarkov.dev's drawing of a map, matched by its English name; null when map drawings are off or there's none
    private async Task<MapArt.Art?> DrawingFor(string map, CancellationToken cancellationToken)
    {
        var name = Config.MapImages && map.Length <= 64 ? Wiki.LocationName(map, PlainLocale("en") ?? []) : null;
        return name == null ? null : await Art.For(name, cancellationToken);
    }

    private ItemWiki Wiki => _wiki ??= Warm(UseBotNames(new ItemWiki(logger, templates, locations, traders, hideout, locationConfig, bots, airdropConfig,
        container => !seasons.ChristmasEventEnabled() && seasonConfig.ChristmasContainerIds.Contains(container), Folder,
        () => globals.Configuration?.Health?.Effects?.Stimulator?.Buffs)
    {
        PmcRoles = () => [pmcConfig.UsecType, pmcConfig.BearType],
        EventRoles = () => EventBotRoles(),
        BundleMod = path => bundleLoader.GetBundle(path)?.ModPath is { Length: > 0 } modPath ? ModFolderOf(modPath) : null,
        Rules =new ItemWiki.RuleSources(globals, insuranceConfig, ragfairConfig, repairConfig, traderConfig, hideoutConfig, seasonConfig, seasons),
        // SPT's own item settings: what it never hands out, and what it keeps out of rewards and airdrops
        ItemSettings = itemConfig,
        // the seasonal items SPT keeps out of loot while their event is off
        OutOfSeason = () => seasons.GetInactiveSeasonalEventItems().Select(x => x.ToString()).ToHashSet()
    }));

    // bot names come from the server's quests, maps and bot settings (see BotRoles)
    private ItemWiki UseBotNames(ItemWiki wiki)
    {
        BotRoles.Use(() => templates.Quests.Values, () => locations.GetDictionary().Values,
            () => bots.Types.ToDictionary(t => t.Key, t => (SPTarkov.Server.Core.Models.Eft.Common.Tables.BotType?)t.Value, StringComparer.OrdinalIgnoreCase));
        return wiki;
    }

    // the armour and ammo charts read every item: worked out in the background so the pages open at once
    private ItemWiki Warm(ItemWiki wiki)
    {
        _ = Task.Run(() =>
        {
            try
            {
                var locale = PlainLocale("en") ?? [];
                wiki.Rule("armor", locale, "en");
                wiki.Rule("ammo", locale, "en");
            }
            catch (Exception ex)
            {
                logger.Warning($"[Tushonkapedia] Couldn't work out the armour and ammo charts: {ex.Message}");
            }
        });
        return wiki;
    }

    public sealed record Cached(int Fingerprint, QuestGraph Graph, byte[] Body);

    private string Folder => _folder ??= modHelper.GetAbsolutePathToModFolder(Assembly.GetExecutingAssembly());

    private ModConfig Config => _config ??= ModConfig.Load(Folder, logger);

    private Locks? _locks;
    private Locks LockStore => _locks ??= new Locks(Folder);

    public string? PackProblem => PackFiles.In(Folder).Problem;

    private bool IsProfile(string id) => MongoId.IsValidMongoId(id) && saveServer.GetProfiles().Any(p => p.Key.ToString() == id);

    // reads config.json and starts getting keys from tarkov.dev
    public void Start()
    {
        lock (_lock)
        {
            if (_keys != null)
            {
                return;
            }

            _keys = new TarkovDevKeys(logger, Folder, Config);
            _keys.Start();
        }
    }

    public bool CanHandle(HttpContext context)
    {
        var path = context.Request.Path.Value ?? "";
        return path.Equals(Root, StringComparison.OrdinalIgnoreCase) || path.StartsWith(Root + "/", StringComparison.OrdinalIgnoreCase)
            || path.Equals(Alias, StringComparison.OrdinalIgnoreCase) || path.StartsWith(Alias + "/", StringComparison.OrdinalIgnoreCase);
    }

    public async Task HandleAsync(MongoId sessionId, HttpContext context, CancellationToken cancellationToken)
    {
        var path = (context.Request.Path.Value ?? "").TrimEnd('/');
        const string root = Root;
        if (path.StartsWith(Alias, StringComparison.OrdinalIgnoreCase))
        {
            context.Response.Redirect(Root + "/" + context.Request.QueryString);
            return;
        }
        var query = context.Request.Query;
        context.Response.Headers.CacheControl = "no-cache";

        try
        {
            if (HttpMethods.IsPost(context.Request.Method) && path.EndsWith("/api/icons/upload", StringComparison.OrdinalIgnoreCase))
            {
                if (!MayUpload(context))
                {
                    context.Response.StatusCode = 403;
                    return;
                }

                IconClaims.Renew(sessionId.ToString());
                await SendJson(context, new { saved = await SaveIcons(context, cancellationToken) }, cancellationToken);
                return;
            }

            if (HttpMethods.IsPost(context.Request.Method) && path.EndsWith("/api/layouts/upload", StringComparison.OrdinalIgnoreCase))
            {
                if (!MayUpload(context))
                {
                    context.Response.StatusCode = 403;
                    return;
                }

                var layoutBody = await ReadBody(context, cancellationToken);
                var measured = layoutBody.Length == 0 ? [] : JsonSerializer.Deserialize<Dictionary<string, List<float[]>>>(layoutBody) ?? [];
                var savedLayouts = Wiki.SaveLayouts(measured);
                if (savedLayouts > 0)
                {
                    logger.Info($"[Tushonkapedia] Saved {savedLayouts} container layouts measured by the game.");
                }

                await SendJson(context, new { saved = savedLayouts }, cancellationToken);
                return;
            }

            if (HttpMethods.IsPost(context.Request.Method) && path.EndsWith("/api/places", StringComparison.OrdinalIgnoreCase))
            {
                if (!MayUpload(context))
                {
                    context.Response.StatusCode = 403;
                    return;
                }

                await SendJson(context, new { saved = await SavePlaces(context, cancellationToken) }, cancellationToken);
                return;
            }

            if (HttpMethods.IsPost(context.Request.Method) && path.EndsWith("/api/portrait", StringComparison.OrdinalIgnoreCase))
            {
                if (!MayUpload(context))
                {
                    context.Response.StatusCode = 403;
                    return;
                }

                await SendJson(context, new { saved = await SavePortrait(sessionId.ToString(), context, cancellationToken) }, cancellationToken);
                return;
            }

            if (HttpMethods.IsPost(context.Request.Method) && path.Contains("/api/lock/", StringComparison.OrdinalIgnoreCase))
            {
                await HideoutAction(context, "lock-" + path[(path.LastIndexOf('/') + 1)..].ToLowerInvariant(), cancellationToken);
                return;
            }

            if (HttpMethods.IsPost(context.Request.Method) && path.Contains("/api/stash/", StringComparison.OrdinalIgnoreCase))
            {
                await HideoutAction(context, "stash-" + path[(path.LastIndexOf('/') + 1)..].ToLowerInvariant(), cancellationToken);
                return;
            }

            if (HttpMethods.IsPost(context.Request.Method) && (path.Contains("/api/hideout/", StringComparison.OrdinalIgnoreCase) || path.Contains("/api/wishlist/", StringComparison.OrdinalIgnoreCase)))
            {
                var verb = path[(path.LastIndexOf('/') + 1)..].ToLowerInvariant();
                await HideoutAction(context, path.Contains("/api/wishlist/", StringComparison.OrdinalIgnoreCase) ? "wish-" + verb : verb, cancellationToken);
                return;
            }

            if (!HttpMethods.IsGet(context.Request.Method) && !HttpMethods.IsHead(context.Request.Method))
            {
                context.Response.StatusCode = 405;
                return;
            }

            if (path.Equals(root, StringComparison.OrdinalIgnoreCase))
            {
                // relative script and style links need the trailing slash
                if (!(context.Request.Path.Value ?? "").EndsWith('/'))
                {
                    context.Response.Redirect(root + "/");
                    return;
                }

                await SendFile(context, "index.html", cancellationToken);
                return;
            }

            var rest = path[(root.Length + 1)..];
            // a character with a password: its own pages need the key its login gave
            var owner = rest.ToLowerInvariant() switch
            {
                "api/profile" => query["id"].ToString(),
                "api/stash" or "api/stash/item" or "api/wishlist" or "api/hideout/me" or "api/hideout/crafts" => query["profile"].ToString(),
                _ => null
            };
            if (owner != null && !LockStore.KeyFits(owner, context.Request.Headers["X-Tushonkapedia-Key"]))
            {
                context.Response.StatusCode = 401;
                await SendJson(context, new { locked = true }, cancellationToken);
                return;
            }

            switch (rest.ToLowerInvariant())
            {
                case "api/graph":
                {
                    var cached = Graph(Lang(query["lang"]), query["rebuild"] == "1");
                    var etag = $"\"{cached.Fingerprint:x8}-{cached.Graph.Lang}-{cached.Graph.BuiltAt.Ticks:x}\"";
                    context.Response.Headers.ETag = etag;
                    if (context.Request.Headers.IfNoneMatch == etag)
                    {
                        context.Response.StatusCode = 304;
                        return;
                    }

                    context.Response.ContentType = "application/json; charset=utf-8";
                    await context.Response.Body.WriteAsync(cached.Body, cancellationToken);
                    return;
                }
                case "api/chain":
                {
                    var graph = Graph(Lang(query["lang"]), false).Graph;
                    var id = query["id"].ToString().Trim();
                    if (query["format"] == "json")
                    {
                        var chain = graph.Chain(id).Where(q => q != id).Select(q => new { id = q, name = graph.ById[q].Name }).OrderBy(q => q.name);
                        await SendJson(context, new { id, quests = chain }, cancellationToken);
                        return;
                    }

                    context.Response.ContentType = "text/plain; charset=utf-8";
                    await context.Response.WriteAsync(graph.ChainText(id), cancellationToken);
                    return;
                }
                case "api/kinds":
                {
                    var lang = Lang(query["lang"]);
                    await SendJson(context, Wiki.Kinds(PlainLocale(lang) ?? [], lang, Graph(lang, false).Graph), cancellationToken);
                    return;
                }
                case "api/item":
                {
                    var lang = Lang(query["lang"]);
                    var item = Wiki.Item(query["id"].ToString().Trim(), Graph(lang, false).Graph, PlainLocale(lang) ?? [], lang)
                        ?? Wiki.ItemKind(query["id"].ToString().Trim(), PlainLocale(lang) ?? []);
                    if (item == null)
                    {
                        context.Response.StatusCode = 404;
                        return;
                    }

                    await SendJson(context, item, cancellationToken);
                    return;
                }
                case "api/items":
                    await SendJson(context, Wiki.Search(query["q"].ToString(), PlainLocale(Lang(query["lang"])) ?? [], Graph(Lang(query["lang"]), false).Graph), cancellationToken);
                    return;
                case "api/where":
                {
                    var lang = Lang(query["lang"]);
                    var ids = query["ids"].ToString().Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                    await SendJson(context, new { lootReady = Wiki.LootReady, items = Wiki.Where(ids, Graph(lang, false).Graph, PlainLocale(lang) ?? []) }, cancellationToken);
                    return;
                }
                case "api/boxids":
                    await SendJson(context, Wiki.BoxIds(), cancellationToken);
                    return;
                case "api/keyids":
                    await SendJson(context, Wiki.KeyIds(), cancellationToken);
                    return;
                case "api/zoneids":
                    // every place a quest sends you, from the quests as the server has them
                    await SendJson(context, Graph(Lang(null), false).Graph.Quests.SelectMany(q => q.Objectives).SelectMany(o => o.Zones).Distinct().ToArray(), cancellationToken);
                    return;
                case "api/lootmap":
                {
                    var map = query["map"].ToString().Trim();
                    var art = await DrawingFor(map, cancellationToken);
                    var lootMap = Wiki.LootMap(query["id"].ToString().Trim(), map, PlainLocale(Lang(query["lang"])) ?? [], query["box"].ToString(), query["version"].ToString() is { Length: > 0 and <= 64 } v ? v : null,
                        art == null ? null : new
                        {
                            transform = art.Transform, rotation = art.Rotation, bounds = art.Bounds, svgBounds = art.SvgBounds, layer = art.Layer,
                            svg = art.Svg == null ? null : "api/mapsvg?map=" + Uri.EscapeDataString(map),
                            tiles = art.Tiles == null ? null : new { url = "api/maptile?map=" + Uri.EscapeDataString(map), size = art.TileSize, min = art.MinZoom, max = art.MaxZoom },
                            floors = art.Floors.Select(f => new { name = f.Name, svg = f.SvgLayer, tiles = f.Tiles != null, extents = f.Extents.Select(e => new { low = e.Low, high = e.High, areas = e.Areas }) })
                        }, query["door"].ToString().Trim(), query["zone"].ToString().Trim());
                    if (lootMap == null)
                    {
                        context.Response.StatusCode = 404;
                        return;
                    }

                    await SendJson(context, lootMap, cancellationToken);
                    return;
                }
                case "api/maptile":
                {
                    var art = await DrawingFor(query["map"].ToString().Trim(), cancellationToken);
                    var floor = int.TryParse(query["floor"], out var f) ? f : 0;
                    var tile = art != null && floor >= 0 && int.TryParse(query["z"], out var z) && int.TryParse(query["x"], out var x) && int.TryParse(query["y"], out var y)
                        ? await Art.Tile(art, floor, z, x, y, cancellationToken)
                        : null;
                    // a tile tarkov.dev doesn't have is empty map, and asked for again on every view: it's cached too
                    context.Response.Headers.CacheControl = "max-age=604800";
                    // no tile here: 204, not 404, since SPT writes an "[UNHANDLED]" error for every 404 and the map's
                    // edges are mostly empty tiles; the page's picture fails to load either way
                    if (tile == null)
                    {
                        context.Response.StatusCode = 204;
                        return;
                    }

                    // the shipped tiles are WebP ("RIFF"), fetched ones PNG
                    context.Response.ContentType = tile.Length > 4 && tile[0] == 'R' && tile[1] == 'I' && tile[2] == 'F' && tile[3] == 'F' ? "image/webp" : "image/png";
                    context.Response.Headers["X-Content-Type-Options"] = "nosniff";
                    await context.Response.Body.WriteAsync(tile, cancellationToken);
                    return;
                }
                case "api/banner":
                {
                    // the launcher's picture of the map, from the pictures SPT (and any mod) has registered
                    var file = BannerFile(query["map"].ToString().Trim());
                    if (file == null || !File.Exists(file))
                    {
                        // none: 204, so SPT doesn't log the miss as an error; the page drops the picture
                        context.Response.Headers.CacheControl = "max-age=86400";
                        context.Response.StatusCode = 204;
                        return;
                    }

                    context.Response.Headers.CacheControl = "max-age=604800";
                    context.Response.ContentType = Path.GetExtension(file).ToLowerInvariant() is ".jpg" or ".jpeg" ? "image/jpeg" : "image/png";
                    await context.Response.SendFileAsync(file, cancellationToken);
                    return;
                }
                case "api/mapsvg":
                {
                    var art = await DrawingFor(query["map"].ToString().Trim(), cancellationToken);
                    var svg = art == null ? null : await Art.Svg(art, cancellationToken);
                    if (svg == null)
                    {
                        context.Response.StatusCode = 404;
                        return;
                    }

                    // a picture only: nothing in it may run or load anything
                    context.Response.ContentType = "image/svg+xml";
                    context.Response.Headers.CacheControl = "max-age=604800";
                    context.Response.Headers["X-Content-Type-Options"] = "nosniff";
                    context.Response.Headers["Content-Security-Policy"] = "default-src 'none'; style-src 'unsafe-inline'";
                    await context.Response.Body.WriteAsync(svg, cancellationToken);
                    return;
                }
                case "api/tunnel/next":
                {
                    if (!MayAct(context, false))
                    {
                        context.Response.StatusCode = 403;
                        return;
                    }

                    await SendJson(context, await hideoutActions.TunnelNext(sessionId.ToString(), cancellationToken) ?? new { }, cancellationToken);
                    return;
                }
                case "api/tunnel/done":
                {
                    hideoutActions.TunnelDone(query["job"].ToString(), query["ok"] == "1", query["why"].ToString() is { Length: > 0 } why ? why : null);
                    await SendJson(context, new { }, cancellationToken);
                    return;
                }
                case "api/stash/item":
                {
                    var options = Config.ShowProfiles ? hideoutActions.ItemOptions(query["profile"].ToString(), query["item"].ToString()) : null;
                    if (options == null)
                    {
                        context.Response.StatusCode = 404;
                        return;
                    }

                    await SendJson(context, new { off = !MayAct(context, false), options }, cancellationToken);
                    return;
                }
                case "api/stash":
                {
                    var stash = Config.ShowProfiles
                        ? hideoutActions.StashView(query["profile"].ToString(), tpl => ragfairPrices.GetFleaPriceForItem(new MongoId(tpl)), Wiki.BestTraderPrice,
                            tpl => PlainLocale(Lang(query["lang"]))?.GetValueOrDefault(tpl + " Name") ?? tpl) : null;
                    if (stash == null)
                    {
                        context.Response.StatusCode = 404;
                        return;
                    }

                    await SendJson(context, stash, cancellationToken);
                    return;
                }
                case "api/flea":
                {
                    var flea = Flea(query["id"].ToString().Trim());
                    if (flea == null)
                    {
                        context.Response.StatusCode = 404;
                        return;
                    }

                    await SendJson(context, flea, cancellationToken);
                    return;
                }
                case "api/catalog":
                {
                    var lang = Lang(query["lang"]);
                    await SendJson(context, Wiki.Catalog(PlainLocale(lang) ?? [], lang, Graph(lang, false).Graph), cancellationToken);
                    return;
                }
                case "api/trader":
                case "api/map":
                case "api/box":
                {
                    var locale = PlainLocale(Lang(query["lang"])) ?? [];
                    var id = query["id"].ToString().Trim();
                    var found = rest.EndsWith("trader") ? Wiki.Trader(id, locale) : rest.EndsWith("box") ? Wiki.Box(id, locale) : Wiki.Map(id, locale, query["version"].ToString() is { Length: > 0 and <= 64 } v ? v : null);
                    if (found == null)
                    {
                        context.Response.StatusCode = 404;
                        return;
                    }

                    await SendJson(context, found, cancellationToken);
                    return;
                }
                case "api/rules":
                {
                    // the armour chart wants to know which plates can be had
                    Wiki.Catalog(PlainLocale("en") ?? [], "en", Graph("en", false).Graph);
                    var lang = Lang(query["lang"]);
                    var rule = Wiki.Rule(query["page"].ToString().Trim(), PlainLocale(lang) ?? [], lang);
                    if (rule == null)
                    {
                        context.Response.StatusCode = 404;
                        return;
                    }

                    await SendJson(context, rule, cancellationToken);
                    return;
                }
                case "api/maps":
                    await SendJson(context, Wiki.Maps(PlainLocale(Lang(query["lang"])) ?? []), cancellationToken);
                    return;
                case "api/lock":
                    await SendJson(context, new { locked = LockStore.IsLocked(query["profile"].ToString()), open = LockStore.KeyFits(query["profile"].ToString(), context.Request.Headers["X-Tushonkapedia-Key"]) }, cancellationToken);
                    return;
                case "api/wishlist":
                {
                    var list = Config.ShowProfiles ? hideoutActions.Wishlist(query["profile"].ToString()) : null;
                    await SendJson(context, list == null ? new { off = true } : new { off = !MayAct(context, false), list }, cancellationToken);
                    return;
                }
                case "api/hideout/me":
                {
                    var state = Config.ShowProfiles && int.TryParse(query["area"], out var area) ? hideoutActions.State(query["profile"].ToString(), area) : null;
                    await SendJson(context, state == null ? new { off = true } : new { off = !MayAct(context, false), state }, cancellationToken);
                    return;
                }
                case "api/botnames":
                {
                    // what each kind of bot is called on this server, for the page's boss lists
                    var locale = PlainLocale(Lang(query["lang"])) ?? [];
                    _ = Wiki;
                    var roles = bots.Types.Keys.Concat(locations.GetDictionary().Values.SelectMany(l => (l?.Base?.BossLocationSpawn ?? [])
                        .SelectMany(b => new[] { b.BossName, b.BossEscortType }.Concat((b.Supports ?? []).Select(x => x?.BossEscortType)))))
                        .Where(r => !string.IsNullOrEmpty(r)).Distinct(StringComparer.OrdinalIgnoreCase);
                    var named = roles.ToDictionary(r => r!, r => BotRoles.Name(r!, locale), StringComparer.OrdinalIgnoreCase);
                    await SendJson(context, named, cancellationToken);
                    return;
                }
                case "api/bots":
                    await SendJson(context, Wiki.Bots(PlainLocale(Lang(query["lang"])) ?? []), cancellationToken);
                    return;
                case "api/bot":
                {
                    var id = query["id"].ToString().Trim();
                    var bot = Wiki.Bot(id, PlainLocale(Lang(query["lang"])) ?? []);
                    if (bot == null)
                    {
                        context.Response.StatusCode = 404;
                        return;
                    }

                    // the bot's write-up from bot-notes.json, by any of its roles
                    var roles = Wiki.Group(id, PlainLocale(Lang(query["lang"])) ?? [])?.Roles ?? [id];
                    var notes = BotNotes();
                    var about = roles.Select(r => notes.GetValueOrDefault(r)).FirstOrDefault(n => n != null);
                    await SendJson(context, new { bot, about }, cancellationToken);
                    return;
                }
                case var pic when pic.StartsWith("botpic/"):
                {
                    // a bot's picture from tarkov.dev (portrait, or poster with ?kind=poster); 404 for bots it hasn't one of
                    var id = pic[7..];
                    var entry = Config.ItemIcons ? Wiki.Group(id, PlainLocale("en") ?? []) : null;
                    var links = entry is { } e ? await BotPictures.For(e.Roles, e.Name, cancellationToken) : null;
                    var picture = links is { } l ? await BotPictures.Get(query["kind"] == "poster" ? l.Poster : l.Portrait, cancellationToken) : null;
                    if (picture == null)
                    {
                        context.Response.StatusCode = 404;
                        return;
                    }

                    context.Response.Headers.CacheControl = "max-age=604800";
                    context.Response.ContentType = picture.ContentType;
                    await context.Response.Body.WriteAsync(picture.Bytes, cancellationToken);
                    return;
                }
                case "api/hideout/crafts":
                {
                    var state = Config.ShowProfiles ? hideoutActions.Crafts(query["profile"].ToString()) : null;
                    await SendJson(context, state == null ? new { off = true } : new { off = !MayAct(context, false), state }, cancellationToken);
                    return;
                }
                case "api/hideout":
                    await SendJson(context, Wiki.Hideout(PlainLocale(Lang(query["lang"])) ?? []), cancellationToken);
                    return;
                case "api/langs":
                    await SendJson(context, new
                    {
                        current = Lang(null),
                        langs = locales.GetServerSupportedLocales().Select(l => new { id = l, name = localeTable.Languages?.GetValueOrDefault(l) ?? NativeName(l) }).OrderBy(l => l.name).ToList()
                    }, cancellationToken);
                    return;
                case "api/profiles":
                    await SendJson(context, Config.ShowProfiles ? Profiles() : [], cancellationToken);
                    return;
                case "api/profile":
                {
                    var profile = Config.ShowProfiles ? Profile(query["id"].ToString()) : null;
                    if (profile == null)
                    {
                        context.Response.StatusCode = 404;
                        return;
                    }

                    await SendJson(context, profile, cancellationToken);
                    return;
                }
                case "api/portrait":
                {
                    var file = Config.ShowProfiles ? PortraitFile(query["id"].ToString()) : null;
                    if (file == null || !File.Exists(file))
                    {
                        context.Response.StatusCode = 404;
                        return;
                    }

                    // the page asks with the picture's date in the address, so a new picture is a new address
                    context.Response.Headers.CacheControl = "max-age=604800";
                    context.Response.Headers.XContentTypeOptions = "nosniff";
                    context.Response.ContentType = "image/png";
                    await context.Response.SendFileAsync(file, cancellationToken);
                    return;
                }
                // nothing to do for a game whose answers would be refused: it doesn't draw or measure for nothing
                case "api/layouts/wanted":
                    await SendJson(context, MayUpload(context, true) ? Wiki.LayoutsWanted() : [], cancellationToken);
                    return;
                // several players in one raid: only the first game to ask records the map
                case "api/places/wanted":
                {
                    var map = query["map"].ToString();
                    await SendJson(context, new { wanted = MayUpload(context, true) && Wiki.PlacesWanted(map) && PlaceClaims.Take(sessionId.ToString(), [map]).Count > 0 }, cancellationToken);
                    return;
                }
                case "api/icons/prints":
                    await SendJson(context, Wiki.IconPrints(), cancellationToken);
                    return;
                case "api/icons/wanted":
                    // several players' games: each draws a share, not all of them the same pictures
                    await SendJson(context, Config.ItemIcons && MayUpload(context, true) ? IconClaims.Take(sessionId.ToString(), Wiki.IconsWanted(Icons)) : [], cancellationToken);
                    return;
                case var icon when icon.StartsWith("icon/"):
                {
                    var id = icon[5..];
                    // the game's own drawing first (the plugin draws a gun with its default parts on); else a gun as
                    // it's sold: tarkov.dev's picture of the default preset
                    var picture = Config.ItemIcons ? await Icons.Drawn(id, cancellationToken) : null;
                    picture ??= Config.ItemIcons && Wiki.IsBaseGame(id) && DefaultPreset(id) is { } preset
                        ? await Icons.Get(preset, true, cancellationToken)
                        : null;
                    picture ??= Config.ItemIcons ? await Icons.Get(id, Wiki.IsBaseGame(id), cancellationToken) : null;
                    var twin = false;
                    if (picture == null && Config.ItemIcons && Wiki.IconTwin(id) is { } other)
                    {
                        picture = await Icons.Get(other, true, cancellationToken);
                        twin = picture != null;
                    }

                    // no picture: 204, not 404 (SPT logs every 404 as an error); the page shows the item's short name
                    if (picture == null)
                    {
                        context.Response.StatusCode = 204;
                        return;
                    }

                    // pictures don't change, so the browser keeps them; a stand-in only briefly, the game may draw the real one
                    context.Response.Headers.CacheControl = twin ? "max-age=600" : "max-age=604800";
                    context.Response.ContentType = picture.ContentType;
                    await context.Response.Body.WriteAsync(picture.Bytes, cancellationToken);
                    return;
                }
                default:
                    await SendFile(context, rest, cancellationToken);
                    return;
            }
        }
        // the other end went away (the game closed while its request waited): nothing to answer, nothing to log
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested || context.RequestAborted.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            logger.Warning($"[Tushonkapedia] {path}: {ex.Message}");
            if (!context.Response.HasStarted)
            {
                context.Response.StatusCode = 500;
                context.Response.ContentType = "text/plain; charset=utf-8";
                await context.Response.WriteAsync($"Tushonkapedia error: {ex.Message}", cancellationToken);
            }
        }
    }

    // What you'd buy on the flea: a gun as its default (stock) build, anything else as one new item (a key with all its
    // uses, armour at full durability: no wear data means new), priced by SPT's own flea. Null if it can't go on the flea.
    private object? Flea(string id)
    {
        if (!MongoId.IsValidMongoId(id) || !templates.Items.TryGetValue(new MongoId(id), out var template)
            || template.Properties?.CanSellOnRagfair == false)
        {
            return null;
        }

        var preset = Wiki.IsWeapon(id) ? presets.GetDefaultPreset(new MongoId(id)) : null;
        var items = preset?.Items is { Count: > 0 } parts
            ? parts.Select(i => new { _id = i.Id.ToString(), _tpl = i.Template.ToString(), parentId = i.ParentId, slotId = i.SlotId }).ToList()
            : [new { _id = "000000000000000000000001", _tpl = id, parentId = (string?)null, slotId = (string?)null }];
        var price = items.Sum(i => ragfairPrices.GetFleaPriceForItem(new MongoId(i._tpl)));
        return new { id, stock = preset != null, items, price = Math.Round(price) };
    }

    private List<string>? _eventRoles;

    // every role the seasonal events' boss spawns and waves name, found by the fields that name a role in them
    private List<string> EventBotRoles()
    {
        if (_eventRoles != null)
        {
            return _eventRoles;
        }

        var found = new List<string>();
        void Walk(System.Text.Json.Nodes.JsonNode? node)
        {
            switch (node)
            {
                case System.Text.Json.Nodes.JsonObject obj:
                    foreach (var (key, value) in obj)
                    {
                        if (key.Equals("BossName", StringComparison.OrdinalIgnoreCase) || key.Equals("BossEscortType", StringComparison.OrdinalIgnoreCase)
                            || key.Equals("WildSpawnType", StringComparison.OrdinalIgnoreCase))
                        {
                            if (value is System.Text.Json.Nodes.JsonValue v && v.ToString() is { Length: > 0 } role)
                            {
                                found.Add(role);
                            }
                        }
                        else
                        {
                            Walk(value);
                        }
                    }

                    break;
                case System.Text.Json.Nodes.JsonArray array:
                    foreach (var item in array)
                    {
                        Walk(item);
                    }

                    break;
            }
        }

        try
        {
            Walk(JsonSerializer.SerializeToNode(seasonConfig.EventBossSpawns));
            Walk(JsonSerializer.SerializeToNode(seasonConfig.EventWaves));
        }
        catch (Exception ex) when (ex is NotSupportedException or JsonException)
        {
        }

        return _eventRoles = found;
    }

    private Dictionary<string, string>? _presets;

    // item id -> the id of its default preset (the gun as the encyclopedia shows it), for items that have one
    private string? DefaultPreset(string id)
    {
        _presets ??= globals.ItemPresets.Values
            .Where(p => p.Encyclopedia != null)
            .GroupBy(p => p.Encyclopedia!.Value.ToString())
            .ToDictionary(g => g.Key, g => g.First().Id.ToString());
        return _presets.GetValueOrDefault(id);
    }

    // Only players on this PC or the home network may send pictures (or anyone, or no one, per config.json).
    // Behind Docker's port mapping every player can look local, so set it to "none" there once the pictures are in.
    // A refusal is logged once per address, not once per request, so a player outside keeps the log quiet.
    private bool MayUpload(HttpContext context, bool quiet = false)
    {
        var from = context.Connection.RemoteIpAddress;
        var allowed = Config.IconUploads.ToLowerInvariant() switch
        {
            "anyone" => true,
            "none" => false,
            _ => from != null && IsLocal(from)
        };
        if (!allowed && !quiet && _refused.TryAdd("upload " + from, true))
        {
            logger.Warning($"[Tushonkapedia] Refused what the game plugin sent from {from} (iconUploads is \"{Config.IconUploads}\" in config.json). Not logged again for this address.");
        }

        return allowed;
    }

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, bool> _refused = new();

    // a game sends its pictures every couple of minutes while drawing; one that stops for 10 minutes (closed, or in a
    // raid) lets its share go. A map's places take a few seconds to send.
    private static readonly Claims IconClaims = new(TimeSpan.FromMinutes(10));
    private static readonly Claims PlaceClaims = new(TimeSpan.FromMinutes(5));

    // Starting a craft or an upgrade: players on this PC or the home network by default (config.json's
    // hideoutActions), and only from the page itself: it sends a header a form on another site can't, so a page
    // elsewhere can't make the browser do it.
    private bool MayAct(HttpContext context, bool write)
    {
        var from = context.Connection.RemoteIpAddress;
        var allowed = Config.ShowProfiles && Config.HideoutActions.ToLowerInvariant() switch
        {
            "anyone" => true,
            "none" => false,
            _ => from != null && IsLocal(from)
        };
        if (write && allowed && context.Request.Headers["X-Tushonkapedia"] != "1")
        {
            allowed = false;
        }

        if (write && !allowed && _refused.TryAdd("act " + from, true))
        {
            logger.Warning($"[Tushonkapedia] Refused a hideout action from {from} (hideoutActions is \"{Config.HideoutActions}\" in config.json). Not logged again for this address.");
        }

        return allowed;
    }

    // POST api/hideout/craft {profile, recipe}, api/hideout/upgrade {profile, area};
    // api/wishlist/add, /group {profile, item, group}, api/wishlist/remove {profile, item}
    private async Task HideoutAction(HttpContext context, string action, CancellationToken cancellationToken)
    {
        if (!MayAct(context, true))
        {
            context.Response.StatusCode = 403;
            return;
        }

        using var reader = new StreamReader(context.Request.Body);
        var body = await reader.ReadToEndAsync(cancellationToken);
        if (body.Length > 4096)
        {
            context.Response.StatusCode = 413;
            return;
        }

        JsonElement request;
        try
        {
            request = JsonDocument.Parse(body).RootElement.Clone();
        }
        catch (JsonException)
        {
            context.Response.StatusCode = 400;
            return;
        }

        string Text(string name) => request.ValueKind == JsonValueKind.Object && request.TryGetProperty(name, out var v) ? v.ToString() : "";
        var profile = Text("profile");
        int.TryParse(Text("area"), out var area);
        // a character with a password needs the key its unlock gave
        if (!action.StartsWith("lock-") && !LockStore.KeyFits(profile, context.Request.Headers["X-Tushonkapedia-Key"]))
        {
            await SendJson(context, new { ok = false, locked = true, why = "This character is locked. Enter its password." }, cancellationToken);
            return;
        }

        object result = action switch
        {
            _ when action.StartsWith("lock-") && !IsProfile(profile) => new { ok = false, why = "No such character." },
            "lock-set" => LockStore.Set(profile, Text("current"), Text("password")),
            "lock-unlock" => LockStore.Unlock(profile, Text("password")),
            "lock-clear" => LockStore.Clear(profile, Text("current")),
            "craft" => await hideoutActions.Craft(profile, Text("recipe")),
            "upgrade" => await hideoutActions.Upgrade(profile, area),
            "stash-sell" => await hideoutActions.Sell(profile, Text("item"), Text("trader"), int.TryParse(Text("count"), out var sellCount) ? sellCount : 0),
            "stash-repair" => await hideoutActions.Repair(profile, Text("item"), Text("trader")),
            // only an item the game knows by name
            "wish-add" or "wish-group" or "wish-remove" when PlainLocale("en")?.ContainsKey(Text("item") + " Name") == true || action == "wish-remove" =>
                await hideoutActions.WishlistChange(profile, action[5..], Text("item"), int.TryParse(Text("group"), out var group) ? group : 4),
            "wish-add" or "wish-group" => new { ok = false, why = "No such item." },
            _ => new { ok = false, why = "Unknown action." }
        };
        await SendJson(context, result, cancellationToken);
    }

    private static bool IsLocal(System.Net.IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6)
        {
            address = address.MapToIPv4();
        }

        if (System.Net.IPAddress.IsLoopback(address))
        {
            return true;
        }

        var b = address.GetAddressBytes();
        return address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork
            ? b[0] == 10 || (b[0] == 172 && b[1] is >= 16 and <= 31) || (b[0] == 192 && b[1] == 168) || (b[0] == 169 && b[1] == 254)
            : address.IsIPv6LinkLocal || address.IsIPv6UniqueLocal;
    }

    // Pictures the game plugin drew: {"<item id>": "<base64 PNG>", ...}. The plugin's requests may come zlib
    // packed, as the game sends them, or plain.
    // portraits/<profile id>.png, for a profile on this server
    private string? PortraitFile(string id)
    {
        return MongoId.IsValidMongoId(id) && saveServer.GetProfiles().Any(p => p.Key.ToString() == id)
            ? Path.Combine(Folder, "portraits", id + ".png")
            : null;
    }

    // The game plugin's picture of the player sending it: their own profile only (the session says whose), and only
    // a PNG of a sane size: at most 512 × 768 and 1.5 MB.
    private async Task<bool> SavePortrait(string id, HttpContext context, CancellationToken cancellationToken)
    {
        var file = PortraitFile(id);
        if (file == null)
        {
            return false;
        }

        var body = await ReadBody(context, cancellationToken);

        if (body.Length == 0)
        {
            return false;
        }

        var png = Convert.FromBase64String(JsonSerializer.Deserialize<Dictionary<string, string>>(body)?.GetValueOrDefault("png") ?? "");
        // the signature, then the IHDR chunk with width and height, big-endian
        var valid = png.Length is > 24 and <= 1_500_000
                    && png.AsSpan(0, 8).SequenceEqual(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A })
                    && png.AsSpan(12, 4).SequenceEqual("IHDR"u8)
                    && System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(16, 4)) is > 0 and <= 512
                    && System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(20, 4)) is > 0 and <= 768;
        if (!valid)
        {
            logger.Warning($"[Tushonkapedia] Refused a portrait from {context.Connection.RemoteIpAddress}: not a PNG of the right size.");
            return false;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        await File.WriteAllBytesAsync(file, png, cancellationToken);
        return true;
    }

    // Where things are on a map, as the game plugin saw them when a raid started:
    // {"map": "<location id>", "boxes": {id: [x,y,z]}, "types": {id: tpl}, "locks": {key: [[x,y,z]]}, "zones": {id: [[x,y,z]]}}
    private async Task<int> SavePlaces(HttpContext context, CancellationToken cancellationToken)
    {
        var body = await ReadBody(context, cancellationToken);

        if (body.Length == 0)
        {
            return 0;
        }

        var read = JsonSerializer.Deserialize<PlacesUpload>(body, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        // only a map the server asked for: what it already has can't be replaced by whatever is sent
        if (read?.Map == null || !Wiki.PlacesWanted(read.Map))
        {
            return 0;
        }

        var saved = Wiki.SavePlaces(read.Map, new ItemWiki.SeenMap { Boxes = read.Boxes ?? [], Types = read.Types ?? [], Locks = read.Locks ?? [], Zones = read.Zones ?? [], Exits = read.Exits ?? [] });
        logger.Info($"[Tushonkapedia] Saw {saved} places on {read.Map} in a raid.");
        return saved;
    }

    private sealed record PlacesUpload(string? Map, Dictionary<string, float[]>? Boxes, Dictionary<string, string>? Types,
        Dictionary<string, List<float[]>>? Locks, Dictionary<string, List<float[]>>? Zones, Dictionary<string, List<float[]>>? Exits);

    // a POST body from the game plugin, unpacked when SPT's request handler zlib-packed it
    // At most MaxBody bytes, packed or unpacked: anything bigger is refused (empty), so a request can't fill memory.
    private const int MaxBody = 16_000_000;

    private static async Task<byte[]> ReadBody(HttpContext context, CancellationToken cancellationToken)
    {
        var body = await ReadAtMost(context.Request.Body, cancellationToken);
        if (body.Length > 1 && body[0] == 0x78)
        {
            try
            {
                await using var zlib = new System.IO.Compression.ZLibStream(new MemoryStream(body), System.IO.Compression.CompressionMode.Decompress);
                body = await ReadAtMost(zlib, cancellationToken);
            }
            catch (InvalidDataException)
            {
                return [];
            }
        }

        return body;
    }

    private static async Task<byte[]> ReadAtMost(Stream stream, CancellationToken cancellationToken)
    {
        using var buffer = new MemoryStream();
        var chunk = new byte[81920];
        int read;
        while ((read = await stream.ReadAsync(chunk, cancellationToken)) > 0)
        {
            if (buffer.Length + read > MaxBody)
            {
                return [];
            }

            buffer.Write(chunk, 0, read);
        }

        return buffer.ToArray();
    }

    // The plugin sends pictures in small batches: one log line a minute at most, with the running total.
    private int _iconsSaved;
    private DateTime _iconsLogged = DateTime.MinValue;

    private void NoteIcons(int saved)
    {
        if (saved == 0)
        {
            return;
        }

        var total = Interlocked.Add(ref _iconsSaved, saved);
        if (DateTime.UtcNow - _iconsLogged < TimeSpan.FromMinutes(1))
        {
            return;
        }

        _iconsLogged = DateTime.UtcNow;
        Interlocked.Exchange(ref _iconsSaved, 0);
        logger.Info($"[Tushonkapedia] Saved {total} item pictures drawn by the game.");
    }

    private async Task<int> SaveIcons(HttpContext context, CancellationToken cancellationToken)
    {
        if (!Config.ItemIcons)
        {
            return 0;
        }

        var body = await ReadBody(context, cancellationToken);

        if (body.Length == 0)
        {
            return 0;
        }

        // an empty picture is an item the game couldn't draw: remembered, so it isn't asked for again
        var pictures = JsonSerializer.Deserialize<Dictionary<string, string>>(body) ?? [];
        var saved = 0;
        var failed = new List<(string, string)>();
        foreach (var (id, base64) in pictures.Take(200))
        {
            var print = Wiki.IconPrint(id);
            if (string.IsNullOrEmpty(base64))
            {
                failed.Add((id, print));
                continue;
            }

            try
            {
                if (Icons.Save(id, Convert.FromBase64String(base64), print))
                {
                    saved++;
                }
            }
            catch (FormatException)
            {
            }
        }

        Icons.MarkFailed(failed);
        NoteIcons(saved);
        return saved;
    }

    // Built once per language and kept until the quest table changes (a mod adding or swapping quests or
    // conditions after startup), new keys arrive, or the page asks for a rebuild.
    public Cached Graph(string lang, bool rebuild)
    {
        Start();
        lock (_lock)
        {
            var userKeys = ModConfig.UserKeys(Folder, logger);
            if (rebuild)
            {
                Wiki.Forget();
            }

            var fingerprint = HashCode.Combine(Fingerprint(), _keys!.Version, KeysFileStamp());
            if (!rebuild && _cache.TryGetValue(lang, out var cached) && cached.Fingerprint == fingerprint)
            {
                return cached;
            }

            var saved = _keys.Current;
            var sources = new GraphSources(
                PlainLocale(lang) ?? [],
                MapIds(),
                traders.ToDictionary(t => t.Key.ToString(), t => (t.Value?.Base?.Avatar, t.Value?.Base?.UnlockedByDefault ?? true, t.Value?.Base?.LoyaltyLevels?.Count ?? 4)),
                saved?.Keys ?? [],
                userKeys,
                saved?.Wiki ?? [],
                tpl => MongoId.IsValidMongoId(tpl) && templates.Items.TryGetValue(new MongoId(tpl), out var item) && item.Properties?.QuestItem == true,
                tpl => MongoId.IsValidMongoId(tpl) ? Wiki.Price(tpl) : 0,
                tpl => MongoId.IsValidMongoId(tpl) ? Wiki.CaseSpace(tpl) : 0,
                BotSpawns(lang),
                Wiki.LocksNear,
                Wiki.QuestMoved,
                Wiki.ZoneMaps);
            var graph = QuestGraphBuilder.Build(templates.Quests.Values, sources, lang, new ModMetadata().Version.ToString());
            graph.Currencies = Wiki.Currencies(sources.Locale);
            graph.CurrencyTags = ItemWiki.CurrencyTags();
            graph.BaseCurrency = ItemWiki.Roubles;
            // the locale's names for body zones and parts ("DeathInfo/ParietalHead" is "Top of the head")
            foreach (var prefix in new[] { "DeathInfo/" })
            {
                foreach (var (key, value) in sources.Locale)
                {
                    if (key.StartsWith(prefix, StringComparison.Ordinal) && key.Length > prefix.Length && !string.IsNullOrWhiteSpace(value))
                    {
                        graph.Words.TryAdd(key[prefix.Length..], value.Trim());
                    }
                }
            }
            foreach (var quest in graph.Quests)
            {
                quest.Mod = Wiki.ModOf(quest.Id, true);
                quest.Changed = quest.Mod == null && templates.Quests.TryGetValue(new MongoId(quest.Id), out var live) && Wiki.QuestChanged(live);
                Offered(quest);
            }
            cached = new Cached(fingerprint, graph, JsonSerializer.SerializeToUtf8Bytes(graph, Json));
            _cache[lang] = cached;
            logger.Info($"[Tushonkapedia] Built quest graph ({lang}): {graph.Quests.Count} quests, {graph.Edges.Count} links, {graph.KeyEntries} key entries.");
            return cached;
        }
    }

    // Cheap check for a changed quest table: which quest, condition and reward objects are there, and the
    // values of the start and fail conditions. Takes well under a millisecond, so it runs on every request.
    private int Fingerprint()
    {
        var hash = new HashCode();
        hash.Add(templates.Quests.Count);
        foreach (var quest in templates.Quests.Values)
        {
            hash.Add(RuntimeHelpers.GetHashCode(quest));
            hash.Add(RuntimeHelpers.GetHashCode(quest.Rewards));
            hash.Add(quest.Rewards?.Values.Sum(r => r?.Count ?? 0));
            var conditions = quest.Conditions;
            if (conditions == null)
            {
                continue;
            }

            hash.Add(conditions.AvailableForFinish?.Count);
            foreach (var list in new[] { conditions.AvailableForStart, conditions.Fail })
            {
                hash.Add(list == null ? 0 : RuntimeHelpers.GetHashCode(list));
                foreach (var condition in list ?? [])
                {
                    hash.Add(condition == null ? 0 : RuntimeHelpers.GetHashCode(condition));
                    hash.Add(condition?.Value);
                    hash.Add(condition?.Status?.Count);
                    hash.Add(condition?.Target?.IsList == true ? condition.Target.List!.Count : condition?.Target?.Item?.GetHashCode());
                }
            }
        }

        return hash.ToHashCode();
    }

    private long KeysFileStamp()
    {
        var file = Path.Combine(Folder, "keys.json");
        return File.Exists(file) ? File.GetLastWriteTimeUtc(file).Ticks : 0;
    }

    private Dictionary<string, string> MapIds()
    {
        var ids = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var location in locations.GetDictionary().Values)
        {
            if (!string.IsNullOrEmpty(location?.Base?.Id))
            {
                ids[location.Base.Id] = location.Base.IdField.ToString();
            }
        }

        return ids;
    }

    // Who SPT offers a quest to, the way it decides: an event quest only while its event runs, some quests only to
    // one side, some only to (or never to) certain game editions.
    private void Offered(QuestNode quest)
    {
        var id = new MongoId(quest.Id);
        if (!questHelper.ShowEventQuestToPlayer(id))
        {
            quest.Hidden = questConfig.EventQuests.TryGetValue(id, out var ev) && !string.IsNullOrEmpty(ev.Name) ? ev.Name : "an event";
        }

        quest.OnlySide = questConfig.BearOnlyQuests.Contains(id) ? "bear" : questConfig.UsecOnlyQuests.Contains(id) ? "usec" : null;
        quest.Editions = questConfig.ProfileWhitelist.TryGetValue(id, out var only) ? only.ToList() : null;
        var never = questConfig.ProfileBlacklist.Where(e => e.Value.Contains(id)).Select(e => e.Key).ToList();
        quest.NotEditions = never.Count > 0 ? never : null;
    }

    // Where each kind of boss, cultist, raider or rogue spawns: the map and its chance a raid (the best of its
    // spawn entries there). From the maps' boss spawn lists, so mods that change them are included.
    private Func<string, List<(string Map, double Chance)>> BotSpawns(string lang)
    {
        var locale = PlainLocale(lang) ?? [];
        var byRole = new Dictionary<string, Dictionary<string, double>>(StringComparer.OrdinalIgnoreCase);
        foreach (var (key, location) in locations.GetDictionary())
        {
            var info = location?.Base;
            if (info?.BossLocationSpawn == null || key.Equals("hideout", StringComparison.OrdinalIgnoreCase) || info.Enabled == false)
            {
                continue;
            }

            var map = locale.GetValueOrDefault(info.IdField + " Name") ?? info.Name ?? key;
            foreach (var spawn in info.BossLocationSpawn)
            {
                var chance = spawn.BossChance ?? 0;
                if (chance <= 0)
                {
                    continue;
                }

                foreach (var role in new[] { spawn.BossName, spawn.BossEscortType }.Concat((spawn.Supports ?? []).Select(s => s.BossEscortType)))
                {
                    if (string.IsNullOrEmpty(role) || role.StartsWith("pmc", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    if (!byRole.TryGetValue(role, out var maps))
                    {
                        byRole[role] = maps = [];
                    }

                    maps[map] = Math.Max(maps.GetValueOrDefault(map), chance);
                }
            }
        }

        return role => byRole.TryGetValue(role, out var maps)
            ? maps.OrderByDescending(m => m.Value).ThenBy(m => m.Key).Select(m => (m.Key, Math.Round(m.Value))).ToList()
            : [];
    }

    // Only the name, level and side: enough to pick one. Profiles are only read.
    private List<object> Profiles()
    {
        var list = new List<object>();
        foreach (var (id, profile) in saveServer.GetProfiles())
        {
            // nothing in the stash: not anyone's character (a Fika headless host's profile, or one whose character
            // isn't made yet), so nothing to show
            var inventory = profile?.CharacterData?.PmcData?.Inventory;
            var stash = inventory?.Stash?.ToString();
            if (stash == null || inventory!.Items?.Any(i => i.ParentId == stash) != true)
            {
                continue;
            }

            var info = profile?.CharacterData?.PmcData?.Info;
            list.Add(new
            {
                id = id.ToString(),
                name = !string.IsNullOrEmpty(info?.Nickname) ? info.Nickname : profile?.ProfileInfo?.Username ?? id.ToString(),
                level = info?.Level,
                side = info?.Side
            });
        }

        return list;
    }

    private static Dictionary<string, int[]> Owned(SPTarkov.Server.Core.Models.Eft.Common.PmcData pmc)
    {
        var owned = new Dictionary<string, int[]>();
        foreach (var item in pmc.Inventory?.Items ?? [])
        {
            if (item == null)
            {
                continue;
            }

            var count = (int)Math.Max(1, item.Upd?.StackObjectsCount ?? 1);
            var tpl = item.Template.ToString();
            if (!owned.TryGetValue(tpl, out var have))
            {
                owned[tpl] = have = [0, 0];
            }

            have[0] += count;
            if (item.Upd?.SpawnedInSession == true)
            {
                have[1] += count;
            }
        }

        return owned;
    }

    private string[] Offered(string id, Dictionary<string, string> quests)
    {
        try
        {
            return questHelper.GetClientQuests(new MongoId(id))
                .Select(q => q.Id.ToString())
                .Where(q => !quests.TryGetValue(q, out var status) || status == "AvailableForStart")
                .ToArray();
        }
        catch (Exception ex)
        {
            logger.Warning($"[Tushonkapedia] Couldn't ask SPT which quests {id} is offered: {ex.Message}");
            return [];
        }
    }

    private object? Profile(string id)
    {
        var profile = saveServer.GetProfiles().FirstOrDefault(p => p.Key.ToString() == id).Value;
        var pmc = profile?.CharacterData?.PmcData;
        if (pmc == null)
        {
            return null;
        }

        var quests = new Dictionary<string, string>();
        foreach (var quest in pmc.Quests ?? [])
        {
            if (quest != null)
            {
                quests[quest.QId.ToString()] = quest.Status.ToString();
            }
        }

        var traderLevels = new Dictionary<string, object>();
        foreach (var (trader, info) in pmc.TradersInfo ?? [])
        {
            traderLevels[trader.ToString()] = new { loyalty = info?.LoyaltyLevel, standing = info?.Standing, salesSum = info?.SalesSum, unlocked = info?.Unlocked };
        }

        return new
        {
            id,
            name = pmc.Info?.Nickname,
            level = pmc.Info?.Level,
            side = pmc.Info?.Side,
            gameVersion = pmc.Info?.GameVersion,
            player = Player(pmc),
            // when the game plugin last drew this character, for the picture's address; null without one
            portrait = PortraitFile(id) is { } portrait && File.Exists(portrait) ? File.GetLastWriteTimeUtc(portrait).Ticks.ToString("x") : null,
            quests,
            // the quests the game would list for this character to start now, as SPT works them out for its quest
            // screen (a quest it has never written to the profile can still be one)
            offered = Offered(id, quests),
            // item -> [how many the character has (stash and kit), how many of those were found in raid]
            owned = Owned(pmc),
            traders = traderLevels,
            // hideout area type -> built level
            hideout = (pmc.Hideout?.Areas ?? []).Where(a => a != null).GroupBy(a => (int)a.Type).ToDictionary(g => g.Key.ToString(), g => g.Max(a => a.Level ?? 0))
        };
    }

    // What the game's character screen shows: level and experience, raid stats, skills and achievements.
    private object Player(SPTarkov.Server.Core.Models.Eft.Common.PmcData pmc)
    {
        var locale = PlainLocale(Lang(null)) ?? [];
        var counters = pmc.Stats?.Eft?.OverallCounters?.Items ?? [];
        long Counter(params string[] key) => counters.Where(c => c.Key != null && c.Key.SetEquals(key)).Sum(c => c.Value ?? 0);

        // the experience table holds what each level adds (the first entry is level 1's 0): this level starts at the
        // sum up to it and the next one adds its own entry
        var table = globals.Configuration.Exp.Level.ExperienceTable;
        var level = pmc.Info?.Level ?? 1;
        var exp = pmc.Info?.Experience ?? 0;
        long from = 0;
        for (var i = 0; i < level && i < table.Length; i++)
        {
            from += table[i].Experience;
        }

        var to = level < table.Length ? from + table[level].Experience : (long?)null;
        var achievements = templates.Achievements.Concat(templates.CustomAchievements ?? []).GroupBy(a => a.Id.ToString()).ToDictionary(g => g.Key, g => g.First());

        return new
        {
            level,
            experience = exp,
            levelFrom = from,
            levelTo = to,
            prestige = pmc.Info?.PrestigeLevel,
            registered = pmc.Info?.RegistrationDate,
            timePlayed = pmc.Stats?.Eft?.TotalInGameTime,
            raids = Counter("Sessions", "Pmc"),
            survived = Counter("ExitStatus", "Survived", "Pmc"),
            runThrough = Counter("ExitStatus", "Runner", "Pmc"),
            killedInRaid = Counter("ExitStatus", "Killed", "Pmc"),
            missing = Counter("ExitStatus", "MissingInAction", "Pmc"),
            left = Counter("ExitStatus", "Left", "Pmc"),
            kills = Counter("Kills"),
            killedPmc = Counter("KilledPmc"),
            killedSavage = Counter("KilledSavage"),
            killedBoss = Counter("KilledBoss"),
            deaths = Counter("Deaths"),
            longestStreak = Counter("LongestWinStreak", "Pmc"),
            currentStreak = Counter("CurrentWinStreak", "Pmc"),
            skills = (pmc.Skills?.Common ?? []).Where(s => s.Progress > 0)
                .Select(s => new { id = s.Id.ToString(), name = locale.GetValueOrDefault(s.Id.ToString()) ?? s.Id.ToString(), level = Math.Floor(s.Progress / 100), progress = Math.Round(s.Progress % 100) })
                .OrderByDescending(s => s.level).ThenBy(s => s.name),
            achievements = (pmc.Achievements ?? [])
                .Select(a => (Id: a.Key.ToString(), At: a.Value, Info: achievements.GetValueOrDefault(a.Key.ToString())))
                .Select(a => new { id = a.Id, name = locale.GetValueOrDefault(a.Id + " name") ?? a.Id, image = a.Info?.ImageUrl, rarity = a.Info?.Rarity, at = a.At })
                .OrderByDescending(a => a.at)
        };
    }

    // where items spawn: the saved copy now, a fresh one in the background
    public void StartLoot() => Wiki.StartLoot(LootFingerprint());

    // file types a mod's code and settings come in; what else a mod keeps is its own business
    private static readonly HashSet<string> SettingsFiles = new(StringComparer.OrdinalIgnoreCase)
        { ".dll", ".json", ".jsonc", ".json5", ".yaml", ".yml", ".toml", ".cfg", ".ini", ".xml" };

    // folders mods write to while the server runs, which never change its loot
    private static readonly HashSet<string> ScratchFolders = new(StringComparer.OrdinalIgnoreCase)
        { "data", "cache", "logs", "log", "temp", "tmp", "backup", "backups" };

    // What the loot tables depend on, in short: SPT's version; each server mod's id, version and when its code or
    // settings files last changed (only the times, never what's in them); SPT's loot settings; and whether Christmas
    // is on. A mod that changes loot gets installed, updated or set up differently, and each of those changes this.
    // the folder name of the loaded server mod that holds this path (the mod's own folder or a file in it)
    private string? ModFolderOf(string path)
    {
        string Full(string p) => Path.GetFullPath(Path.IsPathRooted(p) ? p : Path.Combine(AppContext.BaseDirectory, p))
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var full = Full(path);
        return loadedMods.Select(m => Full(m.Directory))
            .Where(d => full.Equals(d, StringComparison.OrdinalIgnoreCase) || full.StartsWith(d + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(d => d.Length).Select(Path.GetFileName).FirstOrDefault();
    }

    private string LootFingerprint()
    {
        var text = new StringBuilder(ProgramStatics.SPT_VERSION().ToString());
        var own = Path.GetFullPath(Folder).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        foreach (var mod in loadedMods.OrderBy(m => m.ModMetadata.ModGuid, StringComparer.Ordinal))
        {
            var directory = Path.GetFullPath(mod.Directory).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            if (directory.Equals(own, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            text.Append('|').Append(mod.ModMetadata.ModGuid).Append(' ').Append(mod.ModMetadata.Version).Append(' ').Append(LastChanged(directory).Ticks);
        }

        text.Append('|').Append(JsonSerializer.Serialize(new
        {
            locationConfig.LooseLootMultiplier, locationConfig.StaticLootMultiplier, locationConfig.ContainerRandomisationSettings,
            christmas = seasons.ChristmasEventEnabled()
        }));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text.ToString())))[..32];
    }

    private static DateTime LastChanged(string directory)
    {
        var newest = DateTime.MinValue;
        try
        {
            foreach (var file in Directory.EnumerateFiles(directory))
            {
                if (SettingsFiles.Contains(Path.GetExtension(file)) && File.GetLastWriteTimeUtc(file) is var at && at > newest)
                {
                    newest = at;
                }
            }

            foreach (var sub in Directory.EnumerateDirectories(directory))
            {
                if (!ScratchFolders.Contains(Path.GetFileName(sub)) && LastChanged(sub) is var at && at > newest)
                {
                    newest = at;
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }

        return newest;
    }




    // a language the server's own list doesn't name, by its name in itself as .NET knows it from the code ("de" is "Deutsch")
    private static string NativeName(string code)
    {
        try
        {
            var name = System.Globalization.CultureInfo.GetCultureInfo(code).NativeName;
            return string.IsNullOrEmpty(name) || name.StartsWith("Unknown", StringComparison.OrdinalIgnoreCase) ? code : char.ToUpper(name[0]) + name[1..];
        }
        catch (System.Globalization.CultureNotFoundException)
        {
            return code;
        }
    }

    // The language asked for, else the nearest the server has (a client mod can send "kr-en"; that falls to "kr"), else
    // the game's own, else English.
    public string Lang(string? requested)
    {
        var supported = locales.GetServerSupportedLocales();
        var asked = requested?.Trim() ?? "";
        if (asked.Length > 0)
        {
            var exact = supported.FirstOrDefault(l => l.Equals(asked, StringComparison.OrdinalIgnoreCase));
            if (exact != null)
            {
                return exact;
            }

            var dash = asked.IndexOf('-');
            var near = dash > 0 ? supported.FirstOrDefault(l => l.Equals(asked[..dash], StringComparison.OrdinalIgnoreCase)) : null;
            if (near != null)
            {
                return near;
            }
        }

        var game = locales.GetDesiredGameLocale();
        return supported.Contains(game) ? game : "en";
    }

    // The launcher's banner for a map: banner_<map id>, else the same without its last "_part" (factory4_day is
    // banner_factory4), from SPT's own list of pictures it serves, so a mod's registered banner counts too.
    private string? BannerFile(string map)
    {
        for (var name = map; name.Length > 0; name = name.LastIndexOf('_') is > 0 and var cut ? name[..cut] : "")
        {
            var key = ("/files/banners/banner_" + name).ToLowerInvariant();
            if (images.ExistsByKey(key))
            {
                return images.GetByKey(key);
            }
        }

        return null;
    }

    private static Task SendJson(HttpContext context, object body, CancellationToken cancellationToken)
    {
        context.Response.ContentType = "application/json; charset=utf-8";
        return context.Response.WriteAsync(JsonSerializer.Serialize(body, Json), cancellationToken);
    }

    private async Task SendFile(HttpContext context, string relative, CancellationToken cancellationToken)
    {
        var web = Path.Combine(Folder, "web");
        var full = Path.GetFullPath(Path.Combine(web, relative));
        var inside = full.StartsWith(Path.GetFullPath(web) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
        if (!inside || !ContentTypes.TryGetValue(Path.GetExtension(full), out var type) || !File.Exists(full))
        {
            context.Response.StatusCode = 404;
            return;
        }

        context.Response.ContentType = type;
        if (relative == "index.html")
        {
            // the page asks for its style and script with their save times in the address ("?v=..."), so a browser
            // never keeps an old copy after an update
            var html = await File.ReadAllTextAsync(full, cancellationToken);
            foreach (var file in new[] { "tushonkapedia.css", "tushonkapedia.mjs" })
            {
                var stamp = File.GetLastWriteTimeUtc(Path.Combine(web, file)).Ticks.ToString("x");
                html = html.Replace($"\"{file}\"", $"\"{file}?v={stamp}\"");
            }

            await context.Response.WriteAsync(html, cancellationToken);
            return;
        }

        await context.Response.Body.WriteAsync(await File.ReadAllBytesAsync(full, cancellationToken), cancellationToken);
    }
}

// After every mod has loaded: build the graph once so the first page load is quick, and say where the page is.
[Injectable(TypePriority = OnLoadOrder.PostLoad + 1000)]
public class WikiStartup(ISptLogger<WikiStartup> logger, WikiRoute route, HttpConfig http) : IOnLoad
{
    public Task OnLoadAsync(CancellationToken cancellationToken)
    {
        if (route.PackProblem is { } problem)
        {
            logger.Warning($"[Tushonkapedia] Couldn't read pack.zip ({problem}). Pictures and map drawings come from data\\ or tarkov.dev instead; extract the mod's zip again to fix it.");
        }

        try
        {
            route.Start();
            route.Graph(route.Lang(null), true);
            route.StartLoot();
        }
        catch (Exception ex)
        {
            logger.Warning($"[Tushonkapedia] Couldn't build the quest graph yet: {ex.Message}");
        }

        // a newer or older SPT may have moved the request log: then the wiki's requests are just logged as before
        try
        {
            new QuietRequests().Enable();
        }
        catch (Exception ex)
        {
            logger.Debug($"[Tushonkapedia] Couldn't quiet the wiki's request lines: {ex.Message}");
        }

        logger.Success($"[Tushonkapedia] Open it at https://127.0.0.1:{http.Port}{WikiRoute.Root}/");
        return Task.CompletedTask;
    }
}
