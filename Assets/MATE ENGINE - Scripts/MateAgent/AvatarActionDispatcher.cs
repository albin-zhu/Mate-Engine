using System;
using System.Collections;
using System.Linq;
using UnityEngine;
using CustomDancePlayer;

namespace MateEngine.Agent
{
    /// <summary>
    /// Translates AI-decided actions into avatar component calls.
    /// Finds the active avatar components using the FindAvatarSmart pattern.
    /// </summary>
    public class AvatarActionDispatcher : MonoBehaviour
    {
        Animator _animator;
        AvatarWindowHandler _windowHandler;
        AvatarBubbleHandler _bubbleHandler;
        AvatarDanceHandler _danceHandler;
        BlendshapeManager _blendshapeManager;
        AvatarAnimatorController _animController;
        AvatarLocomotionController _locomotion;

        static readonly int isTalkingHash = Animator.StringToHash("isTalking");
        static readonly int idleIndexHash = Animator.StringToHash("IdleIndex");

        float _lastRefresh;
        const float RefreshInterval = 2f;

        void Update()
        {
            if (Time.unscaledTime - _lastRefresh > RefreshInterval)
            {
                _lastRefresh = Time.unscaledTime;
                RefreshReferences();
            }
        }

        void RefreshReferences()
        {
            Animator found = null;
            var loader = FindFirstObjectByType<VRMLoader>();
            if (loader != null)
            {
                var current = loader.GetCurrentModel();
                if (current != null)
                    found = current.GetComponentsInChildren<Animator>(true)
                        .FirstOrDefault(a => a && a.gameObject.activeInHierarchy);
            }
            if (found == null)
            {
                var modelParent = GameObject.Find("Model");
                if (modelParent != null)
                    found = modelParent.GetComponentsInChildren<Animator>(true)
                        .FirstOrDefault(a => a && a.gameObject.activeInHierarchy);
            }
            if (found == null)
                found = FindFirstObjectByType<Animator>();

            if (found != null && found != _animator)
            {
                _animator = found;
                _windowHandler = FindFirstObjectByType<AvatarWindowHandler>();
                _bubbleHandler = FindFirstObjectByType<AvatarBubbleHandler>();
                _danceHandler = FindFirstObjectByType<AvatarDanceHandler>();
                _blendshapeManager = FindFirstObjectByType<BlendshapeManager>();
                _animController = FindFirstObjectByType<AvatarAnimatorController>();
                _locomotion = FindFirstObjectByType<AvatarLocomotionController>();
            }
        }

        /// <summary>Execute a named action with parameters. Returns success message or error.</summary>
        public string Execute(string action, string param = "")
        {
            if (_animator == null) RefreshReferences();
            if (_animator == null) return "error: no avatar found";

            try
            {
                switch (action)
                {
                    case "show_bubble":
                        return ShowBubble(param);
                    case "play_expression":
                        return PlayExpression(param);
                    case "play_dance":
                        return PlayDance(param);
                    case "stop_dance":
                        return StopDance();
                    case "set_idle":
                        return SetIdle(param);
                    case "snap_to_window":
                        return SnapToWindow(param);
                    case "unsnap":
                        return Unsnap();
                    case "set_talking":
                        return SetTalking(param);
                    case "change_outfit":
                    case "set_outfit":
                        return ChangeOutfit(param);
                    case "walk_to":
                        return WalkTo(param);
                    case "go_away":
                        return GoAway();
                    case "get_lost":
                        return GetLost();
                    default:
                        return $"error: unknown action '{action}'";
                }
            }
            catch (Exception e)
            {
                return $"error: {e.Message}";
            }
        }

        /// <summary>Fired when the AI requests a show_bubble. Subscribers render the inner-OS overlay.</summary>
        public static event System.Action<string> OnShowBubble;

        string ShowBubble(string text)
        {
            OnShowBubble?.Invoke(text);
            return "ok: bubble";
        }

        string PlayExpression(string name)
        {
            var bs = FindFirstObjectByType<UniversalBlendshapes>();
            if (bs == null) return "error: no blendshapes";
            var field = typeof(UniversalBlendshapes).GetField(name,
                System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);
            if (field == null || field.FieldType != typeof(float))
                return $"error: unknown expression '{name}'";
            field.SetValue(bs, 1.0f);
            StartCoroutine(ResetExpressionAfter(bs, field, 4.0f));
            return $"ok: expression '{name}'";
        }

