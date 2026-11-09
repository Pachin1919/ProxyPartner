using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;

public static class DesktopWindows
{
    [StructLayout(LayoutKind.Sequential)]
    public struct Rect { public int Left, Top, Right, Bottom; }
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
    [DllImport("user32")] public static extern bool PrintWindow(IntPtr hwnd, IntPtr dc, uint flags);
    [DllImport("user32")] public static extern IntPtr SetThreadDpiAwarenessContext(IntPtr context);
    [DllImport("user32")] public static extern bool MoveWindow(IntPtr hwnd, int x, int y, int width, int height, bool repaint);
    [DllImport("user32")] public static extern uint GetGuiResources(IntPtr process, uint flags);
    [DllImport("user32")] private static extern bool EnumDisplayMonitors(IntPtr dc, IntPtr rect, MonitorCallback callback, IntPtr data);
    [DllImport("user32", CharSet=CharSet.Unicode)] private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);
    [DllImport("user32", CharSet=CharSet.Unicode)] public static extern IntPtr SendMessage(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);
    [DllImport("user32", CharSet=CharSet.Unicode)] public static extern IntPtr SendMessage(IntPtr hwnd, uint msg, IntPtr wParam, string lParam);
    [DllImport("user32", CharSet=CharSet.Unicode)] private static extern IntPtr SendMessage(IntPtr hwnd, uint msg, IntPtr wParam, StringBuilder lParam);
    [DllImport("user32")] public static extern bool PostMessage(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);
    [DllImport("kernel32", SetLastError=true)] private static extern IntPtr OpenProcess(uint access, bool inherit, int pid);
    [DllImport("kernel32", SetLastError=true)] private static extern IntPtr VirtualAllocEx(IntPtr process, IntPtr address, UIntPtr bytes, uint type, uint protect);
    [DllImport("kernel32", SetLastError=true)] private static extern bool VirtualFreeEx(IntPtr process, IntPtr address, UIntPtr bytes, uint type);
    [DllImport("kernel32", SetLastError=true)] private static extern bool WriteProcessMemory(IntPtr process, IntPtr address, IntPtr buffer, UIntPtr bytes, out UIntPtr written);
    [DllImport("kernel32")] private static extern bool CloseHandle(IntPtr handle);

    public static IntPtr Find(int pid, string className, bool visible)
    {
        IntPtr result = IntPtr.Zero;
        EnumWindows((hwnd, data) =>
        {
            GetWindowThreadProcessId(hwnd, out int owner);
            if (owner != pid || (visible && !IsWindowVisible(hwnd))) return true;
            var cls = new StringBuilder(128);
            GetClassName(hwnd, cls, cls.Capacity);
            if (!string.Equals(cls.ToString(), className, StringComparison.Ordinal)) return true;
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
        var result = new StringBuilder(512);
        SendMessage(hwnd, 0xD, (IntPtr)result.Capacity, result);
        return result.ToString();
    }

    // A RECT argument belongs to the receiving process. Allocate it only in the
    // verified test app, send WM_DPICHANGED synchronously, then free it.
    public static void DpiMessage(int pid, IntPtr panel, int dpi)
    {
        GetWindowThreadProcessId(panel, out int owner);
        if (owner != pid || !GetWindowRect(panel, out Rect rect)) throw new InvalidOperationException("Unexpected panel.");
        int bytes = Marshal.SizeOf<Rect>();
        IntPtr process = OpenProcess(0x28, false, pid), remote = IntPtr.Zero, local = IntPtr.Zero;
        if (process == IntPtr.Zero) throw new Win32Exception();
        try
        {
            remote = VirtualAllocEx(process, IntPtr.Zero, (UIntPtr)bytes, 0x3000, 4);
            if (remote == IntPtr.Zero) throw new Win32Exception();
            local = Marshal.AllocHGlobal(bytes);
            Marshal.StructureToPtr(rect, local, false);
            if (!WriteProcessMemory(process, remote, local, (UIntPtr)bytes, out _)) throw new Win32Exception();
            SendMessage(panel, 0x02E0, (IntPtr)(dpi | dpi << 16), remote);
        }
        finally
        {
            if (local != IntPtr.Zero) Marshal.FreeHGlobal(local);
            if (remote != IntPtr.Zero) VirtualFreeEx(process, remote, UIntPtr.Zero, 0x8000);
            CloseHandle(process);
        }
    }
}
