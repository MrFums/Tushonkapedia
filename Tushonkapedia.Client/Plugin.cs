using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using Comfort.Common;
using Diz.DependencyManager;
using Diz.Resources;
using EFT;
using EFT.InventoryLogic;
using EFT.UI.DragAndDrop;
using JsonType;
using Newtonsoft.Json;
using SPT.Common.Http;
using UnityEngine;

namespace Tushonkapedia.Client
{
    // Tushonkapedia shows item pictures from tarkov.dev, which has none for mod items. This draws those with the
    // game's own icon renderer while you sit in the main menu, a few at a time, and sends them to the server, which
    // keeps them. Each item is drawn once per server: next time the server asks for none.
    [BepInPlugin(Guid, Name, Version)]
    public class Plugin : BaseUnityPlugin
    {
        public const string Guid = "com.fums.tushonkapedia.client";
        public const string Name = "Fums-TushonkaClient";
        // BepInEx wants a plain version (no "-alpha"): the package version is in the csproj
        public const string Version = "1.0.0";

        private const string WantedRoute = "/wiki/api/icons/wanted";
        private const string UploadRoute = "/wiki/api/icons/upload";
        private const int PerBatch = 8;
        private const int PerUpload = 24;
        private const float IconTimeout = 8f;
        private const float IconGap = 0.2f;
        private const float BatchGap = 1f;

        // drawn like the pictures tarkov.dev has for base-game items: the item on its grid cell, with the cell's border
        private const int Size = 128;
        private const int Border = 2;
        private static readonly Color32 Edge = new Color32(73, 81, 84, 255);

        private static readonly Dictionary<TaxonomyColor, Color32> Cells = new Dictionary<TaxonomyColor, Color32>
        {
            [TaxonomyColor.@default] = new Color32(50, 50, 50, 255),
            [TaxonomyColor.black] = new Color32(12, 12, 12, 255),
            [TaxonomyColor.grey] = new Color32(21, 21, 21, 255),
            [TaxonomyColor.blue] = new Color32(21, 32, 38, 255),
            [TaxonomyColor.yellow] = new Color32(44, 43, 24, 255),
            [TaxonomyColor.green] = new Color32(18, 26, 12, 255),
            [TaxonomyColor.red] = new Color32(45, 23, 19, 255),
            [TaxonomyColor.violet] = new Color32(35, 25, 38, 255),
            [TaxonomyColor.orange] = new Color32(30, 20, 12, 255),
            [TaxonomyColor.tracerYellow] = new Color32(44, 43, 24, 255),
            [TaxonomyColor.tracerGreen] = new Color32(18, 26, 12, 255),
            [TaxonomyColor.tracerRed] = new Color32(45, 23, 19, 255)
        };

        internal static ManualLogSource Log;

        // the main-menu work: item pictures here, container layouts in Layouts
        internal static ConfigEntry<bool> MenuWork;
        private static ConfigEntry<bool> _enabled => MenuWork;

        private static bool InRaid => Singleton<GameWorld>.Instantiated;

        private void Awake()
        {
            Log = Logger;
            MenuWork = Config.Bind("General", "Fill in missing pictures", true,
                "In the main menu, draw the pictures of items Tushonkapedia has none for (mostly mod items), and measure the pockets "
                + "of rigs and backpacks it hasn't seen, so its pages match your game. Runs once per item; turn off to skip it.");
            // A game with no graphics (a Fika headless host) can't draw: its pictures would come out blank. It has no
            // player at its menus either, so nothing for the wiki to do through it. It still records map places (it
            // hosts the raids); the menu work is left to the players' games.
            var drawing = SystemInfo.graphicsDeviceType != UnityEngine.Rendering.GraphicsDeviceType.Null;
            if (drawing)
            {
                StartCoroutine(Run());
                Portrait.Start(this, Config);
                Tunnel.Start(this, Config);
                Layouts.Start(this, Config);
            }
            else
            {
                Log.LogInfo("No graphics (a headless game): only recording map places for Tushonkapedia; pictures and the rest are left to the players' games.");
            }

            Places.Start(this, Config);
        }

