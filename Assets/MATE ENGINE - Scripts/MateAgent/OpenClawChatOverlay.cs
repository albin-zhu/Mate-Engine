using System.Collections;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using UnityEngine;
using UnityEngine.UI;

namespace MateEngine.Agent
{
    /// <summary>
    /// Danmaku overlay: messages scroll from right to left across the screen.
    /// AI replies type in while scrolling. Max 80 chars shown per message.
    /// Parents all entries directly to chatPanel's RectTransform.
    /// </summary>
    public class OpenClawChatOverlay : MonoBehaviour
    {
        [Header("Scene References")]
        public GameObject chatPanel;

        [Header("Danmaku")]
        public float scrollSpeed    = 200f;   // px / sec
        public float[] trackYs      = { 0.80f, 0.72f, 0.64f, 0.56f }; // normalized Y (0=bottom,1=top)
        public Color playerColor    = new Color(0.6f, 0.9f, 1f,  1f);
        public Color aiColor        = Color.white;
        public Color bgColor        = new Color(0f, 0f, 0f, 0.5f);
        public int   fontSize       = 24;
        public float entryHeight    = 38f;

        [Header("Typewriter")]
        public float typewriterSpeed = 40f;
        public int   maxDisplayChars = 80;

        [Header("Input")]
        public InputField chatInput;
        public Text       statusLabel;

        [Header("Inner OS Bubble")]
        public RectTransform bubbleCanvas;   // always-active canvas for show_bubble; if null, auto-found
        public Color  bubbleColor      = new Color(1f, 0.95f, 0.5f, 1f);
        public float  bubbleScrollSpeed = 80f;
        public float  bubbleTrackY     = 0.30f;  // normalized Y separate from main danmaku tracks

        // ── emotion map ──────────────────────────────────────────
        static readonly Dictionary<string, string> EmotionMap = new()
        {
            { "生气","Angry"  },{ "愤怒","Angry"  },
            { "开心","Joy"    },{ "高兴","Joy"    },{ "快乐","Joy"    },{ "笑","Joy"   },
            { "悲伤","Sorrow" },{ "难过","Sorrow" },{ "伤心","Sorrow" },{ "哭","Sorrow"},
            { "害羞","Fun"    },{ "撒娇","Fun"    },{ "可爱","Fun"    },
            { "Angry","Angry" },{ "Joy","Joy"     },{ "Sorrow","Sorrow"},{ "Fun","Fun" },
        };

        // ── state ────────────────────────────────────────────────
        OpenClawNode               _node;
        AvatarActionDispatcher     _dispatcher;
        PetVoiceReactionHandler    _voiceHandler;
        RectTransform              _canvasRT;
        RectTransform              _bubbleCanvasRT;

        readonly List<Entry> _entries = new();
        Entry  _aiEntry;
        string _accumulated  = "";
        bool   _streaming    = false;
        bool   _emotionDone  = false;
        int    _chunkIndex   = 0;   // which 80-char chunk _aiEntry is currently filling
        string _twTarget     = "";
        string _twCurrent    = "";
        Coroutine _twRoutine;

        // ── lifecycle ─────────────────────────────────────────────
        void Start()
        {
            _node         = GetComponent<OpenClawNode>();
            _dispatcher   = GetComponent<AvatarActionDispatcher>();
            _voiceHandler = FindFirstObjectByType<PetVoiceReactionHandler>();

            if (chatPanel != null)
            {
                chatPanel.SetActive(false);
                _canvasRT = chatPanel.GetComponent<RectTransform>();
            }

            // Bubble canvas: use assigned reference, or find any screen-space Canvas in scene
            if (bubbleCanvas != null)
                _bubbleCanvasRT = bubbleCanvas;
            else
            {
                var cv = FindFirstObjectByType<Canvas>();
                if (cv != null) _bubbleCanvasRT = cv.GetComponent<RectTransform>();
            }

            if (chatInput != null) chatInput.onEndEdit.AddListener(OnSubmit);

            if (_node != null)
            {
                _node.OnChatDelta += OnChatDelta;
                _node.OnChatFinal += OnChatFinal;
            }

            AvatarActionDispatcher.OnShowBubble += OnShowBubble;
        }

