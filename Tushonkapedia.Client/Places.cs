using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading.Tasks;
using BepInEx.Configuration;
using Comfort.Common;
using EFT;
using EFT.Interactive;
using Newtonsoft.Json;
using SPT.Common.Http;
using UnityEngine;

namespace Tushonkapedia.Client
{
    // Where things are on the map, as this game has it (its map mods included): a little after a raid starts, and
    // only when the server asks for that map (it hasn't seen it, or its tables changed what stands there), every container, every lock a key opens, every place a quest can send you and every extract is looked up and sent to
    // Tushonkapedia, which draws them on its maps. Only ids and positions are sent, never anything about the player.
    internal static class Places
    {
        private const string Route = "/wiki/api/places";
        private const string WantedRoute = "/wiki/api/places/wanted";
        // the scene is fully there a little after the raid starts
        private const float Settle = 15f;

        private static Plugin _plugin;
        private static ConfigEntry<bool> _enabled;

        internal static void Start(Plugin plugin, ConfigFile config)
        {
            _plugin = plugin;
            _enabled = config.Bind("General", "Record map places", true,
                "When a raid starts, send Tushonkapedia where the containers, locked doors and quest places are on that map, "
                + "so its maps match your game and your map mods.");
            plugin.StartCoroutine(Run());
        }

        private static IEnumerator Run()
        {
            while (true)
            {
                // wait for a raid
                while (!Singleton<GameWorld>.Instantiated || string.IsNullOrEmpty(Singleton<GameWorld>.Instance.LocationId))
                {
                    yield return new WaitForSeconds(2f);
                }

                var world = Singleton<GameWorld>.Instance;
                var map = world.LocationId;
                yield return new WaitForSeconds(Settle);
                if (_enabled.Value && Singleton<GameWorld>.Instantiated && Singleton<GameWorld>.Instance == world
                    && !map.Equals("hideout", StringComparison.OrdinalIgnoreCase))
                {
                    // only when the server needs it: a map it has never seen, or whose containers its tables changed.
                    // Normally it doesn't, and the raid costs nothing.
                    var ask = Task.Run(() => RequestHandler.GetJson(WantedRoute + "?map=" + Uri.EscapeDataString(map)));
                    while (!ask.IsCompleted)
                    {
                        yield return null;
                    }

                    var wanted = false;
                    try
                    {
                        wanted = !ask.IsFaulted && JsonConvert.DeserializeObject<Wanted>(ask.Result)?.wanted == true;
                    }
                    catch (Exception)
                    {
                        // an older server, or one without the wiki: nothing to send
                    }

                    if (wanted)
                    {
                        yield return Scan(map, world);
                    }
                }

                // wait for this raid to end before looking for the next
                while (Singleton<GameWorld>.Instantiated && Singleton<GameWorld>.Instance == world)
                {
                    yield return new WaitForSeconds(5f);
                }
            }
        }

        private sealed class Wanted
        {
            public bool wanted;
        }

        private static float[] At(Vector3 p) => new[] { (float)Math.Round(p.x, 2), (float)Math.Round(p.y, 2), (float)Math.Round(p.z, 2) };

        // one kind of thing a frame, so looking them up never stalls the raid
        private static IEnumerator Scan(string map, GameWorld world)
        {
            // work done on the game's own frames, and the whole time until the server has it
            var frames = new Stopwatch();
            var total = Stopwatch.StartNew();
            frames.Start();
            var boxes = new Dictionary<string, float[]>();
            var types = new Dictionary<string, string>();
            foreach (var box in UnityEngine.Object.FindObjectsOfType<LootableContainer>())
            {
                if (!string.IsNullOrEmpty(box.Id))
                {
                    boxes[box.Id] = At(box.transform.position);
                    if (!string.IsNullOrEmpty(box.Template))
                    {
                        types[box.Id] = box.Template;
                    }
                }
            }

            frames.Stop();
            yield return null;
            if (Singleton<GameWorld>.Instance != world)
            {
                yield break;
            }

            frames.Start();

            // doors, safes, car trunks... anything with a key
            var locks = new Dictionary<string, List<float[]>>();
            foreach (var thing in UnityEngine.Object.FindObjectsOfType<WorldInteractiveObject>())
            {
                if (string.IsNullOrEmpty(thing.KeyId))
                {
                    continue;
                }

                if (!locks.TryGetValue(thing.KeyId, out var list))
                {
                    locks[thing.KeyId] = list = new List<float[]>();
                }

                list.Add(At(thing.transform.position));
            }

            frames.Stop();
            yield return null;
            if (Singleton<GameWorld>.Instance != world)
            {
                yield break;
            }

            frames.Start();

            // the places quests send you: the middle of each trigger's area
            var zones = new Dictionary<string, List<float[]>>();
            foreach (var trigger in UnityEngine.Object.FindObjectsOfType<TriggerWithId>())
            {
                if (string.IsNullOrEmpty(trigger.Id))
                {
                    continue;
                }

                var area = trigger.GetComponent<Collider>();
                if (!zones.TryGetValue(trigger.Id, out var list))
                {
                    zones[trigger.Id] = list = new List<float[]>();
                }

                list.Add(At(area != null ? area.bounds.center : trigger.transform.position));
            }

            frames.Stop();
            yield return null;
            if (Singleton<GameWorld>.Instance != world)
            {
                yield break;
            }

            frames.Start();

            // the extracts, by their name (the Scavs' ones too)
            var exits = new Dictionary<string, List<float[]>>();
            foreach (var exit in UnityEngine.Object.FindObjectsOfType<ExfiltrationPoint>())
            {
                var name = exit.Settings?.Name;
                if (string.IsNullOrEmpty(name))
                {
                    continue;
                }

                if (!exits.TryGetValue(name, out var list))
                {
                    exits[name] = list = new List<float[]>();
                }

                list.Add(At(exit.transform.position));
            }

            var json = JsonConvert.SerializeObject(new { map, boxes, types, locks, zones, exits });
            frames.Stop();
            var send = Task.Run(() => RequestHandler.PostJson(Route, json));
            while (!send.IsCompleted)
            {
                yield return null;
            }

            total.Stop();
            if (send.IsFaulted)
            {
                Plugin.Log.LogWarning($"Couldn't send the places on {map}: {send.Exception?.GetBaseException().Message}");
                yield break;
            }

            Plugin.Log.LogInfo($"{map}: {boxes.Count} containers, {locks.Count} keys' locks, {zones.Count} quest places, {exits.Count} extracts. "
                + $"Looked up in {frames.Elapsed.TotalMilliseconds:0.0} ms over 4 frames, sent in {total.Elapsed.TotalSeconds:0.00} s.");
        }
    }
}