        IEnumerator ResetExpressionAfter(UniversalBlendshapes bs, System.Reflection.FieldInfo field, float seconds)
        {
            yield return new WaitForSecondsRealtime(seconds);
            if (bs != null) field.SetValue(bs, 0.0f);
        }

        string PlayDance(string param)
        {
            if (_danceHandler == null) return "error: no dance handler";
            if (int.TryParse(param, out int index))
            {
                bool ok = _danceHandler.PlayIndex(index);
                return ok ? $"ok: playing dance {index}" : $"error: dance index {index} failed";
            }
            // Try by title
            int idx = _danceHandler.FindIndexByTitle(param);
            if (idx >= 0)
            {
                _danceHandler.PlayIndex(idx);
                return $"ok: playing dance '{param}'";
            }
            // Fallback: pick a random valid dance
            var dances = _danceHandler.GetDanceList();
            if (dances.Count > 0)
            {
                int fallbackIdx = dances[UnityEngine.Random.Range(0, dances.Count)].Item1;
                _danceHandler.PlayIndex(fallbackIdx);
                return $"ok: fallback dance {fallbackIdx} ('{param}' not found)";
            }
            return $"error: dance '{param}' not found";
        }

        string StopDance()
        {
            if (_danceHandler == null) return "error: no dance handler";
            _danceHandler.StopPlay();
            return "ok: dance stopped";
        }

        string SetIdle(string param)
        {
            if (_animator == null) return "error: no animator";
            if (!int.TryParse(param, out int index)) return "error: invalid idle index";
            _animator.SetInteger(idleIndexHash, index);
            return $"ok: idle set to {index}";
        }

        string SnapToWindow(string title)
        {
            if (_windowHandler == null) return "error: no window handler";
            _windowHandler.SnapToWindowByTitle(title);
            return $"ok: snap requested for '{title}'";
        }

        string Unsnap()
        {
            if (_windowHandler == null) return "error: no window handler";
            _windowHandler.ForceExitWindowSitting();
            return "ok: unsnapped";
        }

        string SetTalking(string param)
        {
            if (_animator == null) return "error: no animator";
            bool val = param == "true" || param == "1";
            _animator.SetBool(isTalkingHash, val);
            return $"ok: talking={val}";
        }

        string ChangeOutfit(string param)
        {
            // Find MEClothes component using reflection (it's in Mod SDK, may not be available at compile time)
            Component clothesComp = null;
            System.Type clothesType = null;
            foreach (var comp in FindObjectsByType<MonoBehaviour>(FindObjectsInactive.Exclude, FindObjectsSortMode.None))
            {
                var t = comp.GetType();
                if (t.Name == "MEClothes" && t.GetMethod("ActivateOutfit") != null)
                {
                    clothesComp = comp;
                    clothesType = t;
                    break;
                }
            }
            if (clothesComp == null) return "error: no clothes system found";

            // Parse JSON param: { "indices": [0,1,2], "action": "equip"|"unequip"|"toggle" }
            // Or legacy: single index "0" or name "school"
            var entriesField = clothesType.GetField("entries");
            if (entriesField == null) return "error: cannot access outfit entries";
            var entries = entriesField.GetValue(clothesComp) as System.Array;
            if (entries == null) return "error: no outfit entries";

            // Try parse as JSON first
            try
            {
                var json = Newtonsoft.Json.Linq.JObject.Parse(param);
                var indicesToken = json["indices"];
                string actionType = json["action"]?.ToString() ?? "toggle";
                var results = new System.Collections.Generic.List<string>();

                if (indicesToken is Newtonsoft.Json.Linq.JArray arr)
                {
                    foreach (var idxToken in arr)
                    {
                        int idx = idxToken.ToObject<int>();
                        if (idx < 0 || idx >= entries.Length) continue;
                        string result = SetOutfitState(clothesComp, clothesType, entries, idx, actionType);
                        results.Add(result);
                    }
                    return $"ok: outfits [{string.Join(", ", results)}] {actionType}ed";
                }
                else if (indicesToken != null)
                {
                    int idx = indicesToken.ToObject<int>();
                    if (idx >= 0 && idx < entries.Length)
                    {
                        string result = SetOutfitState(clothesComp, clothesType, entries, idx, actionType);
                        return $"ok: outfit {result} {actionType}ed";
                    }
                }
            }
            catch (Newtonsoft.Json.JsonReaderException)
            {
                // Not JSON, try legacy format
            }

            // Legacy: single index
            if (int.TryParse(param, out int singleIndex))
            {
                if (singleIndex >= 0 && singleIndex < entries.Length)
                {
                    string name = GetOutfitName(entries, singleIndex);
                    var method = clothesType.GetMethod("ActivateOutfit");
                    method.Invoke(clothesComp, new object[] { singleIndex });
                    return $"ok: outfit '{name}' toggled";
                }
                return $"error: outfit index {singleIndex} out of range";
            }

            // Legacy: find by name
            for (int i = 0; i < entries.Length; i++)
            {
                var entry = entries.GetValue(i);
                if (entry == null) continue;
                string name = GetOutfitName(entries, i);
                if (!string.IsNullOrEmpty(name) && name.ToLowerInvariant().Contains(param.ToLowerInvariant()))
                {
                    var method = clothesType.GetMethod("ActivateOutfit");
                    method.Invoke(clothesComp, new object[] { i });
                    return $"ok: outfit '{name}' toggled";
                }
            }
            return $"error: outfit '{param}' not found";
        }