        void OnDestroy()
        {
            if (_node != null)
            {
                _node.OnChatDelta -= OnChatDelta;
                _node.OnChatFinal -= OnChatFinal;
            }
            AvatarActionDispatcher.OnShowBubble -= OnShowBubble;
        }

        void Update()
        {
            if (chatPanel != null && chatPanel.activeSelf && Input.GetKeyDown(KeyCode.Escape))
                HideChat();

            if (_entries.Count == 0) return;
            float w  = _canvasRT != null ? _canvasRT.rect.width : (_bubbleCanvasRT != null ? _bubbleCanvasRT.rect.width : 1920f);
            float dt = Time.unscaledDeltaTime;

            for (int i = _entries.Count - 1; i >= 0; i--)
            {
                var e = _entries[i];
                e.Tick(dt, scrollSpeed, w);
                if (e.Dead)
                {
                    if (e == _aiEntry) _aiEntry = null;
                    e.Destroy();
                    _entries.RemoveAt(i);
                }
            }
        }

        // ── public API ────────────────────────────────────────────
        public void ToggleChat()
        {
            if (chatPanel == null) return;
            if (chatPanel.activeSelf) HideChat(); else ShowChat();
        }

        public void ShowChat()
        {
            if (chatPanel == null) return;
            _dispatcher?.Execute("stop_dance", "");
            chatPanel.SetActive(true);
            _canvasRT = chatPanel.GetComponent<RectTransform>();
            UpdateStatus();
            if (chatInput != null) { chatInput.text = ""; chatInput.ActivateInputField(); chatInput.Select(); }
        }

        public void HideChat()
        {
            if (chatPanel == null) return;
            chatPanel.SetActive(false);
            StopTypewriter();
            for (int i = _entries.Count - 1; i >= 0; i--)
            {
                if (!_entries[i].isBubble) { _entries[i].Destroy(); _entries.RemoveAt(i); }
            }
            _aiEntry     = null;
            _accumulated = "";
            _streaming   = false;
            _emotionDone = false;
        }

        // ── input ─────────────────────────────────────────────────
        public void OnSubmit(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return;
            string msg = text.Trim();
            if (chatInput != null) chatInput.text = "";

            Spawn($"你: {msg}", playerColor);

            if (_node != null && _node.IsConnected)
            {
                _voiceHandler?.PlayAnyVoice();
                _dispatcher?.Execute("set_talking", "true");
                _streaming    = true;
                _accumulated  = "";
                _emotionDone  = false;
                _chunkIndex   = 0;
                StopTypewriter();
                _aiEntry = Spawn("小T: …", aiColor);
                _ = _node.SendChatMessage(msg);
            }
            else
            {
                Spawn("小T: 未连接", aiColor);
            }

            if (chatInput != null) { chatInput.ActivateInputField(); chatInput.Select(); }
        }

        // ── chat events ───────────────────────────────────────────
        void OnChatDelta(string runId, string delta)
        {
            if (!_streaming) return;
            _accumulated += delta;
            string display = StripEmotion(_accumulated);

            // Which chunk index should the current display end belong to?
            int neededChunk = display.Length > 0 ? (display.Length - 1) / maxDisplayChars : 0;

            if (neededChunk > _chunkIndex)
            {
                // Current entry is full — spawn a new one for the next chunk
                _chunkIndex = neededChunk;
                StopTypewriter();
                _aiEntry = Spawn("小T: …", aiColor);
            }

            // Extract only the current chunk's text
            int start = _chunkIndex * maxDisplayChars;
            string chunkText = display.Substring(start, Mathf.Min(maxDisplayChars, display.Length - start));
            TypewriterQueue("小T: " + chunkText);
        }

