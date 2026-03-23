using System;
using System.Collections;
using System.Collections.Concurrent;
using System.IO;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace MateEngine.Agent
{
    /// <summary>
    /// Connects to the OpenClaw Gateway as a node, exposing avatar.* commands
    /// and using the gateway's chat API for AI conversations (like the Android app).
    /// </summary>
    public class OpenClawNode : MonoBehaviour
    {
        [Header("Gateway")]
        public string gatewayHost = "127.0.0.1";
        public int gatewayPort = 18789;
        public bool useTls = false;
        public bool autoConnect = true;

        [Header("Node Identity")]
        public string displayName = "Mate Engine";
        public string platform = "windows";

        ClientWebSocket _ws;
        CancellationTokenSource _cts;
        readonly ConcurrentQueue<Action> _mainThread = new ConcurrentQueue<Action>();
        volatile bool _connected;
        string _deviceId;
        string _deviceToken;
        string _connId;
        float _lastTick;
        float _tickInterval = 15f;
        int _reqCounter;

        // Pending RPC completions (method calls awaiting response)
        readonly ConcurrentDictionary<string, TaskCompletionSource<(bool ok, JObject payload, JObject error)>>
            _pending = new ConcurrentDictionary<string, TaskCompletionSource<(bool, JObject, JObject)>>();

        // Track active chat runId so HandleAgentEvent doesn't double-fire the same deltas
        volatile string _activeChatRunId;

        AvatarActionDispatcher _dispatcher;
        DesktopEventMonitor _monitor;
        MateAgentConfig _config;
        DeviceIdentity _deviceKeys;

        // Node commands we expose
        static readonly string[] NodeCommands = {
            "avatar.manual",
            "avatar.show_bubble",
            "avatar.play_dance",
            "avatar.stop_dance",
            "avatar.set_idle",
            "avatar.snap_to_window",
            "avatar.unsnap",
            "avatar.set_talking",
            "avatar.play_expression",
            "avatar.status",
            "avatar.windows",
            "avatar.chat_input"
        };

        static readonly string[] NodeCaps = { "avatar" };

        public bool IsConnected => _connected;

        // --- Chat streaming events (fired from receive-loop thread) ---

        /// <summary>Fired for each streaming text chunk. Args: (runId, partialText)</summary>
        public event Action<string, string> OnChatDelta;

        /// <summary>Fired when a chat run completes. Args: (runId, finalText, success, errorMessage)</summary>
        public event Action<string, string, bool, string> OnChatFinal;

        void Start()
        {
            _dispatcher = GetComponent<AvatarActionDispatcher>();
            _monitor = GetComponent<DesktopEventMonitor>();
            _config = MateAgentConfig.Instance;
            _deviceId = SystemInfo.deviceUniqueIdentifier;

            _deviceKeys = DeviceIdentity.Load();
            if (_deviceKeys != null)
                _deviceId = _deviceKeys.DeviceId;

            LoadNodeConfig();

            if (autoConnect)
                Connect();
        }

        void Update()
        {
            while (_mainThread.TryDequeue(out var action))
            {
                try { action(); }
                catch (Exception e) { Debug.LogWarning($"[OpenClawNode] Main thread error: {e.Message}"); }
            }

            if (_connected && Time.unscaledTime - _lastTick > _tickInterval * 3f)
            {
                Debug.LogWarning("[OpenClawNode] Tick timeout, reconnecting...");
                Disconnect();
                StartCoroutine(ReconnectAfterDelay(3f));
            }
        }

        void OnDestroy() => Disconnect();
        void OnApplicationQuit() => Disconnect();

        public void Connect()
        {
            if (_connected) return;
            _cts = new CancellationTokenSource();
            _ = ConnectAsync();
        }

        public void Disconnect()
        {
            _connected = false;
            FailPending();
            try { _cts?.Cancel(); } catch { }
            try
            {
                if (_ws?.State == WebSocketState.Open)
                    _ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "shutdown", CancellationToken.None).Wait(2000);
            }
            catch { }
            _ws?.Dispose();
            _ws = null;
        }

        IEnumerator ReconnectAfterDelay(float seconds)
        {
            yield return new WaitForSecondsRealtime(seconds);
            if (!_connected) Connect();
        }

        async Task ConnectAsync()
        {
            string scheme = useTls ? "wss" : "ws";
            string url = $"{scheme}://{gatewayHost}:{gatewayPort}/ws";
            try
            {
                _ws = new ClientWebSocket();
                _ws.Options.KeepAliveInterval = TimeSpan.FromSeconds(10);
                Debug.Log($"[OpenClawNode] Connecting to {url}...");
                await _ws.ConnectAsync(new Uri(url), _cts.Token);
                Debug.Log("[OpenClawNode] WebSocket connected, waiting for challenge...");
                _lastTick = Time.unscaledTime;
                _ = ReceiveLoop();
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[OpenClawNode] Connect failed: {e.Message}");
                _mainThread.Enqueue(() => StartCoroutine(ReconnectAfterDelay(10f)));
            }
        }

        async Task ReceiveLoop()
        {
            var buffer = new byte[65536];
            var sb = new StringBuilder();
            try
            {
                while (_ws?.State == WebSocketState.Open && !_cts.IsCancellationRequested)
                {
                    var result = await _ws.ReceiveAsync(new ArraySegment<byte>(buffer), _cts.Token);
                    if (result.MessageType == WebSocketMessageType.Close) break;

                    sb.Append(Encoding.UTF8.GetString(buffer, 0, result.Count));
                    if (!result.EndOfMessage) continue;

                    string msg = sb.ToString();
                    sb.Clear();
                    HandleMessage(msg);
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception e)
            {
                if (!_cts.IsCancellationRequested)
                    Debug.LogWarning($"[OpenClawNode] Receive error: {e.Message}");
            }

            _connected = false;
            FailPending();
            if (!_cts.IsCancellationRequested)
                _mainThread.Enqueue(() => StartCoroutine(ReconnectAfterDelay(5f)));
        }

        void HandleMessage(string raw)
        {
            JObject frame;
            try { frame = JObject.Parse(raw); }
            catch { return; }

            string type = frame["type"]?.ToString();

            if (type == "event")
            {
                string evt = frame["event"]?.ToString();
                JObject payload = frame["payload"] as JObject;
                if (payload == null && frame["payloadJSON"] is JValue pj)
                {
                    string ps = pj.ToString();
                    if (!string.IsNullOrEmpty(ps)) try { payload = JObject.Parse(ps); } catch { }
                }

                switch (evt)
                {
                    case "connect.challenge": _ = HandleChallenge(payload); break;
                    case "tick":              HandleTick(payload); break;
                    case "node.invoke.request": HandleInvokeRequest(payload); break;
                    case "chat":  HandleChatEvent(payload); break;
                    case "agent": HandleAgentEvent(payload); break;
                }
            }
            else if (type == "res")
            {
                string id = frame["id"]?.ToString();
                bool ok = frame["ok"]?.Value<bool>() ?? false;
                JObject payload = frame["payload"] as JObject;
                JObject error = frame["error"] as JObject;

                // Complete any awaiting RPC call
                if (id != null && _pending.TryRemove(id, out var tcs))
                {
                    tcs.TrySetResult((ok, payload, error));
                    return;
                }

                // Legacy: connect response
                if (payload?["type"]?.ToString() == "hello-ok")
                    HandleHelloOk(payload);
                else if (!ok)
                {
                    string errCode = error?["code"]?.ToString() ?? "unknown";
                    string errMsg = error?["message"]?.ToString() ?? "?";
                    Debug.LogWarning($"[OpenClawNode] RPC error: {errCode} - {errMsg}");
                }
            }
        }

        // ── Connect handshake ─────────────────────────────────────────────────

        async Task HandleChallenge(JObject payload)
        {
            string nonce = payload?["nonce"]?.ToString() ?? "";
            string authToken = GetGatewayToken();

            var connectParams = new JObject
            {
                ["minProtocol"] = 3,
                ["maxProtocol"] = 3,
                ["client"] = new JObject
                {
                    ["id"] = "openclaw-android",
                    ["displayName"] = displayName,
                    ["version"] = Application.version,
                    ["platform"] = platform,
                    ["deviceFamily"] = "desktop-pet",
                    ["mode"] = "ui"
                },
                ["role"] = "operator",
                ["scopes"] = new JArray("operator.read", "operator.write", "operator.talk.secrets"),
                ["caps"] = new JArray(NodeCaps),
                ["commands"] = new JArray(NodeCommands),
                ["permissions"] = new JObject { ["avatar.control"] = true }
            };

            // Android logic: prefer manual gateway token over stored deviceToken (auth key = "token" always)
            string selectedToken = !string.IsNullOrEmpty(authToken) ? authToken :
                                   !string.IsNullOrEmpty(_deviceToken) ? _deviceToken : "";
            var authObj = new JObject();
            if (!string.IsNullOrEmpty(selectedToken)) authObj["token"] = selectedToken;
            connectParams["auth"] = authObj;

            if (_deviceKeys != null)
            {
                long signedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                string scopes = "operator.read,operator.write,operator.talk.secrets";
                // Match Android: token in signature = same token as auth.token
                string sigPayload = $"v3|{_deviceKeys.DeviceId}|openclaw-android|ui|operator|{scopes}|{signedAt}|{selectedToken}|{nonce}|{platform.ToLower()}|desktop-pet";
                string signature = _deviceKeys.Sign(sigPayload);

                connectParams["device"] = new JObject
                {
                    ["id"] = _deviceKeys.DeviceId,
                    ["publicKey"] = _deviceKeys.PublicKeyBase64Url,
                    ["signature"] = signature,
                    ["signedAt"] = signedAt,
                    ["nonce"] = nonce
                };
            }

            await SendFrame(new JObject
            {
                ["type"] = "req",
                ["id"] = NextReqId(),
                ["method"] = "connect",
                ["params"] = connectParams
            });
        }

        void HandleHelloOk(JObject payload)
        {
            _connected = true;
            _connId = payload["server"]?["connId"]?.ToString() ?? "";
            _lastTick = Time.unscaledTime;

            var policy = payload["policy"] as JObject;
            if (policy != null)
            {
                int tickMs = policy["tickIntervalMs"]?.Value<int>() ?? 15000;
                _tickInterval = tickMs / 1000f;
            }

            string token = payload["auth"]?["deviceToken"]?.ToString();
            if (!string.IsNullOrEmpty(token)) { _deviceToken = token; SaveDeviceAuthToken(token); SaveNodeConfig(); }

            Debug.Log($"[OpenClawNode] Connected! connId={_connId}");
        }

        void HandleTick(JObject payload) => _lastTick = Time.unscaledTime;

        // ── Chat via OpenClaw gateway (like Android ChatController) ────────────

        // Operator role gets chat events automatically (no explicit subscribe needed)
        // Android NodeRuntime uses supportsChatSubscribe=false for operator sessions

        /// <summary>
        /// Send a chat message through OpenClaw gateway. Returns the runId on success, null on failure.
        /// Streaming responses arrive via OnChatDelta / OnChatFinal events.
        /// </summary>
        string BuildDesktopContext()
        {
            var monitor = FindFirstObjectByType<DesktopEventMonitor>();
            if (monitor == null) return null;

            var sb = new System.Text.StringBuilder("[context:");
            string fw = monitor.CurrentWindowTitle;
            if (!string.IsNullOrEmpty(fw)) sb.Append($" foreground=\"{fw}\"");
            if (monitor.IsIdle) sb.Append(", idle=true");

            var windows = monitor.GetVisibleWindows();
            if (windows != null && windows.Count > 0)
            {
                var titles = new System.Collections.Generic.List<string>();
                foreach (var w in windows)
                {
                    if (!string.IsNullOrEmpty(w.title) && w.title != fw)
                    {
                        titles.Add($"\"{w.title}\"");
                        if (titles.Count >= 4) break;
                    }
                }
                if (titles.Count > 0)
                    sb.Append($", open=[{string.Join(",", titles)}]");
            }
            sb.Append("]");
            return sb.ToString();
        }

        public async Task<string> SendChatMessage(string message, string sessionKey = "main", string thinking = "off")
        {
            if (!_connected)
            {
                Debug.LogWarning("[OpenClawNode] SendChatMessage: not connected");
                return null;
            }

            string ctx = BuildDesktopContext();
            if (!string.IsNullOrEmpty(ctx))
                message = message + "\n\n---\n" + ctx;

            string idempotencyKey = Guid.NewGuid().ToString();
            bool ok;
            JObject resPayload;
            JObject resError;
            try
            {
                (ok, resPayload, resError) = await Request("chat.send", new JObject
                {
                    ["sessionKey"] = sessionKey,
                    ["message"] = message,
                    ["thinking"] = thinking,
                    ["timeoutMs"] = 30000,
                    ["idempotencyKey"] = idempotencyKey
                }, 15000);
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[OpenClawNode] chat.send error: {e.Message}");
                return null;
            }

            if (!ok)
            {
                string errMsg = resError?["message"]?.ToString() ?? "chat.send failed";
                Debug.LogWarning($"[OpenClawNode] chat.send rejected: {errMsg}");
                return null;
            }

            return resPayload?["runId"]?.ToString() ?? idempotencyKey;
        }

        void HandleChatEvent(JObject payload)
        {
            if (payload == null) return;
            string runId = payload["runId"]?.ToString() ?? "";
            string state = payload["state"]?.ToString();

            switch (state)
            {
                case "delta":
                    // Register this runId as active so HandleAgentEvent skips the same deltas
                    _activeChatRunId = runId;
                    string deltaText = ExtractAssistantText(payload["message"] as JObject);
                    if (!string.IsNullOrEmpty(deltaText))
                    { string rd = runId, dt = deltaText; _mainThread.Enqueue(() => OnChatDelta?.Invoke(rd, dt)); }
                    break;

                case "final":
                    _activeChatRunId = null;
                    { string rf = runId; _mainThread.Enqueue(() => OnChatFinal?.Invoke(rf, "", true, null)); }
                    break;

                case "aborted":
                    _activeChatRunId = null;
                    { string ra = runId; _mainThread.Enqueue(() => OnChatFinal?.Invoke(ra, "", false, "aborted")); }
                    break;

                case "error":
                    _activeChatRunId = null;
                    string errMsg = payload["errorMessage"]?.ToString() ?? "Chat failed";
                    { string re = runId, em = errMsg; _mainThread.Enqueue(() => OnChatFinal?.Invoke(re, "", false, em)); }
                    break;
            }
        }

        void HandleAgentEvent(JObject payload)
        {
            if (payload == null) return;
            string stream = payload["stream"]?.ToString();
            if (stream != "assistant") return;

            string runId = payload["runId"]?.ToString() ?? "";
            // Skip if HandleChatEvent is already processing this run — prevents duplicate deltas
            if (!string.IsNullOrEmpty(_activeChatRunId) && _activeChatRunId == runId) return;

            string text = payload["data"]?["text"]?.ToString();
            if (string.IsNullOrEmpty(text)) return;

            { string r = runId, t = text; _mainThread.Enqueue(() => OnChatDelta?.Invoke(r, t)); }
        }

        static string ExtractAssistantText(JObject message)
        {
            if (message?["role"]?.ToString() != "assistant") return null;
            var content = message["content"] as JArray;
            if (content == null) return null;
            foreach (var item in content)
            {
                if (item is JObject obj && obj["type"]?.ToString() == "text")
                {
                    string t = obj["text"]?.ToString();
                    if (!string.IsNullOrEmpty(t)) return t;
                }
            }
            return null;
        }

        // ── Avatar command dispatch ───────────────────────────────────────────

        void HandleInvokeRequest(JObject payload)
        {
            string id = payload?["id"]?.ToString();
            string nodeId = payload?["nodeId"]?.ToString();
            string command = payload?["command"]?.ToString();
            string paramsJSON = payload?["paramsJSON"]?.ToString();

            JObject cmdParams = null;
            if (!string.IsNullOrEmpty(paramsJSON))
                try { cmdParams = JObject.Parse(paramsJSON); } catch { }

            _mainThread.Enqueue(() =>
            {
                string resultJson = DispatchCommand(command, cmdParams);
                _ = SendInvokeResult(id, nodeId, true, resultJson, null);
            });
        }

        string DispatchCommand(string command, JObject p)
        {
            if (_dispatcher == null)
                return JsonConvert.SerializeObject(new { error = "no dispatcher" });

            switch (command)
            {
                case "avatar.manual":
                    return JsonConvert.SerializeObject(new { manual = _dispatcher.GetManual() });
                case "avatar.show_bubble":
                    return JsonConvert.SerializeObject(new { result = _dispatcher.Execute("show_bubble", p?["text"]?.ToString() ?? p?["param"]?.ToString() ?? "") });
                case "avatar.play_dance":
                    return JsonConvert.SerializeObject(new { result = _dispatcher.Execute("play_dance", p?["index"]?.ToString() ?? p?["title"]?.ToString() ?? p?["param"]?.ToString() ?? "0") });
                case "avatar.stop_dance":
                    return JsonConvert.SerializeObject(new { result = _dispatcher.Execute("stop_dance") });
                case "avatar.set_idle":
                    return JsonConvert.SerializeObject(new { result = _dispatcher.Execute("set_idle", p?["index"]?.ToString() ?? p?["param"]?.ToString() ?? "0") });
                case "avatar.snap_to_window":
                    return JsonConvert.SerializeObject(new { result = _dispatcher.Execute("snap_to_window", p?["title"]?.ToString() ?? p?["param"]?.ToString() ?? "") });
                case "avatar.unsnap":
                    return JsonConvert.SerializeObject(new { result = _dispatcher.Execute("unsnap") });
                case "avatar.set_talking":
                    return JsonConvert.SerializeObject(new { result = _dispatcher.Execute("set_talking", p?["value"]?.ToString() ?? p?["param"]?.ToString() ?? "false") });
                case "avatar.play_expression":
                    return JsonConvert.SerializeObject(new { result = _dispatcher.Execute("play_expression", p?["name"]?.ToString() ?? p?["param"]?.ToString() ?? "") });
                case "avatar.status":
                    return JsonConvert.SerializeObject(_dispatcher.GetStatus());
                case "avatar.windows":
                    return JsonConvert.SerializeObject(_monitor != null ? _monitor.GetVisibleWindows() : new System.Collections.Generic.List<WindowInfo>());
                default:
                    return JsonConvert.SerializeObject(new { error = $"unknown command: {command}" });
            }
        }

        async Task SendInvokeResult(string invokeId, string nodeId, bool ok, string payloadJSON, string errorMsg)
        {
            var result = new JObject
            {
                ["type"] = "req",
                ["id"] = NextReqId(),
                ["method"] = "node.invoke.result",
                ["params"] = new JObject
                {
                    ["id"] = invokeId,
                    ["nodeId"] = nodeId ?? _deviceId,
                    ["ok"] = ok,
                    ["payloadJSON"] = payloadJSON
                }
            };

            if (!ok && !string.IsNullOrEmpty(errorMsg))
                result["params"]["error"] = new JObject { ["code"] = "COMMAND_FAILED", ["message"] = errorMsg };

            await SendFrame(result);
        }

        // ── RPC helpers ───────────────────────────────────────────────────────

        async Task<(bool ok, JObject payload, JObject error)> Request(string method, JObject parms, int timeoutMs)
        {
            string id = NextReqId();
            var tcs = new TaskCompletionSource<(bool, JObject, JObject)>(TaskCreationOptions.RunContinuationsAsynchronously);
            _pending[id] = tcs;

            await SendFrame(new JObject
            {
                ["type"] = "req",
                ["id"] = id,
                ["method"] = method,
                ["params"] = parms
            });

            using var cts = new CancellationTokenSource(timeoutMs);
            cts.Token.Register(() =>
            {
                if (_pending.TryRemove(id, out var t))
                    t.TrySetException(new TimeoutException($"{method} timeout after {timeoutMs}ms"));
            });

            return await tcs.Task;
        }

        void FailPending()
        {
            foreach (var kv in _pending)
                kv.Value.TrySetException(new InvalidOperationException("disconnected"));
            _pending.Clear();
        }

        // ── Node events ───────────────────────────────────────────────────────

        /// <summary>Send a node event to the gateway (e.g., desktop events).</summary>
        public async Task SendNodeEvent(string eventName, object payload)
        {
            if (!_connected) return;
            await SendFrame(new JObject
            {
                ["type"] = "req",
                ["id"] = NextReqId(),
                ["method"] = "node.event",
                ["params"] = new JObject
                {
                    ["event"] = eventName,
                    ["payloadJSON"] = JsonConvert.SerializeObject(payload)
                }
            });
        }

        async Task SendFrame(JObject frame)
        {
            if (_ws?.State != WebSocketState.Open) return;
            byte[] data = Encoding.UTF8.GetBytes(frame.ToString(Formatting.None));
            try { await _ws.SendAsync(new ArraySegment<byte>(data), WebSocketMessageType.Text, true, _cts.Token); }
            catch (Exception e) { Debug.LogWarning($"[OpenClawNode] Send error: {e.Message}"); }
        }

        string NextReqId() => $"me-{Interlocked.Increment(ref _reqCounter)}";

        // ── Config / persistence ──────────────────────────────────────────────

        string GetGatewayToken()
        {
            string token = Environment.GetEnvironmentVariable("OPENCLAW_GATEWAY_TOKEN");
            if (!string.IsNullOrEmpty(token)) return token;

            try
            {
                string openclawPath = System.IO.Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                    ".openclaw", "openclaw.json");
                if (System.IO.File.Exists(openclawPath))
                {
                    var oc = JObject.Parse(System.IO.File.ReadAllText(openclawPath));
                    string t = oc["gateway"]?["auth"]?["token"]?.ToString();
                    if (!string.IsNullOrEmpty(t)) return t;
                }
            }
            catch { }

            return _config?.Settings?.api_server?.bearer_token ?? "";
        }

        static string NodeConfigPath =>
            System.IO.Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                ".mateengine", "node.json");

        void SaveDeviceAuthToken(string newToken)
        {
            try
            {
                string deviceAuthPath = System.IO.Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                    ".openclaw", "identity", "device-auth.json");

                JObject da;
                if (System.IO.File.Exists(deviceAuthPath))
                    da = JObject.Parse(System.IO.File.ReadAllText(deviceAuthPath));
                else
                    da = new JObject { ["version"] = 1, ["deviceId"] = _deviceId, ["tokens"] = new JObject() };

                var tokens = da["tokens"] as JObject ?? new JObject();
                tokens["operator"] = new JObject
                {
                    ["token"] = newToken,
                    ["role"] = "operator",
                    ["updatedAtMs"] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
                };
                da["tokens"] = tokens;
                File.WriteAllText(deviceAuthPath, da.ToString(Formatting.Indented));
            }
            catch { }
        }

        void SaveNodeConfig()
        {
            try
            {
                File.WriteAllText(NodeConfigPath, new JObject
                {
                    ["deviceId"] = _deviceId,
                    ["deviceToken"] = _deviceToken,
                    ["gatewayHost"] = gatewayHost,
                    ["gatewayPort"] = gatewayPort
                }.ToString(Formatting.Indented));
            }
            catch { }
        }

        void LoadNodeConfig()
        {
            try
            {
                // Prefer operator token from device-auth.json (same as Android's deviceAuthStore per role)
                string deviceAuthPath = System.IO.Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                    ".openclaw", "identity", "device-auth.json");
                if (System.IO.File.Exists(deviceAuthPath))
                {
                    var da = JObject.Parse(System.IO.File.ReadAllText(deviceAuthPath));
                    string t = da["tokens"]?["operator"]?["token"]?.ToString();
                    if (!string.IsNullOrEmpty(t)) { _deviceToken = t; return; }
                }
            }
            catch { }

            try
            {
                if (!System.IO.File.Exists(NodeConfigPath)) return;
                var cfg = JObject.Parse(System.IO.File.ReadAllText(NodeConfigPath));
                _deviceToken = cfg["deviceToken"]?.ToString();
            }
            catch { }
        }
    }
}
