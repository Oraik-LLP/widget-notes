using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;

namespace WidgetNotes;

/// <summary>Hosts the compact window as an Explorer desktop child, never as a topmost window.</summary>
public sealed class DesktopAttachment(Window window)
{
    const long Child = 0x40000000, Popup = 0x80000000, ToolWindow = 0x80, AppWindow = 0x40000, Topmost = 8;
    const int Style = -16, ExStyle = -20;
    const uint NoActivate = 0x10, FrameChanged = 0x20, NoMove = 2, NoSize = 1;
    IntPtr hwnd, host;
    long originalStyle, originalExStyle;
    public bool IsAttached => hwnd != IntPtr.Zero && IsWindow(hwnd) && host != IntPtr.Zero && IsWindow(host) && GetParent(hwnd) == host;
    public string? LastError { get; private set; }
    public static string ClassName(IntPtr h) { var text = new StringBuilder(256); GetClassName(h, text, text.Capacity); return text.ToString(); }
    public static IntPtr FindHost()
    {
        // Explorer moves DefView between Progman and WorkerW on different Windows builds.
        IntPtr shell = GetShellWindow();
        IntPtr found = FindWindowEx(shell, IntPtr.Zero, "SHELLDLL_DefView", null);
        if (shell != IntPtr.Zero && found != IntPtr.Zero) return found;
        found = IntPtr.Zero;
        EnumWindows((top, _) =>
        {
            if (ClassName(top) is not ("Progman" or "WorkerW")) return true;
            found = FindWindowEx(top, IntPtr.Zero, "SHELLDLL_DefView", null);
            return found == IntPtr.Zero;
        }, IntPtr.Zero);
        return found;
    }
    public bool Attach(Rect bounds)
    {
        if (IsAttached) return true;
        hwnd = new WindowInteropHelper(window).Handle;
        if (hwnd == IntPtr.Zero || !IsWindow(hwnd)) return false;
        host = FindHost();
        if (host == IntPtr.Zero) { LastError = "Explorer's desktop is not ready. Retrying automatically."; return false; }
        originalStyle = GetWindowLongPtr(hwnd, Style).ToInt64(); originalExStyle = GetWindowLongPtr(hwnd, ExStyle).ToInt64();
        SetWindowLongPtr(hwnd, Style, new IntPtr((originalStyle | Child | 0x4000000) & ~Popup));
        SetWindowLongPtr(hwnd, ExStyle, new IntPtr((originalExStyle | ToolWindow) & ~(AppWindow | Topmost)));
        SetParent(hwnd, host);
        if (GetParent(hwnd) != host)
        {
            LastError = new Win32Exception(Marshal.GetLastWin32Error()).Message;
            SetWindowLongPtr(hwnd, Style, new IntPtr(originalStyle)); SetWindowLongPtr(hwnd, ExStyle, new IntPtr(originalExStyle)); host = IntPtr.Zero; return false;
        }
        Place(bounds); LastError = null; return true;
    }
    public Rect Bounds
    {
        get
        {
            if (!GetWindowRect(hwnd, out var r)) return new Rect(window.Left, window.Top, window.Width, window.Height);
            var dpi = VisualTreeHelper.GetDpi(window); return new Rect(r.Left / dpi.DpiScaleX, r.Top / dpi.DpiScaleY, (r.Right - r.Left) / dpi.DpiScaleX, (r.Bottom - r.Top) / dpi.DpiScaleY);
        }
    }
    public void Place(Rect bounds)
    {
        if (!IsAttached) return;
        var dpi = VisualTreeHelper.GetDpi(window);
        var origin = new PointI { X = (int)Math.Round(bounds.Left * dpi.DpiScaleX), Y = (int)Math.Round(bounds.Top * dpi.DpiScaleY) };
        MapWindowPoints(IntPtr.Zero, host, ref origin, 1);
        SetWindowPos(hwnd, IntPtr.Zero, origin.X, origin.Y, (int)Math.Round(bounds.Width * dpi.DpiScaleX), (int)Math.Round(bounds.Height * dpi.DpiScaleY), NoActivate | FrameChanged);
    }
    public Rect Detach()
    {
        var bounds = Bounds;
        if (hwnd == IntPtr.Zero || !IsWindow(hwnd) || host == IntPtr.Zero) return bounds;
        SetParent(hwnd, IntPtr.Zero);
        SetWindowLongPtr(hwnd, Style, new IntPtr(originalStyle & ~Child));
        SetWindowLongPtr(hwnd, ExStyle, new IntPtr(originalExStyle & ~Topmost));
        host = IntPtr.Zero;
        var dpi = VisualTreeHelper.GetDpi(window);
        SetWindowPos(hwnd, new IntPtr(-2), (int)Math.Round(bounds.Left * dpi.DpiScaleX), (int)Math.Round(bounds.Top * dpi.DpiScaleY), (int)Math.Round(bounds.Width * dpi.DpiScaleX), (int)Math.Round(bounds.Height * dpi.DpiScaleY), NoActivate | FrameChanged);
        return bounds;
    }
    public void KeepDesktopOrder() { if (IsAttached) SetWindowPos(hwnd, IntPtr.Zero, 0, 0, 0, 0, NoActivate | NoMove | NoSize); }
    public static bool IsChildOfDesktop(IntPtr h) => ClassName(GetParent(h)) == "SHELLDLL_DefView" && (GetWindowLongPtr(h, Style).ToInt64() & Child) != 0;
    public static bool IsTopmost(IntPtr h) => (GetWindowLongPtr(h, ExStyle).ToInt64() & Topmost) != 0;
    public static bool IsChild(IntPtr h) => (GetWindowLongPtr(h, Style).ToInt64() & Child) != 0;
    public static IntPtr RootWindow(IntPtr h) => GetAncestor(h, 2);
    public static IntPtr WindowAt(System.Windows.Point point) => WindowFromPoint(new PointI { X = (int)point.X, Y = (int)point.Y });
    [StructLayout(LayoutKind.Sequential)] public struct PointI { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] struct RectI { public int Left, Top, Right, Bottom; }
    delegate bool EnumProc(IntPtr h, IntPtr p);
    [DllImport("user32.dll")] static extern bool EnumWindows(EnumProc callback, IntPtr parameter);
    [DllImport("user32.dll")] static extern IntPtr GetShellWindow();
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern IntPtr FindWindowEx(IntPtr parent, IntPtr after, string name, string? title);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetClassName(IntPtr h, StringBuilder value, int length);
    [DllImport("user32.dll", SetLastError = true)] static extern IntPtr SetParent(IntPtr child, IntPtr parent);
    [DllImport("user32.dll")] static extern IntPtr GetParent(IntPtr h);
    [DllImport("user32.dll")] static extern IntPtr GetWindowLongPtr(IntPtr h, int index);
    [DllImport("user32.dll")] static extern IntPtr SetWindowLongPtr(IntPtr h, int index, IntPtr value);
    [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr h, out RectI rect);
    [DllImport("user32.dll")] static extern int MapWindowPoints(IntPtr from, IntPtr to, ref PointI point, int count);
    [DllImport("user32.dll")] static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int width, int height, uint flags);
    [DllImport("user32.dll")] static extern bool IsWindow(IntPtr h);
    [DllImport("user32.dll")] static extern IntPtr GetAncestor(IntPtr h, uint flags);
    [DllImport("user32.dll")] static extern IntPtr WindowFromPoint(PointI point);
}