        void OnChatFinal(string runId, string fullText, bool success, string error)
        {
            _dispatcher?.Execute("set_talking", "false");
            _streaming  = false;
            _chunkIndex = 0;
            if (!success && !string.IsNullOrEmpty(error))
            {
                if (_aiEntry != null) _aiEntry.SetText($"小T: {error}");
            }
            // All text was already shown via deltas — nothing to re-process
            _accumulated = "";
            _emotionDone = false;
        }

        // ── inner OS bubble ───────────────────────────────────────
        void OnShowBubble(string text)
        {
            if (_bubbleCanvasRT == null) return;
            // Truncate long inner-OS monologues
            if (text.Length > maxDisplayChars) text = text.Substring(0, maxDisplayChars) + "…";
            SpawnBubble("💭 " + text);
        }

        void SpawnBubble(string text)
        {
            var rt = _bubbleCanvasRT;

            var go = new GameObject("DKBubble", typeof(RectTransform), typeof(CanvasGroup));
            go.transform.SetParent(rt, false);

            var goRT = go.GetComponent<RectTransform>();
            goRT.anchorMin = goRT.anchorMax = new Vector2(0f, bubbleTrackY);
            goRT.pivot     = new Vector2(0f, 0.5f);
            goRT.sizeDelta = new Vector2(0f, entryHeight);
            goRT.anchoredPosition = new Vector2(rt.rect.width + 40f, 0f);

            // bg
            var bg  = new GameObject("B", typeof(RectTransform), typeof(Image));
            bg.transform.SetParent(go.transform, false);
            var bgRT = bg.GetComponent<RectTransform>();
            bgRT.anchorMin = Vector2.zero; bgRT.anchorMax = Vector2.one;
            bgRT.offsetMin = bgRT.offsetMax = Vector2.zero;
            bg.GetComponent<Image>().color = new Color(0.12f, 0.10f, 0f, 0.72f);

            // text
            var tgo = new GameObject("T", typeof(RectTransform), typeof(Text));
            tgo.transform.SetParent(go.transform, false);
            var tRT = tgo.GetComponent<RectTransform>();
            tRT.anchorMin = Vector2.zero; tRT.anchorMax = Vector2.one;
            tRT.offsetMin = new Vector2(8, 0); tRT.offsetMax = new Vector2(-8, 0);
            var t = tgo.GetComponent<Text>();
            t.text      = text;
            t.font      = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            t.fontSize  = fontSize;
            t.color     = bubbleColor;
            t.alignment = TextAnchor.MiddleLeft;
            t.horizontalOverflow = HorizontalWrapMode.Overflow;
            t.verticalOverflow   = VerticalWrapMode.Overflow;
            t.fontStyle = FontStyle.Italic;

            // Size root to text width
            Canvas.ForceUpdateCanvases();
            goRT.sizeDelta = new Vector2(t.preferredWidth + 20f, entryHeight);

            var entry = new Entry { rt = goRT, label = t, scrollSpeed = bubbleScrollSpeed, isBubble = true };
            _entries.Add(entry);
        }

        // ── emotion ───────────────────────────────────────────────
        string StripEmotion(string text)
        {
            var m = Regex.Match(text, @"^\[([^\]]+)\][\s\u3000]*");
            if (!m.Success) return text;
            string tag = m.Groups[1].Value.Trim();
            if (!_emotionDone && EmotionMap.TryGetValue(tag, out string bs))
            {
                _emotionDone = true;
                _dispatcher?.Execute("play_expression", bs);
            }
            return text.Substring(m.Length);
        }

        // ── typewriter ────────────────────────────────────────────
        void TypewriterQueue(string target)
        {
            if (target.Length <= _twTarget.Length) return;
            _twTarget = target;
            if (_twRoutine == null)
                _twRoutine = StartCoroutine(TypewriterRoutine());
        }

        IEnumerator TypewriterRoutine()
        {
            float delay = 1f / Mathf.Max(1f, typewriterSpeed);
            while (_twCurrent.Length < _twTarget.Length)
            {
                _twCurrent = _twTarget.Substring(0, _twCurrent.Length + 1);
                if (_aiEntry != null) _aiEntry.SetText(_twCurrent);
                yield return new WaitForSecondsRealtime(delay);
            }
            _twRoutine = null;
        }