        string WalkTo(string param)
        {
            if (_locomotion == null) return "error: no locomotion controller";

            // Parse JSON param: { "x": <int> } OR just a raw integer
            int targetX = -1;

            try
            {
                var json = Newtonsoft.Json.Linq.JObject.Parse(param);
                targetX = json["x"]?.ToObject<int>() ?? -1;
            }
            catch (Newtonsoft.Json.JsonReaderException)
            {
                // Not JSON, try raw integer
                int.TryParse(param, out targetX);
            }

            if (targetX < 0) return "error: invalid target position (expected x coordinate)";

            _locomotion.WalkToPosition(targetX);
            return $"ok: walking to x={targetX}";
        }

        string GoAway()
        {
            if (_locomotion == null) return "error: no locomotion controller";
            _locomotion.GoAway();
            return "ok: walking to screen edge";
        }

        string GetLost()
        {
            if (_locomotion == null) return "error: no locomotion controller";
            _locomotion.GetLost();
            return "ok: walking off-screen";
        }

        string SetOutfitState(Component clothesComp, System.Type clothesType, System.Array entries, int index, string action)
        {
            var entry = entries.GetValue(index);
            if (entry == null) return $"index{index}(null)";

            string name = GetOutfitName(entries, index);
            var gameObjectsField = entry.GetType().GetField("gameObjects");
            if (gameObjectsField == null) return $"index{index}(no objects)";

            var objs = gameObjectsField.GetValue(entry) as GameObject[];
            if (objs == null) return $"index{index}(empty)";

            bool isCurrentlyOn = IsAnyGameObjectActive(objs);

            switch (action.ToLowerInvariant())
            {
                case "equip":
                case "on":
                case "wear":
                    if (!isCurrentlyOn)
                    {
                        var method = clothesType.GetMethod("ActivateOutfit");
                        method.Invoke(clothesComp, new object[] { index });
                    }
                    break;
                case "unequip":
                case "off":
                case "remove":
                    if (isCurrentlyOn)
                    {
                        var method = clothesType.GetMethod("ActivateOutfit");
                        method.Invoke(clothesComp, new object[] { index });
                    }
                    break;
                case "toggle":
                default:
                    var toggleMethod = clothesType.GetMethod("ActivateOutfit");
                    toggleMethod.Invoke(clothesComp, new object[] { index });
                    break;
            }
            return $"'{name}'";
        }

        string GetOutfitName(System.Array entries, int index)
        {
            if (index < 0 || index >= entries.Length) return null;
            var entry = entries.GetValue(index);
            if (entry == null) return $"unnamed_{index}";
            var nameField = entry.GetType().GetField("name");
            string name = nameField?.GetValue(entry) as string;
            return !string.IsNullOrEmpty(name) ? name : $"outfit_{index}";
        }

