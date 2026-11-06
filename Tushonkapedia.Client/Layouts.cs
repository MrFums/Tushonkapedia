using System;
using System.Collections;
using System.Collections.Generic;
using System.Threading.Tasks;
using BepInEx.Configuration;
using Comfort.Common;
using EFT;
using EFT.UI.DragAndDrop;
using EFT.Utilities;
using Newtonsoft.Json;
using SPT.Common.Http;
using UnityEngine;
using UnityEngine.UI;

namespace Tushonkapedia.Client
{
    // Where a rig's (or backpack's) parts sit, as the game draws it. Items with a layout name have their parts placed by
    // a piece of the game's UI ("UI/Rig Layouts/<name>"); the server only knows the parts' sizes. In the main menu this
    // opens each layout the server asks for, out of sight, sizes its parts as the server's grids say, lets the UI lay
    // them out, and sends back each part's box. Only layout names and boxes are sent.
    internal static class Layouts
    {
        private const string WantedRoute = "/wiki/api/layouts/wanted";
        private const string UploadRoute = "/wiki/api/layouts/upload";

        // the plugin's main-menu setting covers this too
        private static ConfigEntry<bool> _enabled => Plugin.MenuWork;

        private sealed class Wanted
        {
            public string key;
            public string name;
            public List<int[]> grids;
        }

        internal static void Start(Plugin plugin, ConfigFile config)
        {
            plugin.StartCoroutine(Run());
        }

        private static bool InMenu => !Singleton<GameWorld>.Instantiated
                                      && Singleton<ClientApplication<IEftSession>>.Instance?.GetClientBackEndSession()?.Profile != null;

        private static IEnumerator Run()
        {
            while (!_enabled.Value || !InMenu)
            {
                yield return new WaitForSeconds(5f);
            }

            yield return new WaitForSeconds(5f);

            var ask = Task.Run(() => RequestHandler.GetJson(WantedRoute));
            while (!ask.IsCompleted)
            {
                yield return null;
            }

            List<Wanted> wanted = null;
            try
            {
                wanted = JsonConvert.DeserializeObject<List<Wanted>>(ask.Result);
            }
            catch (Exception e)
            {
                Plugin.Log.LogInfo($"Tushonkapedia didn't give container layouts to measure ({e.GetBaseException().Message}).");
            }

            if (wanted == null || wanted.Count == 0)
            {
                yield break;
            }

            // a world-space canvas no camera draws: the layouts are laid out at scale 1 and never seen
            var stage = new GameObject("Tushonkapedia layouts", typeof(RectTransform));
            UnityEngine.Object.DontDestroyOnLoad(stage);
            var canvas = stage.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.WorldSpace;
            stage.AddComponent<CanvasGroup>().alpha = 0f;
            stage.transform.position = new Vector3(0f, -100000f, 0f);

            var measured = new Dictionary<string, List<float[]>>();
            foreach (var want in wanted)
            {
                if (!InMenu)
                {
                    break;
                }

                TemplatedGridsView view = null;
                try
                {
                    var prefab = ResourcesCache.Pop<ContainedGridsView>("UI/Rig Layouts/" + want.name);
                    if (prefab != null)
                    {
                        view = UnityEngine.Object.Instantiate(prefab, stage.transform, false) as TemplatedGridsView;
                    }
                }
                catch (Exception e)
                {
                    Plugin.Log.LogDebug($"No layout {want.name}: {e.Message}");
                }

                // the game fills a layout's parts in order and draws no more than it has (ContainedGridsView.Show), so a
                // layout with fewer parts than the item shows only the first ones: those are measured
                var shown = view?._presetGridViews == null ? 0 : Math.Min(view._presetGridViews.Length, want.grids.Count);
                if (shown == 0)
                {
                    if (view != null)
                    {
                        UnityEngine.Object.Destroy(view.gameObject);
                    }

                    // no such layout in this game: sent as empty, so the server doesn't ask again
                    measured[want.key] = new List<float[]>();
                    continue;
                }

                try
                {
                    view.gameObject.SetActive(true);
                    // sized as the game sizes a part when it shows one (GridView.OnGridResized)
                    for (var i = 0; i < shown; i++)
                    {
                        var grid = view._presetGridViews[i];
                        grid.gameObject.SetActive(true);
                        var px = ItemViewFactory.GetCellPixelSize(new IntVec2(want.grids[i][0], want.grids[i][1]));
                        var element = grid.GetComponent<LayoutElement>();
                        if (element != null)
                        {
                            element.minWidth = px.X;
                            element.minHeight = px.Y;
                        }

                        ((RectTransform)grid.transform).sizeDelta = new Vector2(px.X, px.Y);
                    }
                }
                catch (Exception e)
                {
                    Plugin.Log.LogDebug($"Couldn't size layout {want.name}: {e.Message}");
                }

                // one frame for the UI to wake, then lay it out
                yield return null;

                try
                {
                    var root = (RectTransform)view.transform;
                    LayoutRebuilder.ForceRebuildLayoutImmediate(root);
                    Canvas.ForceUpdateCanvases();
                    LayoutRebuilder.ForceRebuildLayoutImmediate(root);

                    var boxes = new List<float[]>();
                    var corners = new Vector3[4];
                    float left = float.MaxValue, top = float.MinValue;
                    for (var i = 0; i < shown; i++)
                    {
                        ((RectTransform)view._presetGridViews[i].transform).GetWorldCorners(corners);
                        // corners: bottom left, top left, top right, bottom right; in the layout's own space
                        var a = root.InverseTransformPoint(corners[1]);
                        var b = root.InverseTransformPoint(corners[3]);
                        boxes.Add(new[] { a.x, a.y, b.x - a.x, a.y - b.y });
                        left = Mathf.Min(left, a.x);
                        top = Mathf.Max(top, a.y);
                    }

                    // from the top left of the parts, y going down
                    foreach (var box in boxes)
                    {
                        box[0] = Mathf.Round(box[0] - left);
                        box[1] = Mathf.Round(top - box[1]);
                        box[2] = Mathf.Round(box[2]);
                        box[3] = Mathf.Round(box[3]);
                    }

                    measured[want.key] = boxes;
                }
                catch (Exception e)
                {
                    Plugin.Log.LogDebug($"Couldn't measure layout {want.name}: {e.Message}");
                }

                UnityEngine.Object.Destroy(view.gameObject);
                // laying a layout out rebuilds the menu's canvases: one every half second, so it never stutters
                yield return new WaitForSecondsRealtime(0.5f);
            }

            UnityEngine.Object.Destroy(stage);
            if (measured.Count == 0)
            {
                yield break;
            }

            var body = JsonConvert.SerializeObject(measured);
            var send = Task.Run(() => RequestHandler.PostJson(UploadRoute, body));
            while (!send.IsCompleted)
            {
                yield return null;
            }

            if (send.IsFaulted)
            {
                Plugin.Log.LogWarning($"Couldn't send container layouts: {send.Exception?.GetBaseException().Message}");
            }
            else
            {
                Plugin.Log.LogInfo($"Sent {measured.Count} of {wanted.Count} container layouts to Tushonkapedia.");
            }
        }
    }
}