        void StopTypewriter()
        {
            if (_twRoutine != null) { StopCoroutine(_twRoutine); _twRoutine = null; }
            _twTarget = _twCurrent = "";
        }

        // ── spawn ─────────────────────────────────────────────────
        Entry Spawn(string text, Color col)
        {
            if (_canvasRT == null) return null;

            float y = trackYs.Length > 0 ? trackYs[0] : 0.80f;

            var go = new GameObject("DK", typeof(RectTransform), typeof(CanvasGroup));
            go.transform.SetParent(_canvasRT, false);

            var rt = go.GetComponent<RectTransform>();
            // Anchor at left edge, position y along height
            rt.anchorMin = rt.anchorMax = new Vector2(0f, y);
            rt.pivot     = new Vector2(0f, 0.5f);
            // Start just off the right edge
            rt.sizeDelta = new Vector2(0f, entryHeight);  // width auto from text
            rt.anchoredPosition = new Vector2(_canvasRT.rect.width + 40f, 0f);

            // bg
            var bg = new GameObject("B", typeof(RectTransform), typeof(Image));
            bg.transform.SetParent(go.transform, false);
            var bgRT = bg.GetComponent<RectTransform>();
            bgRT.anchorMin = Vector2.zero; bgRT.anchorMax = Vector2.one;
            bgRT.offsetMin = bgRT.offsetMax = Vector2.zero;
            bg.GetComponent<Image>().color = bgColor;

            // text
            var tgo = new GameObject("T", typeof(RectTransform), typeof(Text));
            tgo.transform.SetParent(go.transform, false);
            var tRT = tgo.GetComponent<RectTransform>();
            tRT.anchorMin = Vector2.zero; tRT.anchorMax = Vector2.one;
            tRT.offsetMin = new Vector2(8, 0); tRT.offsetMax = new Vector2(-8, 0);

            var t = tgo.GetComponent<Text>();
            t.text      = text;
            t.font      = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            t.fontSize  = fontSize;
            t.color     = col;
            t.alignment = TextAnchor.MiddleLeft;
            t.horizontalOverflow = HorizontalWrapMode.Overflow;  // single line
            t.verticalOverflow   = VerticalWrapMode.Overflow;

            // Size the root to match text width + padding
            var le = go.AddComponent<LayoutElement>();
            le.preferredHeight = entryHeight;

            var entry = new Entry { rt = rt, label = t };
            _entries.Add(entry);
            return entry;
        }

        void UpdateStatus()
        {
            if (statusLabel == null) return;
            bool ok = _node != null && _node.IsConnected;
            statusLabel.text  = ok ? "● OpenClaw" : "○ 未连接";
            statusLabel.color = ok ? new Color(0.4f, 0.9f, 0.4f) : new Color(0.9f, 0.4f, 0.4f);
        }

        // ── inner ─────────────────────────────────────────────────
        class Entry
        {
            public RectTransform rt;
            public Text          label;
            public bool          Dead;
            public bool          isBubble;            // inner-OS bubble — survives HideChat
            public float         scrollSpeed = -1f;   // -1 = use global scrollSpeed

            public void SetText(string t)
            {
                if (label) label.text = t;
                // Resize root width to match text
                if (rt) rt.sizeDelta = new Vector2(label.preferredWidth + 20f, rt.sizeDelta.y);
            }

            public void Tick(float dt, float globalSpeed, float canvasWidth)
            {
                if (rt == null) { Dead = true; return; }
                float spd = scrollSpeed >= 0f ? scrollSpeed : globalSpeed;
                var pos = rt.anchoredPosition;
                pos.x -= spd * dt;
                rt.anchoredPosition = pos;
                // Dead when fully off left edge
                if (pos.x + rt.sizeDelta.x < 0) Dead = true;
            }

            public void Destroy() { if (rt) Object.Destroy(rt.gameObject); }
        }
    }
}