        bool IsAnyGameObjectActive(GameObject[] targets)
        {
            foreach (var obj in targets)
                if (obj != null && obj.activeSelf) return true;
            return false;
        }

        /// <summary>Get list of all outfits with their current active state.</summary>
        public System.Collections.Generic.List<OutfitInfo> GetOutfitStatus()
        {
            var result = new System.Collections.Generic.List<OutfitInfo>();
            foreach (var comp in FindObjectsByType<MonoBehaviour>(FindObjectsInactive.Exclude, FindObjectsSortMode.None))
            {
                var t = comp.GetType();
                if (t.Name == "MEClothes")
                {
                    var entriesField = t.GetField("entries");
                    if (entriesField == null) continue;
                    var entries = entriesField.GetValue(comp) as System.Array;
                    if (entries == null) continue;

                    for (int i = 0; i < entries.Length; i++)
                    {
                        var entry = entries.GetValue(i);
                        if (entry == null) continue;

                        string name = GetOutfitName(entries, i);
                        var gameObjectsField = entry.GetType().GetField("gameObjects");
                        bool isActive = false;
                        if (gameObjectsField != null)
                        {
                            var objs = gameObjectsField.GetValue(entry) as GameObject[];
                            isActive = IsAnyGameObjectActive(objs);
                        }
                        result.Add(new OutfitInfo { index = i, name = name, isActive = isActive });
                    }
                    break;
                }
            }
            return result;
        }

