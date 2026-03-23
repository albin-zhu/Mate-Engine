using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
using UnityEngine;
using Kirurobo;

namespace MateEngine.Agent
{
    public enum DesktopEventType
    {
        WindowSwitch,
        IdleStart,
        IdleEnd,
        MusicStart,
        MusicStop
    }

    public struct DesktopEvent
    {
        public DesktopEventType type;
        public string data;
        public float timestamp;
    }

    public class DesktopEventMonitor : MonoBehaviour
    {
        [Header("Polling")]
        public float pollInterval = 1.5f;

        public event Action<DesktopEvent> OnDesktopEvent;

        IntPtr _lastForegroundWindow;
        string _lastWindowTitle = "";
        bool _isIdle;
        float _nextPoll;

        int IdleThreshold => MateAgentConfig.Instance != null
            ? MateAgentConfig.Instance.Settings.events.idle_threshold_sec
            : 300;

        bool MonitorWindows => MateAgentConfig.Instance == null || MateAgentConfig.Instance.Settings.events.monitor_windows;
        bool MonitorIdle => MateAgentConfig.Instance == null || MateAgentConfig.Instance.Settings.events.monitor_idle;

        /// <summary>Current foreground window title, updated each poll.</summary>
        public string CurrentWindowTitle => _lastWindowTitle;
        public bool IsIdle => _isIdle;

        void Update()
        {
            if (Time.unscaledTime < _nextPoll) return;
            _nextPoll = Time.unscaledTime + pollInterval;

            if (MonitorWindows) PollForegroundWindow();
            if (MonitorIdle) PollIdle();
        }

        void PollForegroundWindow()
        {
            IntPtr fg = WinApi.GetForegroundWindow();
            if (fg == IntPtr.Zero || fg == _lastForegroundWindow) return;

            var sb = new StringBuilder(256);
            WinApi.GetWindowTextW(fg, sb, sb.Capacity);
            string title = sb.ToString();

            if (string.IsNullOrEmpty(title) || title == _lastWindowTitle) return;

            _lastForegroundWindow = fg;
            _lastWindowTitle = title;

            OnDesktopEvent?.Invoke(new DesktopEvent
            {
                type = DesktopEventType.WindowSwitch,
                data = title,
                timestamp = Time.unscaledTime
            });
        }

        void PollIdle()
        {
            var info = WinApi.LASTINPUTINFO.Create();
            if (!WinApi.GetLastInputInfo(ref info)) return;

            uint idleMs = WinApi.GetTickCount() - info.dwTime;
            float idleSec = idleMs / 1000f;

            if (!_isIdle && idleSec >= IdleThreshold)
            {
                _isIdle = true;
                OnDesktopEvent?.Invoke(new DesktopEvent
                {
                    type = DesktopEventType.IdleStart,
                    data = $"{idleSec:F0}s idle",
                    timestamp = Time.unscaledTime
                });
            }
            else if (_isIdle && idleSec < 5f)
            {
                _isIdle = false;
                OnDesktopEvent?.Invoke(new DesktopEvent
                {
                    type = DesktopEventType.IdleEnd,
                    data = "user returned",
                    timestamp = Time.unscaledTime
                });
            }
        }

        /// <summary>Enumerates visible desktop windows with titles. Called by HTTP API.</summary>
        public List<WindowInfo> GetVisibleWindows()
        {
            var result = new List<WindowInfo>();
            var sb = new StringBuilder(256);

            WinApi.EnumWindows((hWnd, lParam) =>
            {
                if (!WinApi.IsWindowVisible(hWnd)) return true;
                sb.Clear();
                WinApi.GetWindowTextW(hWnd, sb, sb.Capacity);
                string title = sb.ToString();
                if (string.IsNullOrEmpty(title)) return true;

                WinApi.GetWindowRect(hWnd, out var rect);
                result.Add(new WindowInfo
                {
                    hwnd = hWnd.ToInt64(),
                    title = title,
                    x = rect.left,
                    y = rect.top,
                    width = rect.right - rect.left,
                    height = rect.bottom - rect.top
                });
                return true;
            }, IntPtr.Zero);

            return result;
        }
    }

    [Serializable]
    public class WindowInfo
    {
        public long hwnd;
        public string title;
        public int x, y, width, height;
    }
}
