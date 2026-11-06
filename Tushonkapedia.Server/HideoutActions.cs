using SPTarkov.Common.Models.Logging;
using SPTarkov.DI.Annotations;
using SPTarkov.Server.Core.Controllers;
using SPTarkov.Server.Core.DI;
using SPTarkov.Server.Core.Helpers.Items;
using SPTarkov.Server.Core.Helpers.Traders;
using SPTarkov.Server.Core.Helpers.Commerce;
using SPTarkov.Server.Core.Models.Eft.Repair;
using SPTarkov.Server.Core.Models.Eft.Trade;
using SPTarkov.Server.Core.Models.Spt.Config;
using SPTarkov.Server.Core.Helpers.Profile;
using SPTarkov.Server.Core.Models.Common;
using SPTarkov.Server.Core.Models.Eft.Common;
using SPTarkov.Server.Core.Models.Eft.Common.Tables;
using SPTarkov.Server.Core.Models.Eft.Hideout;
using SPTarkov.Server.Core.Models.Eft.Inventory;
using SPTarkov.Server.Core.Models.Eft.Wishlist;
using SPTarkov.Server.Core.Models.Enums.Hideout;
using SPTarkov.Server.Core.Models.Spt.Tables;
using SPTarkov.Server.Core.Routers;
using SPTarkov.Server.Core.Servers;
using SPTarkov.Server.Core.Servers.Ws;
using SPTarkov.Server.Core.Services.Profile;
using System.Collections.Concurrent;
using System.Text.Json;
using System.Threading.Channels;

namespace Tushonkapedia.Server;