        /// <summary>Build a runtime manual describing every avatar command + current state.</summary>
        public string GetManual()
        {
            RefreshReferences();

            var sb = new System.Text.StringBuilder();
            sb.AppendLine("# Mate Engine Avatar — Command Manual");
            sb.AppendLine();

            // --- Avatar identity ---
            string avatarName = "unknown";
            var loader = FindFirstObjectByType<VRMLoader>();
            if (loader != null)
            {
                var model = loader.GetCurrentModel();
                if (model != null) avatarName = model.name;
            }
            sb.AppendLine($"## Current avatar: {avatarName}");
            sb.AppendLine();

            // --- Commands ---
            sb.AppendLine("## Commands");
            sb.AppendLine();

            sb.AppendLine("### avatar.show_bubble");
            sb.AppendLine("Show a speech bubble above the avatar's head.");
            sb.AppendLine("Params: `{ \"text\": \"<string>\" }`");
            sb.AppendLine("The bubble auto-hides after ~0.15s per character (min 3s).");
            sb.AppendLine("Use for avatar inner thoughts, reactions, and short responses.");
            sb.AppendLine();

            // Dances
            sb.AppendLine("### avatar.play_dance");
            sb.AppendLine("Play a dance animation by index or title.");
            sb.AppendLine("Params: `{ \"index\": <int> }` OR `{ \"title\": \"<string>\" }`");
            if (_danceHandler != null)
            {
                var dances = _danceHandler.GetDanceList();
                if (dances.Count > 0)
                {
                    sb.AppendLine($"Available dances ({dances.Count} total):");
                    foreach (var (idx, id) in dances)
                        sb.AppendLine($"  - index {idx}: \"{id}\"");
                }
                else
                {
                    sb.AppendLine("No dance clips loaded yet.");
                }
            }
            else
            {
                sb.AppendLine("Dance handler not ready.");
            }
            sb.AppendLine();

            sb.AppendLine("### avatar.stop_dance");
            sb.AppendLine("Stop the currently playing dance and return to idle.");
            sb.AppendLine("Params: none");
            sb.AppendLine();

            sb.AppendLine("### avatar.set_idle");
            sb.AppendLine("Switch the avatar's idle animation.");
            sb.AppendLine("Params: `{ \"index\": <int> }` (0 = default standing idle)");
            sb.AppendLine();

            sb.AppendLine("### avatar.snap_to_window");
            sb.AppendLine("Move the avatar to sit on top of a window.");
            sb.AppendLine("Params: `{ \"title\": \"<window title substring>\" }`");
            sb.AppendLine("Use avatar.windows first to get a list of visible windows.");
            sb.AppendLine();

            sb.AppendLine("### avatar.unsnap");
            sb.AppendLine("Release the avatar from any window it is sitting on.");
            sb.AppendLine("Params: none");
            sb.AppendLine();

            sb.AppendLine("### avatar.set_talking");
            sb.AppendLine("Trigger or stop the talking mouth animation.");
            sb.AppendLine("Params: `{ \"value\": true|false }`");
            sb.AppendLine();

            sb.AppendLine("### avatar.play_expression");
            sb.AppendLine("Request a facial expression by blendshape name.");
            sb.AppendLine("Params: `{ \"name\": \"<expression name>\" }`");
            sb.AppendLine("Common names: Joy, Angry, Sorrow, Fun, Blink, Neutral");
            sb.AppendLine();

            sb.AppendLine("### avatar.status");
            sb.AppendLine("Get current avatar state (position, is_dancing, is_idle, etc.).");
            sb.AppendLine("Params: none");
            sb.AppendLine();

            sb.AppendLine("### avatar.windows");
            sb.AppendLine("List all visible desktop windows (title + bounds).");
            sb.AppendLine("Params: none");
            sb.AppendLine("Use this before avatar.snap_to_window to find the exact title.");
            sb.AppendLine();

            sb.AppendLine("### avatar.chat_input");
            sb.AppendLine("Toggle the on-screen chat input overlay for the user.");
            sb.AppendLine("Params: none");
            sb.AppendLine();

            sb.AppendLine("### avatar.manual");
            sb.AppendLine("Return this manual. Call it at the start of a session.");
            sb.AppendLine("Params: none");
            sb.AppendLine();

            sb.AppendLine("### avatar.walk_to");
            sb.AppendLine("Walk to a specific screen X coordinate.");
            sb.AppendLine("Params: `{ \"x\": <int> }` (screen pixel coordinate)");
            sb.AppendLine("Example: `{ \"x\": 500 }` walks to x=500 on screen");
            sb.AppendLine();

            sb.AppendLine("### avatar.go_away");
            sb.AppendLine("走开 — Walk to the nearest screen edge.");
            sb.AppendLine("Params: none");
            sb.AppendLine();

            sb.AppendLine("### avatar.get_lost");
            sb.AppendLine("滚 — Walk off-screen (partially hidden).");
            sb.AppendLine("Params: none");
            sb.AppendLine();

            // --- Usage notes ---
            sb.AppendLine("## Notes");
            sb.AppendLine("- Always call avatar.status first to understand current state.");
            sb.AppendLine("- Call avatar.windows before snap_to_window.");
            sb.AppendLine("- show_bubble is the primary way to give the user feedback.");
            sb.AppendLine("- Dances play until finished or until avatar.stop_dance is called.");
            sb.AppendLine("- set_talking(true) + show_bubble + set_talking(false) simulates speech.");

            return sb.ToString();
        }

        /// <summary>Get current avatar status for the HTTP API.</summary>
        public AvatarStatus GetStatus()
        {
            RefreshReferences();
            var status = new AvatarStatus();

            if (_animator != null)
            {
                status.has_avatar = true;
                status.position_x = _animator.transform.position.x;
                status.position_y = _animator.transform.position.y;
                status.is_dancing = _danceHandler != null && _danceHandler.IsPlaying;
                status.is_idle = _animController != null && _animController.isIdle;
                status.is_walking = _locomotion != null && _locomotion.IsWalking;
            }

            // Get outfit status
            status.outfits = GetOutfitStatus();
            status.active_outfits = status.outfits.FindAll(o => o.isActive).ConvertAll(o => o.name);

            // Get snapped window title
            status.snapped_window = _windowHandler?.GetSnappedWindowTitle() ?? "";

            return status;
        }
    }

    [Serializable]
    public class OutfitInfo
    {
        public int index;
        public string name;
        public bool isActive;
    }

    [Serializable]
    public class AvatarStatus
    {
        public bool has_avatar;
        public float position_x;
        public float position_y;
        public bool is_dancing;
        public bool is_idle;
        public bool is_walking;
        public string current_expression = "";
        public string snapped_window = "";
        public System.Collections.Generic.List<OutfitInfo> outfits = new();
        public System.Collections.Generic.List<string> active_outfits = new();
    }
}