        private IEnumerator Run()
        {
            // the menu: items can be made and models loaded
            while (!_enabled.Value || InRaid || !Singleton<ItemFactory>.Instantiated || !Singleton<IEasyAssets>.Instantiated
                   || Singleton<ClientApplication<IEftSession>>.Instance?.GetClientBackEndSession()?.Profile == null)
            {
                yield return new WaitForSeconds(5f);
            }

            yield return new WaitForSeconds(10f);

            // With other players on the server, the server hands each game the pictures nobody else is drawing. One
            // that stops halfway (closed, or went into a raid) lets its share go after a while, so this game asks
            // again now and then and draws what was left. Each item is tried once per game start.
            var tried = new HashSet<string>();
            while (true)
            {
                while (InRaid || !_enabled.Value)
                {
                    yield return new WaitForSeconds(5f);
                }

                var ask = Task.Run(() => RequestHandler.GetJson(WantedRoute));
                while (!ask.IsCompleted)
                {
                    yield return null;
                }

                List<string> wanted = null;
                try
                {
                    wanted = JsonConvert.DeserializeObject<List<string>>(ask.Result);
                }
                catch (Exception e)
                {
                    Log.LogInfo($"Tushonkapedia isn't on this server, or didn't answer ({e.GetBaseException().Message}). No pictures drawn.");
                    yield break;
                }

                wanted = (wanted ?? new List<string>()).Where(tried.Add).ToList();
                if (wanted.Count > 0)
                {
                    yield return Draw(wanted);
                }

                yield return new WaitForSecondsRealtime(AskAgain);
            }
        }

        // a little over the time the server holds another game's share
        private const float AskAgain = 11 * 60f;

        private IEnumerator Draw(List<string> wanted)
        {
            Log.LogInfo($"Drawing {wanted.Count} item pictures for Tushonkapedia.");
            var drawn = new Dictionary<string, string>();
            var sent = 0;
            for (var start = 0; start < wanted.Count; start += PerBatch)
            {
                while (InRaid || !_enabled.Value)
                {
                    yield return new WaitForSeconds(5f);
                }

                yield return Batch(wanted.Skip(start).Take(PerBatch).ToList(), drawn);
                yield return new WaitForSecondsRealtime(BatchGap);
                if (drawn.Count >= PerUpload || start + PerBatch >= wanted.Count)
                {
                    sent += drawn.Count;
                    yield return Upload(drawn);
                    drawn = new Dictionary<string, string>();
                }
            }

            Log.LogInfo($"Sent {sent} of {wanted.Count} item pictures to Tushonkapedia.");
        }

        private IEnumerator Batch(List<string> ids, Dictionary<string, string> drawn)
        {
            var items = new List<KeyValuePair<string, Item>>();
            foreach (var id in ids)
            {
                try
                {
                    // a gun with its default parts on, as the encyclopedia and the traders show it
                    var item = Singleton<ItemFactory>.Instance.GetPresetItem(id);
                    if (item != null)
                    {
                        items.Add(new KeyValuePair<string, Item>(id, item));
                    }
                    else
                    {
                        drawn[id] = "";
                    }
                }
                catch (Exception e)
                {
                    // sent as empty: the server won't ask for it again until the item changes
                    drawn[id] = "";
                    Log.LogDebug($"Can't make {id}: {e.Message}");
                }
            }

            // the models have to be loaded before the icon is drawn, or it comes out blank and stays cached blank
            var paths = items.SelectMany(i => i.Value.GetAllItems()).SelectMany(i => i.Template.AllResources)
                .Where(k => k != null && !string.IsNullOrEmpty(k.path)).Select(k => k.path).Distinct().ToArray();
            DependencyGraph<IEasyBundle>.Token token = null;
            Task loading = null;
            try
            {
                token = Singleton<IEasyAssets>.Instance.System.Retain(paths, null);
                loading = EasyAssetsExtensions.LoadBundles(token);
            }
            catch (Exception e)
            {
                Log.LogWarning($"Couldn't load models for {ids.Count} items: {e.Message}");
            }

            while (loading != null && !loading.IsCompleted)
            {
                yield return null;
            }

            if (loading == null || loading.IsFaulted || loading.IsCanceled)
            {
                Log.LogDebug($"Models didn't load for {string.Join(", ", ids.ToArray())}");
                Release(token);
                // one bad model fails the whole batch: try each on its own, so only that one counts as failed
                if (items.Count > 1)
                {
                    foreach (var pair in items)
                    {
                        yield return Batch(new List<string> { pair.Key }, drawn);
                    }
                }
                else
                {
                    foreach (var pair in items)
                    {
                        drawn[pair.Key] = "";
                    }
                }

                yield break;
            }

            foreach (var pair in items)
            {
                ItemIcon icon = null;
                try
                {
                    icon = ItemViewFactory.LoadItemIcon(pair.Value);
                }
                catch (Exception e)
                {
                    Log.LogDebug($"No icon for {pair.Key}: {e.Message}");
                }

                var until = Time.realtimeSinceStartup + IconTimeout;
                while (icon != null && icon.Sprite == null && Time.realtimeSinceStartup < until)
                {
                    yield return null;
                }

                if (icon?.Sprite == null)
                {
                    drawn[pair.Key] = "";
                    continue;
                }

                try
                {
                    drawn[pair.Key] = Convert.ToBase64String(Png(icon.Sprite, pair.Value.BackgroundColor));
                }
                catch (Exception e)
                {
                    drawn[pair.Key] = "";
                    Log.LogDebug($"Couldn't save the icon of {pair.Key}: {e.Message}");
                }

                // spread out: copying a picture off the graphics card and packing it costs a frame, so one at a time with
                // a gap, and the menu never stutters. Slower the first time, and there is only a first time.
                yield return new WaitForSecondsRealtime(IconGap);
            }

            Release(token);
        }

