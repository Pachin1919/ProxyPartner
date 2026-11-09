using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;

public static class DesktopWindows
{
    [StructLayout(LayoutKind.Sequential)]
    public struct Rect { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)]
    public struct Scroll
    {
        public uint Size, Mask;
        public int Min, Max;
        public uint Page;
        public int Position, Track;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct Point { public int X, Y; }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct MonitorInfo
    {
        public int Size;
        public Rect Bounds, Work;
        public uint Flags;
    }
    private delegate bool WindowCallback(IntPtr hwnd, IntPtr data);
    private delegate bool MonitorCallback(IntPtr handle, IntPtr dc, IntPtr rect, IntPtr data);
    [DllImport("user32")] private static extern bool EnumWindows(WindowCallback callback, IntPtr data);
    [DllImport("user32", CharSet=CharSet.Unicode)] private static extern int GetClassName(IntPtr hwnd, StringBuilder name, int size);
    [DllImport("user32")] private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out int pid);
    [DllImport("user32")] public static extern bool IsWindow(IntPtr hwnd);
    [DllImport("user32")] public static extern bool IsWindowVisible(IntPtr hwnd);
    [DllImport("user32")] public static extern IntPtr GetDlgItem(IntPtr hwnd, int id);
    [DllImport("user32")] public static extern uint GetDpiForWindow(IntPtr hwnd);
    [DllImport("user32")] public static extern bool GetWindowRect(IntPtr hwnd, out Rect rect);
    [DllImport("user32")] public static extern bool GetClientRect(IntPtr hwnd, out Rect rect);
    [DllImport("user32")] private static extern bool ClientToScreen(IntPtr hwnd, ref Point point);
    [DllImport("user32")] private static extern bool GetScrollInfo(IntPtr hwnd, int bar, ref Scroll info);
    [DllImport("user32")] public static extern bool PrintWindow(IntPtr hwnd, IntPtr dc, uint flags);
    [DllImport("user32")] public static extern IntPtr SetThreadDpiAwarenessContext(IntPtr context);
    [DllImport("user32")] public static extern bool MoveWindow(IntPtr hwnd, int x, int y, int width, int height, bool repaint);
    [DllImport("user32")] public static extern uint GetGuiResources(IntPtr process, uint flags);
    [DllImport("user32")] private static extern bool EnumDisplayMonitors(IntPtr dc, IntPtr rect, MonitorCallback callback, IntPtr data);
    [DllImport("user32", CharSet=CharSet.Unicode)] private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);
    [DllImport("user32", CharSet=CharSet.Unicode)] public static extern IntPtr SendMessage(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);
    [DllImport("user32", CharSet=CharSet.Unicode)] public static extern IntPtr SendMessage(IntPtr hwnd, uint msg, IntPtr wParam, string lParam);
    [DllImport("user32", CharSet=CharSet.Unicode)] private static extern IntPtr SendMessage(IntPtr hwnd, uint msg, IntPtr wParam, StringBuilder lParam);
    [DllImport("user32", EntryPoint="SendMessageW")] private static extern IntPtr SendDpi(IntPtr hwnd, uint msg, IntPtr wParam, ref Rect lParam);
    [DllImport("user32")] public static extern bool PostMessage(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);
    [DllImport("user32")] private static extern bool GetCursorPos(out Point point);
    [DllImport("user32")] private static extern bool SetCursorPos(int x, int y);
    [DllImport("user32")] private static extern void mouse_event(uint flags, uint dx, uint dy, uint data, UIntPtr extra);

    public static IntPtr Find(int pid, string className, bool visible)
    {
        IntPtr result = IntPtr.Zero;
        EnumWindows((hwnd, data) =>
        {
            GetWindowThreadProcessId(hwnd, out int owner);
            if (owner != pid || (visible && !IsWindowVisible(hwnd))) return true;
            var cls = new StringBuilder(128);
            GetClassName(hwnd, cls, cls.Capacity);
            if (!string.IsNullOrEmpty(className) && !string.Equals(cls.ToString(), className, StringComparison.Ordinal)) return true;
            result = hwnd;
            return false;
        }, IntPtr.Zero);
        return result;
    }

    public static bool BelongsTo(int pid, IntPtr window)
    {
        return window != IntPtr.Zero && IsWindowVisible(window) &&
            GetWindowThreadProcessId(window, out int owner) != 0 && owner == pid;
    }

    public static Scroll ReadScroll(IntPtr window)
    {
        var result = new Scroll { Size = (uint)Marshal.SizeOf<Scroll>(), Mask = 7 };
        if (!GetScrollInfo(window,1,ref result)) throw new InvalidOperationException("Cannot read vertical scroll info.");
        return result;
    }

    public static void Click(int x, int y)
    {
        if (!GetCursorPos(out Point previous) || !SetCursorPos(x,y))
            throw new InvalidOperationException("Could not position the pointer for the identified control.");
        mouse_event(2, 0, 0, 0, UIntPtr.Zero);
        mouse_event(4, 0, 0, 0, UIntPtr.Zero);
        if (GetCursorPos(out Point current) && current.X == x && current.Y == y)
            SetCursorPos(previous.X, previous.Y);
    }

    public static Rect[] Displays()
    {
        var displays = new List<Rect>();
        EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (handle, dc, rect, data) =>
        {
            var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
            if (GetMonitorInfo(handle, ref info)) displays.Add(info.Work);
            return true;
        }, IntPtr.Zero);
        return displays.ToArray();
    }

    public static string Text(IntPtr hwnd)
    {
        int length = SendMessage(hwnd, 0xE, IntPtr.Zero, IntPtr.Zero).ToInt32();
        var result = new StringBuilder(Math.Clamp(length + 1, 512, 64 * 1024));
        SendMessage(hwnd, 0xD, (IntPtr)result.Capacity, result);
        return result.ToString();
    }

    public static bool ControlInClient(IntPtr parent, int id)
    {
        IntPtr control = GetDlgItem(parent, id);
        Point origin = default;
        return control != IntPtr.Zero && IsWindowVisible(control) &&
            GetWindowRect(control, out Rect child) && GetClientRect(parent, out Rect client) &&
            ClientToScreen(parent, ref origin) && child.Left >= origin.X && child.Top >= origin.Y &&
            child.Right <= origin.X + client.Right && child.Bottom <= origin.Y + client.Bottom;
    }

    public static void DpiMessage(int pid, IntPtr panel, int dpi)
    {
        GetWindowThreadProcessId(panel, out int owner);
        if (owner != pid || !GetWindowRect(panel, out Rect rect)) throw new InvalidOperationException("Unexpected panel.");
        // User32 marshals the system message's RECT to the window process.
        SendDpi(panel, 0x02E0, (IntPtr)(dpi | dpi << 16), ref rect);
    }
}
