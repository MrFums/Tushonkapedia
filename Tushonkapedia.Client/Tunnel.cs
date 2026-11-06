using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using BepInEx.Configuration;
using Comfort.Common;
using EFT;
using EFT.Hideout;
using EFT.Trading;
using EFT.UI;
using JsonType;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using SPT.Common.Http;
using UnityEngine;

namespace Tushonkapedia.Client
{
    // Lets the wiki act on a character while the game is open. The game keeps its own copy of the profile, so the server
    // can't change it behind the game's back; instead the page's action comes down here as a job and is done with the
    // game's own code, the same calls its buttons make. One request stays waiting on the server, in the menus only, so a
    // job arrives at once and the server's log stays quiet.
    internal static class Tunnel
    {
        private const string Next = "/wiki/api/tunnel/next";
        private const string Done = "/wiki/api/tunnel/done";

        private static ConfigEntry<bool> _enabled;

        private static bool InRaid => Singleton<GameWorld>.Instantiated;
        private static IEftSession Session => Singleton<ClientApplication<IEftSession>>.Instance?.GetClientBackEndSession();

        internal static void Start(Plugin plugin, ConfigFile config)
        {
            _enabled = config.Bind("General", "Let the wiki act through the game", true,
                "While you're in the menus, let Tushonkapedia's page start crafts and upgrades and change your wishlist through the game. "
                + "Turned off, the page waits for the game to close.");
            plugin.StartCoroutine(Run());
        }

        private static bool Ready => _enabled.Value && !InRaid && Session?.Profile != null && ItemUiContext.Instance != null
                                     && Singleton<HideoutRepresentation>.Instantiated;

        private static IEnumerator Run()
        {
            while (true)
            {
                if (!Ready)
                {
                    yield return new WaitForSeconds(3f);
                    continue;
                }

                var asked = Time.realtimeSinceStartup;
                var ask = Task.Run(() => RequestHandler.GetJson(Next));
                while (!ask.IsCompleted)
                {
                    yield return null;
                }

                JObject job = null;
                if (ask.IsFaulted)
                {
                    // an older server, or it's restarting
                    yield return new WaitForSeconds(15f);
                    continue;
                }

                try
                {
                    job = JObject.Parse(ask.Result);
                }
                catch (Exception e)
                {
                    Plugin.Log.LogDebug($"Tunnel answer unreadable: {e.Message}");
                }

                if (job?["job"] == null)
                {
                    // the server holds the request until there's a job; a quick empty answer means it won't (refused,
                    // or an older server), so wait instead of asking again at once
                    if (Time.realtimeSinceStartup - asked < 5f)
                    {
                        yield return new WaitForSeconds(30f);
                    }

                    continue;
                }

                var why = Do(job);
                Plugin.Log.LogInfo($"Tunnel {job["kind"]}: {why ?? "done"}");
                var id = job["job"].ToString();
                var query = $"{Done}?job={id}&ok={(why == null ? 1 : 0)}&why={Uri.EscapeDataString(why ?? "")}";
                Task.Run(() => RequestHandler.GetJson(query));
            }
        }

        // the job done, or why it can't be
        private static string Do(JObject job)
        {
            if (!Ready)
            {
                return "The game isn't in the menus.";
            }

            try
            {
                var args = (JObject)job["args"];
                switch (job["kind"]?.ToString())
                {
                    case "wishlist":
                        return Wishlist(args);
                    case "craft":
                        return Craft(args);
                    case "upgrade":
                        return Upgrade(args);
                    case "sell":
                        return Sell(args);
                    case "repair":
                        return Repair(args);
                    default:
                        return "This version of the game plugin doesn't know that action.";
                }
            }
            catch (Exception e)
            {
                Plugin.Log.LogError($"Tunnel: {e}");
                return $"The game couldn't do it: {e.GetBaseException().Message}";
            }
        }

        // what the wishlist's right-click menu does
        private static string Wishlist(JObject args)
        {
            var item = new MongoID(args["item"].ToString());
            var ui = ItemUiContext.Instance;
            if (args["action"]?.ToString() == "remove")
            {
                ui.RemoveFromWishList(item);
            }
            else
            {
                ui.AddToWishList(item, (EWishlistGroup)args["group"].Value<int>());
            }

            // the game sends these in batches: send this one now
            Session.FlushOperationQueue();
            return null;
        }

        // what the craft window's Start button sends, with the items the server picked from the stash
        private static string Craft(JObject args)
        {
            var hand = new Func<string, List<ItemToHandover>>(name =>
                args[name].Select(i => new ItemToHandover(i["id"].ToString(), i["count"].Value<int>())).ToList());
            Session.StartSingleProduction(args["recipe"].ToString(), hand("items"), hand("tools"));
            return null;
        }

        // what the trader screen's Deal button sends
        private static string Sell(JObject args)
        {
            var id = args["item"].ToString();
            var item = Session.Profile.Inventory.AllRealPlayerItems.FirstOrDefault(i => i.Id == id);
            if (item == null)
            {
                return "The game can't find that item.";
            }

            var reference = new TradingItemReference { Item = item, Count = args["count"].Value<int>(), SchemeId = 0 };
            Session.ConfirmSell(args["trader"].ToString(), new[] { reference }, args["price"].Value<int>(), result => { });
            return null;
        }

        // what the repair window's Repair button sends
        private static string Repair(JObject args)
        {
            Session.RepairItemsByTrader(args["trader"].ToString(), new RepairItem(args["item"].ToString(), args["points"].Value<float>()));
            return null;
        }

        // what the area screen's Upgrade button does
        private static string Upgrade(JObject args)
        {
            var type = (EAreaType)args["area"].Value<int>();
            var area = Singleton<HideoutRepresentation>.Instance.AreaDatas.FirstOrDefault(a => a.Template.Type == type);
            if (area == null)
            {
                return "The game doesn't have that area loaded yet.";
            }

            if (area.Status != EAreaStatus.ReadyToConstruct && area.Status != EAreaStatus.ReadyToUpgrade)
            {
                return $"The game says that area isn't ready ({area.Status}).";
            }

            // it runs on until the building's done, so it isn't waited for
            area.UpgradeAction().ContinueWith(t => Plugin.Log.LogError($"Tunnel upgrade: {t.Exception}"), TaskContinuationOptions.OnlyOnFaulted);
            return null;
        }
    }
}