        private static void Release(DependencyGraph<IEasyBundle>.Token token)
        {
            try
            {
                token?.Release();
            }
            catch (Exception e)
            {
                Log.LogDebug($"Couldn't release models: {e.Message}");
            }
        }

        private static IEnumerator Upload(Dictionary<string, string> drawn)
        {
            if (drawn.Count == 0)
            {
                yield break;
            }

            var body = JsonConvert.SerializeObject(drawn);
            var send = Task.Run(() => RequestHandler.PostJson(UploadRoute, body));
            while (!send.IsCompleted)
            {
                yield return null;
            }

            if (send.IsFaulted)
            {
                Log.LogWarning($"Couldn't send item pictures: {send.Exception?.GetBaseException().Message}");
            }
        }

        // The sprite's part of its texture, fitted onto a grid cell, as a PNG. Copied through a render texture: icon
        // textures can't be read. One pixel is cut from each side: the icon sheet leaves a light line along the edge.
        private static byte[] Png(Sprite sprite, TaxonomyColor color)
        {
            var texture = sprite.texture;
            var rect = sprite.textureRect;
            var crop = new Rect(rect.x + 1, rect.y + 1, Mathf.Max(1, rect.width - 2), Mathf.Max(1, rect.height - 2));
            var inner = Size - 2 * Border;
            var scale = Mathf.Min(inner / crop.width, inner / crop.height);
            var width = Mathf.Clamp(Mathf.RoundToInt(crop.width * scale), 1, inner);
            var height = Mathf.Clamp(Mathf.RoundToInt(crop.height * scale), 1, inner);

            var target = RenderTexture.GetTemporary(width, height, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.Default);
            var previous = RenderTexture.active;
            var copy = new Texture2D(width, height, TextureFormat.ARGB32, false);
            var cell = new Texture2D(Size, Size, TextureFormat.ARGB32, false);
            try
            {
                Graphics.Blit(texture, target, new Vector2(crop.width / texture.width, crop.height / texture.height),
                    new Vector2(crop.x / texture.width, crop.y / texture.height));
                RenderTexture.active = target;
                copy.ReadPixels(new Rect(0, 0, width, height), 0, 0);
                copy.Apply();
                var item = copy.GetPixels32();

                Color32 fill;
                if (!Cells.TryGetValue(color, out fill))
                {
                    fill = Cells[TaxonomyColor.@default];
                }

                var pixels = new Color32[Size * Size];
                for (var y = 0; y < Size; y++)
                {
                    for (var x = 0; x < Size; x++)
                    {
                        var edge = x < Border || y < Border || x >= Size - Border || y >= Size - Border;
                        pixels[y * Size + x] = edge ? Edge : fill;
                    }
                }

                var left = (Size - width) / 2;
                var bottom = (Size - height) / 2;
                for (var y = 0; y < height; y++)
                {
                    for (var x = 0; x < width; x++)
                    {
                        var top = item[y * width + x];
                        var at = (bottom + y) * Size + left + x;
                        var under = pixels[at];
                        var a = top.a / 255f;
                        pixels[at] = new Color32((byte)(top.r * a + under.r * (1 - a)), (byte)(top.g * a + under.g * (1 - a)),
                            (byte)(top.b * a + under.b * (1 - a)), 255);
                    }
                }

                cell.SetPixels32(pixels);
                cell.Apply();
                return cell.EncodeToPNG();
            }
            finally
            {
                RenderTexture.active = previous;
                RenderTexture.ReleaseTemporary(target);
                Destroy(copy);
                Destroy(cell);
            }
        }
    }
}