// Starting a craft or a hideout upgrade from the page, for a character whose game is closed. The game checks what
// a craft or an upgrade needs before it asks the server, and the server trusts it; here the page asks, so every
// check the game makes is made first: the area's level, the area being free, the recipe unlocked, the quest done,
// the trader levels and skills, and the items, from the stash only, found in raid where it says so. Then SPT's own
// handlers do the work, the same ones the game calls, so a mod that changes them works as it does in game. The game picks it all up when it next logs in.
//
// Never behind the back of a running game: it holds its own copy of the hideout and would overwrite what the page did.
// With the game plugin there, the action goes through the game instead (see the tunnel below), the way pressing the
// button in the game would; without it, the page waits for the game to close.
[Injectable(InjectionType.Singleton)]
public class HideoutActions(ISptLogger<HideoutActions> logger, HideoutTable hideout, HideoutController controller, ProfileHelper profiles, InventoryHelper inventory,
    TradeController trading, RepairController repairing, TraderHelper traderHelper, HandbookHelper handbook, ItemHelper itemHelper, RepairConfig repairConfig,
    SaveServer saveServer, PaymentHelper payments, WishlistController wishlist, EventOutputHolder outputs, SptWebSocketConnectionHandler sockets, ProfileActivityService activity)
{
    private readonly Lock _lock = new();

    // ---------- the tunnel ----------
    // While the game is open, its plugin keeps one request waiting here (the long poll), in the menus only. An action from
    // the page goes down it as a job; the plugin does it with the game's own code and says when it has, and the result
    // is then checked in the server's copy of the profile, which the game's own request has just changed.

    private sealed record TunnelJob(string Id, object Payload, TaskCompletionSource<(bool Ok, string? Why)> Done)
    {
        // the page has given up on it: it must never run later, after the page said it failed
        public volatile bool Abandoned;
    }

    private readonly ConcurrentDictionary<string, Channel<TunnelJob>> _jobs = new();
    private readonly ConcurrentDictionary<string, TunnelJob> _sent = new();
    private readonly ConcurrentDictionary<string, int> _listening = new();
    private readonly ConcurrentDictionary<string, DateTime> _heard = new();

    // how long the plugin's request waits for a job, and how long the page waits for the game
    private static readonly TimeSpan PollWait = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan JobWait = TimeSpan.FromSeconds(15);
    // the plugin is back with its next request well within this
    private static readonly TimeSpan PollGap = TimeSpan.FromSeconds(4);
    private static readonly TimeSpan ConfirmWait = TimeSpan.FromSeconds(5);

    private Channel<TunnelJob> Jobs(string id) => _jobs.GetOrAdd(id, _ => Channel.CreateUnbounded<TunnelJob>());

    // whether the game's plugin is there to take an action for this character
    public bool GameListening(string id) =>
        _listening.GetValueOrDefault(id) > 0 || (_heard.TryGetValue(id, out var at) && DateTime.UtcNow - at < PollGap);

    // the plugin's request: the next job for this character, or null after a while with none
    public async Task<object?> TunnelNext(string id, CancellationToken cancellationToken)
    {
        _listening.AddOrUpdate(id, 1, (_, n) => n + 1);
        using var wait = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        wait.CancelAfter(PollWait);
        try
        {
            while (true)
            {
                var job = await Jobs(id).Reader.ReadAsync(wait.Token);
                if (!job.Abandoned)
                {
                    _sent[job.Id] = job;
                    return job.Payload;
                }
            }
        }
        catch (OperationCanceledException)
        {
            return null;
        }
        finally
        {
            _listening.AddOrUpdate(id, 0, (_, n) => Math.Max(0, n - 1));
            _heard[id] = DateTime.UtcNow;
        }
    }

    // the plugin's answer
    public void TunnelDone(string job, bool ok, string? why)
    {
        if (_sent.TryRemove(job, out var sent))
        {
            sent.Done.TrySetResult((ok, why));
        }
    }

    // Has the game do something, then waits until the server's copy of the profile shows it happened.
    private async Task<object> ViaGame(string id, string kind, object fields, Func<bool> happened)
    {
        var jobId = new MongoId().ToString();
        var job = new TunnelJob(jobId, new Dictionary<string, object> { ["job"] = jobId, ["kind"] = kind, ["args"] = fields },
            new TaskCompletionSource<(bool, string?)>(TaskCreationOptions.RunContinuationsAsynchronously));
        await Jobs(id).Writer.WriteAsync(job);
        var answer = await Task.WhenAny(job.Done.Task, Task.Delay(JobWait));
        _sent.TryRemove(jobId, out _);
        if (answer != job.Done.Task)
        {
            job.Abandoned = true;
            return new { ok = false, why = "The game didn't answer. It might be loading or in a raid." };
        }

        var (ok, why) = await job.Done.Task;
        if (!ok)
        {
            return new { ok = false, why = why ?? "The game said no." };
        }

        for (var left = ConfirmWait; left > TimeSpan.Zero; left -= TimeSpan.FromMilliseconds(200))
        {
            if (happened())
            {
                return new { ok = true, viaGame = true };
            }

            await Task.Delay(200);
        }

        return new { ok = false, why = "The game tried, but the server didn't get the change. Check it in the game." };
    }

    // Why the page can't touch this character now, with the game's plugin counted in: null when it can, one way or the other.
    private string? Blocked(MongoId id) => Busy(id) is { } busy && !GameListening(id.ToString()) ? busy : null;

    // Why the page can't touch this character's hideout now, or null when it can.
    public string? Busy(MongoId id)
    {
        if (sockets.IsWebSocketConnected(id))
        {
            return "The game is running for this character. Close the game first: it keeps its own copy of the hideout and would overwrite the change.";
        }

        // the socket opens a little after login, and drops a little before the last save
        return activity.ActiveWithinLastMinutes(id, 1)
            ? "The game was talking to the server under a minute ago. Wait a minute after closing it, then try again."
            : null;
    }

    // ---------- what the character has ----------

    // Items in the stash, outside guns and gear (a round in a magazine or a part on a gun isn't free to use), with
    // how many each stack holds.
    private static List<(Item Item, int Count)> Stash(PmcData pmc)
    {
        var items = pmc.Inventory?.Items ?? [];
        var stash = pmc.Inventory?.Stash;
        var byId = items.Where(i => i != null).ToDictionary(i => i.Id);
        bool Free(Item item)
        {
            for (var at = item; at != null;)
            {
                if (at.SlotId is "cartridges" or "patron_in_weapon" || at.SlotId?.StartsWith("mod_") == true)
                {
                    return false;
                }

                if (at.ParentId == stash?.ToString())
                {
                    return true;
                }

                at = at.ParentId != null && MongoId.IsValidMongoId(at.ParentId) && byId.TryGetValue(new MongoId(at.ParentId), out var parent) ? parent : null;
            }

            return false;
        }

        return items.Where(i => i != null && Free(i)).Select(i => (i, (int)Math.Max(1, i.Upd?.StackObjectsCount ?? 1))).ToList();
    }

    // Stacks to take `count` of an item from, found-in-raid ones only when it must be; the smallest stacks first
    // so the big ones stay whole. Null when there aren't enough.
    private static List<(Item Item, int Take)>? Pick(List<(Item Item, int Count)> stash, string tpl, int count, bool fir, HashSet<MongoId> used)
    {
        var picked = new List<(Item, int)>();
        foreach (var (item, have) in stash.Where(s => s.Item.Template.ToString() == tpl && !used.Contains(s.Item.Id) && (!fir || s.Item.Upd?.SpawnedInSession == true)).OrderBy(s => s.Count))
        {
            if (count <= 0)
            {
                break;
            }

            var take = Math.Min(have, count);
            picked.Add((item, take));
            count -= take;
        }

        if (count > 0)
        {
            return null;
        }

        foreach (var (item, _) in picked)
        {
            used.Add(item.Id);
        }

        return picked;
    }

    private static int Have(List<(Item Item, int Count)> stash, string tpl, bool fir) =>
        stash.Where(s => s.Item.Template.ToString() == tpl && (!fir || s.Item.Upd?.SpawnedInSession == true)).Sum(s => s.Count);

    private static int AreaLevel(PmcData pmc, int type) =>
        (pmc.Hideout?.Areas ?? []).Where(a => a != null && (int)a.Type == type).Select(a => a.Level ?? 0).DefaultIfEmpty(0).Max();

    private static bool QuestDone(PmcData pmc, MongoId? quest) =>
        quest == null || (pmc.Quests ?? []).Any(q => q != null && q.QId == quest && q.Status.ToString() == "Success");

    // How far a craft really is and when it'll be done: progress only counts up at full speed while the area has power, so
    // the time it started says little. Worked out the way the server's own hideout tick does it, from the last tick on.
    private object Timing(PmcData pmc, HideoutProduction recipe, Production production)
    {
        var powered = (pmc.Hideout?.Areas ?? []).FirstOrDefault(a => a?.Type == HideoutAreas.Generator)?.Active == true;
        var needsPower = hideout.Areas?.FirstOrDefault(a => a.Type == recipe.AreaType)?.NeedsFuel == true;
        var speed = needsPower && !powered ? hideout.Settings?.GeneratorSpeedWithoutFuel ?? 1 : 1;
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var seconds = production.ProductionTime ?? 0;
        var progress = Math.Min(seconds, (production.Progress ?? 0) + Math.Max(0, now - (pmc.Hideout?.SptUpdateLastRunTimestamp ?? now)) * speed);
        return new { until = now + (long)Math.Ceiling((seconds - progress) / Math.Max(speed, 0.0001)), percent = seconds > 0 ? Math.Floor(progress * 100 / seconds) : 100, speed };
    }

    // What the character holds in their stash, in the shape the page draws it: every item with where it sits, what it's
    // worth and what's fitted to it, and any flea offers they have up. Read only.
    public object? StashView(string id, Func<string, double> flea, Func<string, double> trader, Func<string, string> name)
    {
        if (!MongoId.IsValidMongoId(id) || profiles.GetPmcProfile(new MongoId(id)) is not { } pmc || pmc.Inventory?.Items is not { } items)
        {
            return null;
        }

        var kids = items.Where(i => i.ParentId != null).ToLookup(i => i.ParentId!);
        var root = pmc.Inventory.Stash?.ToString();
        var rows = new List<object>();
        double total = 0, traders = 0, roubles = 0;
        // what an item and everything fitted to it is worth
        (double Flea, double Trader) Worth(Item item)
        {
            var count = item.Upd?.StackObjectsCount ?? 1;
            var tpl = item.Template.ToString();
            // money is worth what it says, whatever the flea thinks
            var face = payments.IsMoneyTpl(item.Template) ? handbook.GetTemplatePrice(item.Template) * count : (double?)null;
            var (f, t) = (face ?? flea(tpl) * count, face ?? trader(tpl) * count);
            foreach (var part in kids[item.Id.ToString()].Where(k => Place(k) == null))
            {
                var (pf, pt) = Worth(part);
                f += pf;
                t += pt;
            }

            return (f, t);
        }

        void Walk(string parent, int depth)
        {
            foreach (var item in kids[parent])
            {
                if (Place(item) is not { } at)
                {
                    continue;
                }

                var tpl = item.Template.ToString();
                var (width, height) = inventory.GetItemSize(item.Template, item.Id, items);
                var (f, t) = Worth(item);
                var money = tpl == ItemWiki.Roubles;
                if (money)
                {
                    roubles += item.Upd?.StackObjectsCount ?? 1;
                }

                total += f;
                traders += t;
                rows.Add(new
                {
                    id = item.Id.ToString(), tpl, parent, grid = item.SlotId, x = at.X, y = at.Y, turned = at.Turned, w = at.Turned ? height : width, h = at.Turned ? width : height,
                    count = item.Upd?.StackObjectsCount ?? 1, fir = item.Upd?.SpawnedInSession == true, flea = Math.Round(f), trader = Math.Round(t),
                    holds = kids[item.Id.ToString()].Count(k => Place(k) != null)
                });
                Walk(item.Id.ToString(), depth + 1);
            }
        }

        if (root != null)
        {
            Walk(root, 0);
        }

        var stash = items.FirstOrDefault(i => i.Id.ToString() == root);
        return new
        {
            names = items.Select(i => i.Template.ToString()).Distinct().ToDictionary(t => t, name),
            stash = stash?.Template.ToString(), root, items = rows, total = Math.Round(total), traders = Math.Round(traders), roubles,
            offers = (pmc.RagfairInfo?.Offers ?? []).Select(o => new
            {
                id = o.Id.ToString(), tpl = o.Items?.FirstOrDefault()?.Template.ToString(), count = o.Items?.FirstOrDefault()?.Upd?.StackObjectsCount ?? 1,
                price = o.RequirementsCost, ends = o.EndTime, whole = o.SellInOnePiece
            })
        };
    }

    // ---------- selling and repairing ----------

    // an item in the stash and everything fitted to it, or null
    private static (Item Root, List<Item> Tree)? StashItem(PmcData pmc, string item)
    {
        var items = pmc.Inventory?.Items ?? [];
        if (items.FirstOrDefault(i => i.Id.ToString() == item) is not { } root)
        {
            return null;
        }

        var tree = new List<Item> { root };
        for (var at = 0; at < tree.Count; at++)
        {
            tree.AddRange(items.Where(i => i.ParentId == tree[at].Id.ToString()));
        }

        return (root, tree);
    }

    // how worn it is: the share of its value left
    private static double Condition(Item item, Dictionary<string, double> full)
    {
        if (item.Upd?.Repairable is { } worn && worn.MaxDurability > 0)
        {
            return Math.Clamp((worn.Durability ?? 0) / (full.GetValueOrDefault("durability", worn.MaxDurability ?? 1)), 0, 1);
        }

        return item.Upd?.MedKit?.HpResource is { } hp && full.GetValueOrDefault("hp") > 0 ? Math.Clamp(hp / full["hp"], 0, 1) : 1;
    }

    private bool TraderBuys(TraderBase trader, MongoId template)
    {
        var buys = trader.ItemsBuy;
        var never = trader.ItemsBuyProhibited;
        if (buys == null || never?.IdList?.Contains(template) == true || (never?.Category is { Count: > 0 } no && itemHelper.IsOfBaseclasses(template, no)))
        {
            return false;
        }

        return buys.IdList?.Contains(template) == true || (buys.Category is { Count: > 0 } yes && itemHelper.IsOfBaseclasses(template, yes));
    }

    // What each unlocked trader pays for an item as it stands, in their own money, and what each would charge to repair it.
    public object? ItemOptions(string id, string item)
    {
        if (!MongoId.IsValidMongoId(id) || profiles.GetPmcProfile(new MongoId(id)) is not { } pmc || StashItem(pmc, item) is not { } found)
        {
            return null;
        }

        var mongo = new MongoId(id);
        var (root, tree) = found;
        var count = root.Upd?.StackObjectsCount ?? 1;
        var money = payments.IsMoneyTpl(root.Template);
        var holds = (pmc.Inventory?.Items ?? []).Any(i => i.ParentId == root.Id.ToString() && Place(i) != null);
        var value = tree.Sum(i =>
        {
            var props = hideoutItems(i.Template);
            return handbook.GetTemplatePrice(i.Template) * (i.Upd?.StackObjectsCount ?? 1) * Condition(i, props);
        });
        var sell = new List<object>();
        var repair = new List<object>();
        var worn = root.Upd?.Repairable;
        var cost = itemHelper.GetItem(root.Template).Value?.Properties?.RepairCost ?? 0;
        var points = worn == null ? 0 : (worn.MaxDurability ?? 0) - (worn.Durability ?? 0);
        foreach (var (key, info) in pmc.TradersInfo ?? [])
        {
            var trader = traderHelper.GetTrader(key, mongo);
            if (trader == null || info?.Unlocked != true || trader.ItemsBuy == null)
            {
                continue;
            }

            var level = traderHelper.GetLoyaltyLevel(key, pmc);
            var currency = ItemWiki.CurrencyOf(trader.Currency);
            if (!money && !holds && TraderBuys(trader, root.Template) && (level?.BuyPriceCoefficient ?? 100) < 100)
            {
                var rate = Math.Max(1, handbook.GetTemplatePrice(new MongoId(currency)));
                sell.Add(new { trader = key.ToString(), name = trader.Nickname, currency, price = (int)Math.Floor(value * (100 - (level?.BuyPriceCoefficient ?? 0)) / 100 / rate) });
            }

            if (points >= 1 && trader.Repair?.Availability == true)
            {
                var coef = level?.RepairPriceCoefficient ?? 0;
                repair.Add(new { trader = key.ToString(), name = trader.Nickname, cost = Math.Round(cost * points * (coef <= 0 ? 1 : coef / 100 + 1) * repairConfig.PriceMultiplier), quality = trader.Repair.Quality });
            }
        }

        return new
        {
            busy = Blocked(mongo), viaGame = GameListening(id), count, money, holds, sell, repair,
            durability = worn == null ? null : new { now = Math.Round(worn.Durability ?? 0, 1), max = Math.Round(worn.MaxDurability ?? 0, 1), points = Math.Round(points, 1) }
        };
    }

    // the item's full durability and uses, from its template
    private Dictionary<string, double> hideoutItems(MongoId template)
    {
        var props = itemHelper.GetItem(template).Value?.Properties;
        return new Dictionary<string, double> { ["durability"] = props?.MaxDurability ?? 0, ["hp"] = props?.MaxHpResource ?? 0 }.Where(p => p.Value > 0).ToDictionary(p => p.Key, p => p.Value);
    }

    // Sells the stack, or only `take` of it when that's fewer.
    public async Task<object> Sell(string id, string item, string traderId, int take)
    {
        if (!MongoId.IsValidMongoId(id) || !MongoId.IsValidMongoId(traderId) || profiles.GetPmcProfile(new MongoId(id)) is not { } pmc || StashItem(pmc, item) is not { } found)
        {
            return new { ok = false, why = "That item isn't in the stash any more." };
        }

        var mongo = new MongoId(id);
        // the price comes from the server's own sums, never from the page
        var options = JsonSerializer.SerializeToNode(ItemOptions(id, item));
        var offer = (options?["sell"] as System.Text.Json.Nodes.JsonArray ?? []).FirstOrDefault(o => o?["trader"]?.ToString() == traderId);
        if (offer == null)
        {
            return new { ok = false, why = "That trader won't buy this." };
        }

        var stack = found.Root.Upd?.StackObjectsCount ?? 1;
        var count = take > 0 && take < stack ? take : stack;
        // the offer is for the whole stack; part of it is paid in proportion
        var pay = (int)Math.Floor(offer["price"]!.GetValue<int>() * count / stack);
        lock (_lock)
        {
            if (Blocked(mongo) is { } busy)
            {
                return new { ok = false, why = busy };
            }

            // looked at again under the lock: a second click (or another tab) may have sold it since
            if (StashItem(pmc, item) is not { } now || (now.Root.Upd?.StackObjectsCount ?? 1) != stack)
            {
                return new { ok = false, why = "That item changed in the stash. Look again." };
            }

            if (Busy(mongo) != null && count < stack)
            {
                return new { ok = false, why = "With the game open, only whole stacks can be sold from here. Sell the whole stack, or close the game first." };
            }

            if (Busy(mongo) == null)
            {
                // SPT sells the whole item it's given, so part of a stack is split off first, the way the game's own
                // split does, and that part is sold
                var sold = found.Root;
                if (count < stack)
                {
                    var upd = JsonSerializer.Deserialize<Upd>(JsonSerializer.Serialize(found.Root.Upd))!;
                    upd.StackObjectsCount = count;
                    found.Root.Upd!.StackObjectsCount = stack - count;
                    sold = new Item { Id = new MongoId(), Template = found.Root.Template, ParentId = found.Root.ParentId, SlotId = found.Root.SlotId, Location = found.Root.Location, Upd = upd };
                    pmc.Inventory!.Items!.Add(sold);
                }

                trading.ConfirmTrading(pmc, new ProcessSellTradeRequestData
                {
                    Action = "TradingConfirm", Type = "sell_to_trader", TransactionId = new MongoId(traderId), Price = pay,
                    Items = [new SoldItem { Id = sold.Id, Count = (int)count, SchemeId = 0 }]
                }, mongo);
            }
        }

        if (Busy(mongo) != null)
        {
            logger.Info($"[Tushonkapedia] Asking the game to sell {item} for {pmc.Info?.Nickname}.");
            return await ViaGame(id, "sell", new { item, trader = traderId, count = (int)count, price = pay }, () => StashItem(pmc, item) is not { } left || (left.Root.Upd?.StackObjectsCount ?? 1) <= stack - count);
        }

        await saveServer.SaveProfileAsync(mongo);
        logger.Info($"[Tushonkapedia] Sold {count} of {item} for {pmc.Info?.Nickname} from the page.");
        return new { ok = true };
    }

    public async Task<object> Repair(string id, string item, string traderId)
    {
        if (!MongoId.IsValidMongoId(id) || !MongoId.IsValidMongoId(traderId) || profiles.GetPmcProfile(new MongoId(id)) is not { } pmc || StashItem(pmc, item) is not { } found)
        {
            return new { ok = false, why = "That item isn't in the stash any more." };
        }

        var mongo = new MongoId(id);
        var options = JsonSerializer.SerializeToNode(ItemOptions(id, item));
        var offer = (options?["repair"] as System.Text.Json.Nodes.JsonArray ?? []).FirstOrDefault(o => o?["trader"]?.ToString() == traderId);
        if (offer == null || found.Root.Upd?.Repairable is not { } worn)
        {
            return new { ok = false, why = "That trader can't repair this." };
        }

        var points = (worn.MaxDurability ?? 0) - (worn.Durability ?? 0);
        var before = worn.Durability ?? 0;
        var cost = offer["cost"]!.GetValue<double>();
        lock (_lock)
        {
            if (Blocked(mongo) is { } busy)
            {
                return new { ok = false, why = busy };
            }

            // looked at again under the lock: a second click (or another tab) may have repaired it since
            if ((StashItem(pmc, item)?.Root.Upd?.Repairable?.Durability ?? -1) != before)
            {
                return new { ok = false, why = "That item changed in the stash. Look again." };
            }

            if (Have(Stash(pmc), ItemWiki.Roubles, false) < cost)
            {
                return new { ok = false, why = "You can't afford that." };
            }

            if (Busy(mongo) == null)
            {
                repairing.TraderRepair(mongo, new TraderRepairActionDataRequest
                {
                    Action = "TraderRepair", TraderId = new MongoId(traderId), RepairItems = [new RepairItem { Id = found.Root.Id, Count = points }]
                }, pmc);
            }
        }

        if (Busy(mongo) != null)
        {
            logger.Info($"[Tushonkapedia] Asking the game to repair {item} for {pmc.Info?.Nickname}.");
            return await ViaGame(id, "repair", new { item, trader = traderId, points }, () => (StashItem(pmc, item)?.Root.Upd?.Repairable?.Durability ?? 0) > before);
        }

        await saveServer.SaveProfileAsync(mongo);
        logger.Info($"[Tushonkapedia] Repaired {item} for {pmc.Info?.Nickname} from the page.");
        return new { ok = true };
    }

    // where an item sits in a grid; null for something fitted to another item, or a round in a magazine
    private static (int X, int Y, bool Turned)? Place(Item item)
    {
        if (item.Location is ItemLocation at)
        {
            return (at.X ?? 0, at.Y ?? 0, at.R == ItemRotation.Vertical);
        }

        if (item.Location is System.Text.Json.JsonElement { ValueKind: System.Text.Json.JsonValueKind.Object } json)
        {
            var turned = json.TryGetProperty("r", out var r) && (r.ValueKind == System.Text.Json.JsonValueKind.String ? r.GetString() == "Vertical" : r.ValueKind == System.Text.Json.JsonValueKind.Number && r.GetInt32() == 1);
            return (json.TryGetProperty("x", out var x) ? x.GetInt32() : 0, json.TryGetProperty("y", out var y) ? y.GetInt32() : 0, turned);
        }

        return null;
    }

    // the craft running (or finished and waiting to be taken) in an area, if any
    private HideoutProduction? Running(PmcData pmc, int type) => RunningWith(pmc, type)?.Recipe;

    // the craft an area holds, running or finished and not yet taken out, with its progress
    private (HideoutProduction Recipe, Production Production)? RunningWith(PmcData pmc, int type)
    {
        foreach (var (recipe, production) in pmc.Hideout?.Production ?? [])
        {
            if (production == null)
            {
                continue;
            }

            var r = hideout.Production?.Recipes?.FirstOrDefault(p => p.Id == recipe);
            if (r != null && (int)(r.AreaType ?? 0) == type)
            {
                return (r, production);
            }
        }

        return null;
    }

    // ---------- checks ----------

    // What stops a craft, or null when it can start.
    private string? CraftBlock(PmcData pmc, HideoutProduction recipe, List<(Item Item, int Count)> stash, out List<IdWithCount> items, out List<IdWithCount> tools)
    {
        items = [];
        tools = [];
        if (recipe.Continuous == true || recipe.IsCodeProduction == true)
        {
            return "This one runs by itself in the game.";
        }

        var type = (int)(recipe.AreaType ?? 0);
        var requirements = recipe.Requirements ?? [];
        foreach (var area in requirements.Where(r => r.Type == "Area"))
        {
            if (AreaLevel(pmc, area.AreaType ?? type) < (area.RequiredLevel ?? 0))
            {
                return $"Needs the area at level {area.RequiredLevel}.";
            }
        }

        if (requirements.FirstOrDefault(r => r.Type == "QuestComplete") is { } quest && !QuestDone(pmc, quest.QuestId))
        {
            return "Unlocked by a quest you haven't finished.";
        }

        if (recipe.Locked == true && pmc.UnlockedInfo?.UnlockedProductionRecipe?.Contains(recipe.Id) != true)
        {
            return "Not unlocked yet.";
        }

        if (requirements.Any(r => r.Type is not ("Area" or "Item" or "Tool" or "QuestComplete")))
        {
            return "This one needs something only the game can check. Start it in the game.";
        }

        if (RunningWith(pmc, type) is { } running)
        {
            // a finished craft still waiting in the area blocks it just the same; taking it out frees it
            var finished = running.Production.SptIsComplete == true || running.Production.AvailableForFinish == true;
            return finished
                ? "A finished craft is waiting here. Take it out in the game and the area is free again."
                : running.Recipe.Id == recipe.Id ? "Already crafting this." : "The area is busy with another craft.";
        }

        var used = new HashSet<MongoId>();
        var missing = new List<string>();
        foreach (var need in requirements.Where(r => r.Type is "Item" or "Tool" && r.TemplateId != null))
        {
            if (need.IsFunctional == true || need.IsEncoded == true)
            {
                return "This one needs a working or encoded item only the game can check. Start it in the game.";
            }

            var picked = Pick(stash, need.TemplateId!.Value.ToString(), need.Count ?? 1, need.IsSpawnedInSession == true, used);
            if (picked == null)
            {
                missing.Add(need.TemplateId!.Value.ToString());
                continue;
            }

            (need.Type == "Tool" ? tools : items).AddRange(picked.Select(p => new IdWithCount { Id = p.Item.Id, Count = p.Take }));
        }

        return missing.Count > 0 ? "missing:" + string.Join(',', missing) : null;
    }

    // What stops the next level of an area, or null when it can start.
    private string? UpgradeBlock(PmcData pmc, int type, List<(Item Item, int Count)> stash, out List<HideoutItem> items)
    {
        items = [];
        var area = (pmc.Hideout?.Areas ?? []).FirstOrDefault(a => a != null && (int)a.Type == type);
        var table = hideout.Areas?.FirstOrDefault(a => a?.Type != null && (int)a.Type == type);
        if (area == null || table == null)
        {
            return "This character doesn't have that area.";
        }

        if (area.Constructing == true)
        {
            return "Already being built.";
        }

        var next = (area.Level ?? 0) + 1;
        if (table.Stages == null || !table.Stages.TryGetValue(next.ToString(), out var stage) || stage == null)
        {
            return "Fully built.";
        }

        foreach (var r in stage.Requirements ?? [])
        {
            switch (r.Type)
            {
                case "Area" when AreaLevel(pmc, r.AreaType ?? 0) < (r.RequiredLevel ?? 0):
                    return $"Needs another area built first (level {r.RequiredLevel}).";
                case "TraderLoyalty" when (pmc.TradersInfo?.GetValueOrDefault(r.TraderId)?.LoyaltyLevel ?? 0) < (r.LoyaltyLevel ?? 0):
                    return $"Needs a trader at loyalty level {r.LoyaltyLevel}.";
                case "Skill" when (pmc.Skills?.Common ?? []).FirstOrDefault(s => s.Id.ToString() == r.SkillName)?.Progress / 100 < (r.SkillLevel ?? 0)
                    || (pmc.Skills?.Common ?? []).All(s => s.Id.ToString() != r.SkillName) && (r.SkillLevel ?? 0) > 0:
                    return $"Needs {r.SkillName} at level {r.SkillLevel}.";
                case "Area" or "TraderLoyalty" or "Skill" or "Item":
                    break;
                default:
                    return "This one needs something only the game can check. Start it in the game.";
            }
        }

        var used = new HashSet<MongoId>();
        var missing = new List<string>();
        foreach (var need in (stage.Requirements ?? []).Where(r => r.Type == "Item"))
        {
            var tpl = need.TemplateId.ToString();
            var count = need.Count ?? 1;
            if (count == 0)
            {
                continue;
            }

            var picked = Pick(stash, tpl, count, need.IsSpawnedInSession == true, used);
            if (picked == null)
            {
                missing.Add(tpl);
                continue;
            }

            items.AddRange(picked.Select(p => new HideoutItem { Id = p.Item.Id, Count = p.Take }));
        }

        return missing.Count > 0 ? "missing:" + string.Join(',', missing) : null;
    }

    // ---------- what the page shows ----------

    // One area for one character: its level, what it's building or crafting, whether the next level can start
    // and whether each craft can.
    public object? State(string id, int type)
    {
        if (!MongoId.IsValidMongoId(id) || profiles.GetPmcProfile(new MongoId(id)) is not { } pmc)
        {
            return null;
        }

        var mongo = new MongoId(id);
        var stash = Stash(pmc);
        var area = (pmc.Hideout?.Areas ?? []).FirstOrDefault(a => a != null && (int)a.Type == type);
        var upgradeWhy = UpgradeBlock(pmc, type, stash, out _);
        var running = Running(pmc, type);
        var production = running == null ? null : pmc.Hideout!.Production![running.Id];
        var crafts = new Dictionary<string, object>();
        foreach (var recipe in hideout.Production?.Recipes ?? [])
        {
            if (recipe == null || (int)(recipe.AreaType ?? 0) != type)
            {
                continue;
            }

            crafts[recipe.Id.ToString()] = new { why = CraftBlock(pmc, recipe, stash, out _, out _) };
        }

        var table = hideout.Areas?.FirstOrDefault(a => a?.Type != null && (int)a.Type == type);
        var next = table?.Stages?.GetValueOrDefault(((area?.Level ?? 0) + 1).ToString());
        return new
        {
            busy = Blocked(mongo),
            viaGame = GameListening(id),
            level = area?.Level ?? 0,
            constructing = area?.Constructing == true,
            completeTime = area?.Constructing == true ? area.CompleteTime : null,
            upgrade = new
            {
                why = upgradeWhy,
                // what each item of the next level needs, and what the stash has
                items = (next?.Requirements ?? []).Where(r => r.Type == "Item").Select(r => new
                {
                    id = r.TemplateId.ToString(), count = r.Count ?? 1, fir = r.IsSpawnedInSession == true,
                    have = Have(stash, r.TemplateId.ToString(), r.IsSpawnedInSession == true)
                })
            },
            crafting = running == null ? null : new
            {
                recipe = running.Id.ToString(), item = running.EndProduct.ToString(),
                start = production?.StartTimestamp, seconds = production?.ProductionTime, done = production?.SptIsComplete == true || production?.AvailableForFinish == true,
                timing = production == null ? null : Timing(pmc, running, production)
            },
            crafts,
            // what the stash holds of each craft's inputs
            have = (hideout.Production?.Recipes ?? []).Where(r => r != null && (int)(r.AreaType ?? 0) == type)
                .SelectMany(r => r.Requirements ?? []).Where(r => r.TemplateId != null).Select(r => r.TemplateId!.Value.ToString()).Distinct()
                .ToDictionary(t => t, t => Have(stash, t, false))
        };
    }

    // Every craft for one character, for the page's list of all crafts: whether each can start, what the stash
    // holds of their inputs, and what each area is crafting now.
    public object? Crafts(string id)
    {
        if (!MongoId.IsValidMongoId(id) || profiles.GetPmcProfile(new MongoId(id)) is not { } pmc)
        {
            return null;
        }

        var stash = Stash(pmc);
        var recipes = (hideout.Production?.Recipes ?? []).Where(r => r != null).ToList();
        var crafting = new Dictionary<string, object>();
        foreach (var type in recipes.Select(r => (int)(r.AreaType ?? 0)).Distinct())
        {
            if (Running(pmc, type) is { } running && pmc.Hideout?.Production?.GetValueOrDefault(running.Id) is { } production)
            {
                crafting[type.ToString()] = new
                {
                    recipe = running.Id.ToString(), item = running.EndProduct.ToString(), start = production.StartTimestamp, seconds = production.ProductionTime,
                    done = production.SptIsComplete == true || production.AvailableForFinish == true, timing = Timing(pmc, running, production)
                };
            }
        }

        return new
        {
            busy = Blocked(new MongoId(id)),
            viaGame = GameListening(id),
            crafts = recipes.ToDictionary(r => r.Id.ToString(), r => (object)new { why = CraftBlock(pmc, r, stash, out _, out _) }),
            crafting,
            have = recipes.SelectMany(r => r.Requirements ?? []).Where(r => r.TemplateId != null).Select(r => r.TemplateId!.Value.ToString()).Distinct()
                .ToDictionary(t => t, t => Have(stash, t, false))
        };
    }

    // ---------- doing it ----------

    public async Task<object> Craft(string id, string recipeId)
    {
        if (!MongoId.IsValidMongoId(id) || !MongoId.IsValidMongoId(recipeId) || profiles.GetPmcProfile(new MongoId(id)) is not { } pmc)
        {
            return new { ok = false, why = "No such character or recipe." };
        }

        var mongo = new MongoId(id);
        object? forGame = null;
        var recipeKey = new MongoId(recipeId);
        lock (_lock)
        {
            if (Blocked(mongo) is { } busy)
            {
                return new { ok = false, why = busy };
            }

            var recipe = hideout.Production?.Recipes?.FirstOrDefault(r => r?.Id == recipeKey);
            if (recipe == null)
            {
                return new { ok = false, why = "No such recipe." };
            }

            if (CraftBlock(pmc, recipe, Stash(pmc), out var items, out var tools) is { } why)
            {
                return new { ok = false, why };
            }

            if (Busy(mongo) != null)
            {
                forGame = new
                {
                    recipe = recipeId, items = items.Select(i => new { id = i.Id.ToString(), count = (int)(i.Count ?? 1) }),
                    tools = tools.Select(i => new { id = i.Id.ToString(), count = (int)(i.Count ?? 1) })
                };
            }
            else
            {
            controller.SingleProductionStart(pmc, new HideoutSingleProductionStartRequestData
            {
                Action = "HideoutSingleProductionStart", RecipeId = recipe.Id, Items = items, Tools = tools,
                Timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds()
            }, mongo);
            }
        }

        if (forGame != null)
        {
            logger.Info($"[Tushonkapedia] Asking the game to start craft {recipeId} for {pmc.Info?.Nickname}.");
            return await ViaGame(id, "craft", forGame, () => pmc.Hideout?.Production?.ContainsKey(recipeKey) == true);
        }

        await saveServer.SaveProfileAsync(mongo);
        logger.Info($"[Tushonkapedia] Started craft {recipeId} for {pmc.Info?.Nickname} from the page.");
        return new { ok = true };
    }

    public async Task<object> Upgrade(string id, int type)
    {
        if (!MongoId.IsValidMongoId(id) || profiles.GetPmcProfile(new MongoId(id)) is not { } pmc)
        {
            return new { ok = false, why = "No such character." };
        }

        var mongo = new MongoId(id);
        var viaGame = false;
        var levelBefore = pmc.Hideout?.Areas?.FirstOrDefault(a => (int)a.Type == type)?.Level ?? 0;
        lock (_lock)
        {
            if (Blocked(mongo) is { } busy)
            {
                return new { ok = false, why = busy };
            }

            var stash = Stash(pmc);
            if (UpgradeBlock(pmc, type, stash, out var items) is { } why)
            {
                return new { ok = false, why };
            }

            viaGame = Busy(mongo) != null;
            if (!viaGame)
            {
            // SPT takes a whole stack for anything but money; take just what's needed from a bigger stack here
            var whole = new List<HideoutItem>();
            foreach (var item in items)
            {
                var stack = stash.First(s => s.Item.Id == item.Id);
                if (!payments.IsMoneyTpl(stack.Item.Template) && stack.Count > (int)(item.Count ?? 1))
                {
                    stack.Item.Upd!.StackObjectsCount -= item.Count;
                    continue;
                }

                whole.Add(item);
            }

            // what the game would be sent back; nobody reads it here
            var output = outputs.GetOutput(mongo);
            var areaType = (HideoutAreas)type;
            controller.StartUpgrade(pmc, new HideoutUpgradeRequestData { Action = "HideoutUpgrade", AreaType = areaType, Items = whole, Timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds() }, mongo, output);
            // a level with no build time is finished at once, as the game does it
            var area = pmc.Hideout!.Areas!.First(a => (int)a.Type == type);
            if (area.Constructing != true)
            {
                controller.UpgradeComplete(pmc, new HideoutUpgradeCompleteRequestData { Action = "HideoutUpgradeComplete", AreaType = areaType, Timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds() }, mongo, output);
            }
            }
        }

        if (viaGame)
        {
            logger.Info($"[Tushonkapedia] Asking the game to upgrade hideout area {(HideoutAreas)type} for {pmc.Info?.Nickname}.");
            return await ViaGame(id, "upgrade", new { area = type }, () =>
                pmc.Hideout?.Areas?.FirstOrDefault(a => (int)a.Type == type) is { } now && (now.Constructing == true || now.Level > levelBefore));
        }

        await saveServer.SaveProfileAsync(mongo);
        logger.Info($"[Tushonkapedia] Started upgrading hideout area {(HideoutAreas)type} for {pmc.Info?.Nickname} from the page.");
        return new { ok = true };
    }

    // ---------- wishlist ----------

    // A character's wishlist: item -> its group, as the game numbers them (0 Tasks, 1 Hideout, 2 Barter, 3 Equipment,
    // 4 Other), and why it can't be changed now, if it can't
    public object? Wishlist(string id)
    {
        if (!MongoId.IsValidMongoId(id) || profiles.GetPmcProfile(new MongoId(id)) is not { } pmc)
        {
            return null;
        }

        return new { busy = Blocked(new MongoId(id)), viaGame = GameListening(id), items = (pmc.WishList ?? []).Select(w => new { id = w.Key.ToString(), group = w.Value }).ToList() };
    }

    // Adding, regrouping or removing one item, through SPT's own wishlist handlers, the ones the game calls; never
    // while the game is running for the character, for the same reason as the hideout
    public async Task<object> WishlistChange(string id, string action, string item, int group)
    {
        if (!MongoId.IsValidMongoId(id) || !MongoId.IsValidMongoId(item) || profiles.GetPmcProfile(new MongoId(id)) is not { } pmc)
        {
            return new { ok = false, why = "No such character or item." };
        }

        if (group is < 0 or > 4)
        {
            return new { ok = false, why = "No such wishlist group." };
        }

        var mongo = new MongoId(id);
        var key = new MongoId(item);
        if (Blocked(mongo) is { } blocked)
        {
            return new { ok = false, why = blocked };
        }

        if (Busy(mongo) != null)
        {
            logger.Info($"[Tushonkapedia] Asking the game to {action} {item} on {pmc.Info?.Nickname}'s wishlist.");
            return await ViaGame(id, "wishlist", new { action, item, group }, () => action == "remove"
                ? pmc.WishList?.ContainsKey(key) != true
                : pmc.WishList?.TryGetValue(key, out var now) == true && (int)now == group);
        }

        lock (_lock)
        {
            var has = pmc.WishList?.ContainsKey(key) == true;
            switch (action)
            {
                case "add" when !has:
                    wishlist.AddToWishList(pmc, new AddToWishlistRequest { Action = "AddToWishList", Items = new Dictionary<MongoId, int> { [key] = group } }, mongo);
                    break;
                case "add" or "group":
                    wishlist.ChangeWishListItemCategory(pmc, new ChangeWishlistItemCategoryRequest { Action = "ChangeWishlistItemCategory", Item = key, Category = group }, mongo);
                    break;
                case "remove" when has:
                    wishlist.RemoveFromWishList(pmc, new RemoveFromWishlistRequest { Action = "RemoveFromWishList", Items = [key] }, mongo);
                    break;
                case "remove":
                    break;
                default:
                    return new { ok = false, why = "Unknown action." };
            }
        }

        await saveServer.SaveProfileAsync(mongo);
        logger.Info($"[Tushonkapedia] Wishlist {action} {item} for {pmc.Info?.Nickname} from the page.");
        return new { ok = true };
    }
}
