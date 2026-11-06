using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading.Tasks;
using BepInEx.Configuration;
using Comfort.Common;
using EFT;
using EFT.UI;
using HarmonyLib;
using Newtonsoft.Json;
using SPT.Common.Http;
using UnityEngine;

namespace Tushonkapedia.Client
{
    // The player's character for Tushonkapedia's "My character" page. The game shows the model on the Character
    // screen (and the side picker); when it has loaded there, and the gear or look changed since the last picture, a
    // camera of our own draws one small frame of it off-screen and sends it to the server. Nothing runs otherwise:
    // no model is loaded for it, no polling, and an unchanged character costs one short hash per screen.
    internal static class Portrait
    {
        private const string Route = "/wiki/api/portrait";
        private const int Width = 256;
        private const int Height = 384;
        private const float Settle = 1f;
        // the page's panel colour, so the picture sits on it without an edge
        private static readonly Color Background = new Color32(25, 28, 35, 255);

        // which character each model view was asked to show
        private static readonly ConditionalWeakTable<PlayerModelView, PlayerVisualRepresentation> Shown =
            new ConditionalWeakTable<PlayerModelView, PlayerVisualRepresentation>();

        private static Plugin _plugin;
        private static ConfigEntry<bool> _enabled;
        // which server, profile and gear the last picture was of: a new one is only sent when this changes.
        // Kept in the game's own player prefs, not the settings, as it's not something to change by hand.
        private const string LastKey = "Fums-TushonkaClient.LastCharacter";
        private static string Last
        {
            get => PlayerPrefs.GetString(LastKey, "");
            set { PlayerPrefs.SetString(LastKey, value); PlayerPrefs.Save(); }
        }
        private static bool _busy;

        internal static void Start(Plugin plugin, ConfigFile config)
        {
            _plugin = plugin;
            _enabled = config.Bind("General", "Draw my character", true,
                "When you open the Character screen and your gear or look changed, send a small picture of your character "
                + "to Tushonkapedia's \"My character\" page.");

            try
            {
                var harmony = new Harmony(Plugin.Guid + ".portrait");
                var show = AccessTools.GetDeclaredMethods(typeof(PlayerModelView))
                    .First(m => m.Name == nameof(PlayerModelView.Show) && m.GetParameters().FirstOrDefault()?.ParameterType == typeof(PlayerVisualRepresentation));
                harmony.Patch(show, prefix: new HarmonyMethod(typeof(Portrait), nameof(Showing)));
                harmony.Patch(AccessTools.Method(typeof(PlayerModelView), nameof(PlayerModelView.OnLoadingCompleted)),
                    postfix: new HarmonyMethod(typeof(Portrait), nameof(Loaded)));
                Plugin.Log.LogInfo("Watching for your character on the Character screen.");
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning($"Can't watch the Character screen, so no character picture: {e.Message}");
            }
        }

        private static void Showing(PlayerModelView __instance, PlayerVisualRepresentation __0)
        {
            Shown.Remove(__instance);
            if (__0 != null)
            {
                Shown.Add(__instance, __0);
            }
        }

        private static void Loaded(PlayerModelView __instance)
        {
            try
            {
                if (_busy || !_enabled.Value || Singleton<GameWorld>.Instantiated || !Shown.TryGetValue(__instance, out var visual))
                {
                    return;
                }

                // only your own PMC: the side picker also shows your Scav, and other screens other players
                var profile = Singleton<ClientApplication<IEftSession>>.Instance?.GetClientBackEndSession()?.Profile;
                if (profile == null || visual.Info == null || visual.Info.Side == EPlayerSide.Savage || visual.Info.Nickname != profile.Nickname)
                {
                    return;
                }

                var key = Key(profile);
                if (key == Last)
                {
                    Plugin.Log.LogDebug("Your character hasn't changed since the last picture; not drawing it again.");
                    return;
                }

                Plugin.Log.LogInfo("Drawing your character for Tushonkapedia.");
                _busy = true;
                _plugin.StartCoroutine(Draw(__instance, key));
            }
            catch (Exception e)
            {
                _busy = false;
                Plugin.Log.LogWarning($"Character picture skipped: {e}");
            }
        }

        // the picture's version (raise it when the drawing changes), the server, the profile, its look and everything you can see on it (what sits in slots, not in bags)
        private static string Key(Profile profile)
        {
            var text = new StringBuilder("2|").Append(RequestHandler.Host).Append('|').Append(profile.Id).Append('|').Append((int)profile.Side);
            foreach (var part in (profile.Customization ?? new BodyCustomization()).OrderBy(p => p.Key))
            {
                text.Append('|').Append(part.Value);
            }

            foreach (var slot in profile.Inventory.Equipment.AllSlots)
            {
                text.Append('|').Append(slot.ContainedItem?.TemplateId.ToString() ?? "-");
            }

            // FNV-1a, so the config keeps a short line
            var hash = 14695981039346656037UL;
            foreach (var c in text.ToString())
            {
                hash = (hash ^ c) * 1099511628211UL;
            }

            return hash.ToString("x16");
        }

