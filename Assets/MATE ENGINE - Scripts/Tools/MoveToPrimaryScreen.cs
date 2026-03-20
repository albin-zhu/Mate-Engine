#if UNITY_STANDALONE_WIN || UNITY_EDITOR_WIN
using UnityEngine;
using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Debug = UnityEngine.Debug;

public class MoveToPrimaryScreen : MonoBehaviour
{
    private IntPtr unityHWND = IntPtr.Zero;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool MoveWindow(IntPtr hWnd, int X, int Y, int nWidth, int nHeight, bool bRepaint);

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromPoint(POINT pt, uint dwFlags);

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFO lpmi);

    private const uint MONITOR_DEFAULTTOPRIMARY = 1;

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int x;
        public int y;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    private struct MONITORINFO
    {
        public int cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public uint dwFlags;
    }

    void Start()
    {
        unityHWND = Process.GetCurrentProcess().MainWindowHandle;
    }

    public void MoveToPrimary()
    {
        if (unityHWND == IntPtr.Zero) return;

        if (!GetWindowRect(unityHWND, out RECT rect)) return;

        int currentWidth = rect.Right - rect.Left;
        int currentHeight = rect.Bottom - rect.Top;

        POINT origin = new POINT { x = 0, y = 0 };
        IntPtr hMonitor = MonitorFromPoint(origin, MONITOR_DEFAULTTOPRIMARY);
        MONITORINFO mi = new MONITORINFO();
        mi.cbSize = Marshal.SizeOf(typeof(MONITORINFO));
        if (!GetMonitorInfo(hMonitor, ref mi)) return;

        int boundsLeft = mi.rcMonitor.Left;
        int boundsTop = mi.rcMonitor.Top;
        int boundsWidth = mi.rcMonitor.Right - mi.rcMonitor.Left;
        int boundsHeight = mi.rcMonitor.Bottom - mi.rcMonitor.Top;

        int x = boundsLeft + (boundsWidth - currentWidth) / 2;
        int y = boundsTop + (boundsHeight - currentHeight) / 2;

        MoveWindow(unityHWND, x, y, currentWidth, currentHeight, true);

        Debug.Log($"[MoveToPrimaryScreen] moved window {currentWidth}x{currentHeight} to {x},{y}");
    }
}
#endif
