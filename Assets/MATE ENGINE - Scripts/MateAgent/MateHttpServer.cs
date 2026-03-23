using System;
using System.Collections.Concurrent;
using System.IO;
using System.Net;
using System.Text;
using System.Threading;
using UnityEngine;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace MateEngine.Agent
{
    /// <summary>
    /// HTTP API server on localhost for external agent control.
    /// Runs HttpListener on a background thread, marshals to Unity main thread.
    /// </summary>
    public class MateHttpServer : MonoBehaviour
    {
        HttpListener _listener;
        Thread _listenerThread;
        volatile bool _running;

        readonly ConcurrentQueue<Action> _mainThreadQueue = new ConcurrentQueue<Action>();

        MateAgentConfig _config;
        AvatarActionDispatcher _dispatcher;
        DesktopEventMonitor _monitor;
        OpenClawNode _node;

        void Start()
        {
            _config = MateAgentConfig.Instance;
            _dispatcher = GetComponent<AvatarActionDispatcher>();
            _monitor = GetComponent<DesktopEventMonitor>();
            _node = GetComponent<OpenClawNode>();

            if (_config != null && _config.Settings.api_server.enabled)
                StartServer(_config.Settings.api_server.port);
        }

        void OnDestroy()
        {
            StopServer();
        }

        void OnApplicationQuit()
        {
            StopServer();
        }

        void Update()
        {
            while (_mainThreadQueue.TryDequeue(out var action))
            {
                try { action(); }
                catch (Exception e) { Debug.LogWarning($"[MateHttpServer] Main thread action error: {e.Message}"); }
            }
        }

        void StartServer(int port)
        {
            try
            {
                _listener = new HttpListener();
                _listener.Prefixes.Add($"http://localhost:{port}/");
                _listener.Prefixes.Add($"http://127.0.0.1:{port}/");
                _listener.Start();
                _running = true;

                _listenerThread = new Thread(ListenLoop) { IsBackground = true, Name = "MateHttpServer" };
                _listenerThread.Start();

                Debug.Log($"[MateHttpServer] Listening on http://localhost:{port}/");
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[MateHttpServer] Failed to start: {e.Message}");
            }
        }

        void StopServer()
        {
            _running = false;
            try { _listener?.Stop(); } catch { /* ignore */ }
            try { _listener?.Close(); } catch { /* ignore */ }
            _listener = null;
        }

        void ListenLoop()
        {
            while (_running)
            {
                try
                {
                    var ctx = _listener.GetContext();
                    ThreadPool.QueueUserWorkItem(_ => HandleRequest(ctx));
                }
                catch (HttpListenerException) { /* shutdown */ }
                catch (ObjectDisposedException) { break; }
                catch (Exception e)
                {
                    if (_running) Debug.LogWarning($"[MateHttpServer] Listen error: {e.Message}");
                }
            }
        }

        void HandleRequest(HttpListenerContext ctx)
        {
            var req = ctx.Request;
            var resp = ctx.Response;

            // CORS
            resp.AddHeader("Access-Control-Allow-Origin", "*");
            resp.AddHeader("Access-Control-Allow-Methods", "GET, POST, OPTIONS");
            resp.AddHeader("Access-Control-Allow-Headers", "Content-Type, Authorization");

            if (req.HttpMethod == "OPTIONS")
            {
                resp.StatusCode = 204;
                resp.Close();
                return;
            }

            // Auth check
            if (_config != null && !string.IsNullOrEmpty(_config.Settings.api_server.bearer_token))
            {
                string auth = req.Headers["Authorization"] ?? "";
                string expected = "Bearer " + _config.Settings.api_server.bearer_token;
                if (auth != expected)
                {
                    SendJson(resp, 401, new { error = "unauthorized" });
                    return;
                }
            }

            string path = req.Url.AbsolutePath.TrimEnd('/');

            try
            {
                switch (path)
                {
                    case "/api/status":
                        HandleStatus(req, resp);
                        break;
                    case "/api/windows":
                        HandleWindows(req, resp);
                        break;
                    case "/api/action":
                        HandleAction(req, resp);
                        break;
                    case "/api/chat":
                        HandleChat(req, resp);
                        break;
                    default:
                        SendJson(resp, 404, new { error = "not found", endpoints = new[] { "/api/status", "/api/windows", "/api/action", "/api/chat" } });
                        break;
                }
            }
            catch (Exception e)
            {
                SendJson(resp, 500, new { error = e.Message });
            }
        }

        void HandleStatus(HttpListenerRequest req, HttpListenerResponse resp)
        {
            AvatarStatus status = null;
            var done = new ManualResetEventSlim(false);

            _mainThreadQueue.Enqueue(() =>
            {
                status = _dispatcher != null ? _dispatcher.GetStatus() : new AvatarStatus();
                done.Set();
            });

            done.Wait(5000);
            SendJson(resp, 200, status ?? new AvatarStatus());
        }

        void HandleWindows(HttpListenerRequest req, HttpListenerResponse resp)
        {
            object result = null;
            var done = new ManualResetEventSlim(false);

            _mainThreadQueue.Enqueue(() =>
            {
                result = _monitor != null ? _monitor.GetVisibleWindows() : new System.Collections.Generic.List<WindowInfo>();
                done.Set();
            });

            done.Wait(5000);
            SendJson(resp, 200, result);
        }

        void HandleAction(HttpListenerRequest req, HttpListenerResponse resp)
        {
            if (req.HttpMethod != "POST")
            {
                SendJson(resp, 405, new { error = "method not allowed, use POST" });
                return;
            }

            string body = ReadBody(req);
            JObject json;
            try { json = JObject.Parse(body); }
            catch { SendJson(resp, 400, new { error = "invalid JSON" }); return; }

            string action = json["action"]?.ToString();
            string param = json["param"]?.ToString() ?? json["params"]?.ToString() ?? "";

            if (string.IsNullOrEmpty(action))
            {
                SendJson(resp, 400, new { error = "missing 'action' field" });
                return;
            }

            string result = null;
            var done = new ManualResetEventSlim(false);

            _mainThreadQueue.Enqueue(() =>
            {
                result = _dispatcher != null ? _dispatcher.Execute(action, param) : "error: dispatcher not available";
                done.Set();
            });

            done.Wait(5000);
            bool ok = result != null && result.StartsWith("ok");
            SendJson(resp, ok ? 200 : 400, new { result });
        }

        void HandleChat(HttpListenerRequest req, HttpListenerResponse resp)
        {
            if (req.HttpMethod != "POST")
            {
                SendJson(resp, 405, new { error = "method not allowed, use POST" });
                return;
            }

            string body = ReadBody(req);
            JObject json;
            try { json = JObject.Parse(body); }
            catch { SendJson(resp, 400, new { error = "invalid JSON" }); return; }

            string message = json["message"]?.ToString();
            if (string.IsNullOrEmpty(message))
            {
                SendJson(resp, 400, new { error = "missing 'message' field" });
                return;
            }

            if (_node == null || !_node.IsConnected)
            {
                SendJson(resp, 503, new { error = "OpenClaw gateway not connected" });
                return;
            }

            // Stream response as SSE, forwarding chat events from the gateway
            resp.ContentType = "text/event-stream";
            resp.StatusCode = 200;
            resp.SendChunked = true;

            var outputStream = resp.OutputStream;
            var done = new ManualResetEventSlim(false);
            string activeRunId = null;

            void WriteChunk(object data)
            {
                try
                {
                    byte[] bytes = Encoding.UTF8.GetBytes($"data: {JsonConvert.SerializeObject(data)}\n\n");
                    outputStream.Write(bytes, 0, bytes.Length);
                    outputStream.Flush();
                }
                catch { /* client disconnected */ }
            }

            Action<string, string> onDelta = (runId, text) =>
            {
                if (activeRunId != null && runId != activeRunId) return;
                WriteChunk(new { text, done = false });
            };

            Action<string, string, bool, string> onFinal = (runId, text, success, error) =>
            {
                if (activeRunId != null && runId != activeRunId) return;
                WriteChunk(new { text, done = true, success, error });
                done.Set();
            };

            _node.OnChatDelta += onDelta;
            _node.OnChatFinal += onFinal;

            try
            {
                var sendTask = _node.SendChatMessage(message);
                if (!sendTask.Wait(15000) || sendTask.Result == null)
                {
                    _node.OnChatDelta -= onDelta;
                    _node.OnChatFinal -= onFinal;
                    SendJson(resp, 503, new { error = "Failed to send message to OpenClaw" });
                    return;
                }
                activeRunId = sendTask.Result;
                done.Wait(60000);
            }
            finally
            {
                _node.OnChatDelta -= onDelta;
                _node.OnChatFinal -= onFinal;
            }

            try { resp.Close(); } catch { }
        }

        static string ReadBody(HttpListenerRequest req)
        {
            using var reader = new StreamReader(req.InputStream, req.ContentEncoding);
            return reader.ReadToEnd();
        }

        static void SendJson(HttpListenerResponse resp, int statusCode, object data)
        {
            resp.StatusCode = statusCode;
            resp.ContentType = "application/json";
            string json = JsonConvert.SerializeObject(data);
            byte[] buf = Encoding.UTF8.GetBytes(json);
            resp.ContentLength64 = buf.Length;
            resp.OutputStream.Write(buf, 0, buf.Length);
            resp.Close();
        }
    }
}