        private static IEnumerator Draw(PlayerModelView view, string key)
        {
            // let the pose and the weapon settle
            yield return new WaitForSecondsRealtime(Settle);

            byte[] png = null;
            try
            {
                if (view == null || Singleton<GameWorld>.Instantiated)
                {
                    Plugin.Log.LogWarning("Couldn't draw your character: the screen closed first.");
                }
                else
                {
                    png = Render(view);
                }
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning($"Couldn't draw your character: {e}");
            }

            if (png == null)
            {
                _busy = false;
                yield break;
            }

            var body = JsonConvert.SerializeObject(new Dictionary<string, string> { ["png"] = Convert.ToBase64String(png) });
            var send = Task.Run(() => RequestHandler.PostJson(Route, body));
            while (!send.IsCompleted)
            {
                yield return null;
            }

            if (send.IsFaulted)
            {
                Plugin.Log.LogWarning($"Couldn't send the character picture: {send.Exception?.GetBaseException().Message}");
            }
            else if (send.Result != null && send.Result.Contains("true"))
            {
                Last = key;
                Plugin.Log.LogInfo($"Sent your character's picture ({png.Length / 1024} KB).");
            }
            else
            {
                Plugin.Log.LogWarning($"The server didn't keep your character's picture: {send.Result}");
            }

            _busy = false;
        }

        // One frame of the model on the Weapon Preview layer, framed to fit, from the same side the game shows it.
        private static byte[] Render(PlayerModelView view)
        {
            // the posed model, or failing that the view itself, which holds it
            var model = view.ModelPlayerPoser != null ? view.ModelPlayerPoser.gameObject
                : view.PlayerBody != null ? view.PlayerBody.gameObject : view.gameObject;

            var renderers = model.GetComponentsInChildren<Renderer>(false).Where(r => r.enabled && (r is SkinnedMeshRenderer || r is MeshRenderer)).ToList();
            // the shadow under the feet is a flat plane; framing it too leaves the character small
            var body = renderers.Where(r => r.bounds.size.y > 0.05f).ToList();
            if (renderers.Count == 0)
            {
                Plugin.Log.LogWarning($"Couldn't draw your character: no visible model under {model.name} "
                    + $"({model.GetComponentsInChildren<Renderer>(true).Length} renderers in all, poser {(view.ModelPlayerPoser != null ? "found" : "missing")}).");
                return null;
            }

            // the layers the model is on (the game puts it on Weapon Preview); the camera sees only those
            var mask = renderers.Aggregate(0, (m, r) => m | (1 << r.gameObject.layer));

            if (body.Count == 0)
            {
                body = renderers;
            }

            var bounds = body[0].bounds;
            foreach (var r in body.Skip(1))
            {
                bounds.Encapsulate(r.bounds);
            }

            // the game lights the model only while its own camera draws it (a CameraLightSwitcher on that camera turns
            // the lights on before the draw and off after), so ours borrows that switcher for the one frame
            var cameras = Camera.allCameras.Where(c => (c.cullingMask & mask) != 0).ToList();
            var source = cameras.FirstOrDefault(c => c.GetComponent<CameraLightSwitcher>() != null) ?? cameras.FirstOrDefault();
            var lights = source != null ? source.GetComponent<CameraLightSwitcher>() : null;
            if (lights == null)
            {
                Plugin.Log.LogInfo("No preview lights found for your character; the picture may be dark.");
            }
            var holder = new GameObject("Tushonkapedia portrait camera");
            var target = RenderTexture.GetTemporary(Width, Height, 24, RenderTextureFormat.ARGB32, RenderTextureReadWrite.Default, 4);
            var previous = RenderTexture.active;
            var picture = new Texture2D(Width, Height, TextureFormat.RGB24, false);
            try
            {
                var camera = holder.AddComponent<Camera>();
                if (source != null)
                {
                    camera.CopyFrom(source);
                }

                camera.enabled = false;
                camera.orthographic = false;
                camera.fieldOfView = 20f;
                camera.aspect = (float)Width / Height;
                camera.cullingMask = mask;
                camera.clearFlags = CameraClearFlags.SolidColor;
                camera.backgroundColor = Background;
                camera.allowHDR = false;
                camera.targetTexture = target;

                // look the way the game's camera looks, or at the model's front
                var forward = source != null ? source.transform.forward : -model.transform.forward;
                var half = Mathf.Tan(camera.fieldOfView * 0.5f * Mathf.Deg2Rad);
                var size = bounds.extents * 1.08f;
                var distance = Mathf.Max(size.y / half, Mathf.Max(size.x, size.z) / (half * camera.aspect)) + size.z;
                camera.transform.position = bounds.center - forward * distance;
                camera.transform.rotation = Quaternion.LookRotation(forward, Vector3.up);
                camera.nearClipPlane = Mathf.Max(0.01f, distance - size.magnitude * 2f);
                camera.farClipPlane = distance + size.magnitude * 2f;

                lights?.OnPreCull();
                try
                {
                    camera.Render();
                }
                finally
                {
                    lights?.OnPostRender();
                }

                RenderTexture.active = target;
                picture.ReadPixels(new Rect(0, 0, Width, Height), 0, 0);
                picture.Apply();
                return picture.EncodeToPNG();
            }
            finally
            {
                RenderTexture.active = previous;
                RenderTexture.ReleaseTemporary(target);
                UnityEngine.Object.Destroy(picture);
                UnityEngine.Object.Destroy(holder);
            }
        }
    }
}
