using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shell;

namespace WidgetNotes;

// The desktop compositor owns this material. No screenshots, hide/show cycle,
// polling timer, or WPF pixel shader participates in backdrop rendering.
internal static class NativeGlass
{
    static readonly Dictionary<IntPtr, int> AcceptedStates = new();
    public static void Configure(Window window)
    {
        window.WindowStyle = WindowStyle.None;
        window.ResizeMode = ResizeMode.CanResize;
        WindowChrome.SetWindowChrome(window, new WindowChrome
        {
            CaptionHeight = 54,
            ResizeBorderThickness = new Thickness(6),
            GlassFrameThickness = new Thickness(-1),
            CornerRadius = new CornerRadius(16),
            UseAeroCaptionButtons = false
        });
        IntPtr configuredHandle = IntPtr.Zero;
        window.SourceInitialized += (_, _) => configuredHandle = new WindowInteropHelper(window).Handle;
        window.Closed += (_, _) => AcceptedStates.Remove(configuredHandle);
    }

    public static bool Apply(Window window, bool dark) => ApplyMode(window, dark, 2);

    // Mode 1 uses compositor blur during interactive move/resize.
    // Mode 0 is the documented system material, retained as a fallback.
    internal static bool ApplyMode(Window window, bool dark, int mode)
    {
        var hwnd = new WindowInteropHelper(window).Handle;
        if (hwnd == IntPtr.Zero) return false;
        int immersive = dark ? 1 : 0, round = 2, backdrop = mode == 0 ? 3 : 1;
        _ = DwmSetWindowAttribute(hwnd, 20, ref immersive, 4);
        _ = DwmSetWindowAttribute(hwnd, 33, ref round, 4);
        bool available = false;
        if (!SystemParameters.HighContrast)
        {
            _ = SetAccent(hwnd, 0, dark);
            int systemResult = DwmSetWindowAttribute(hwnd, 38, ref backdrop, 4);
            available = mode == 0 ? systemResult == 0 : SetAccent(hwnd, mode == 1 ? 3 : 4, dark);
            if (!available && mode != 0) { backdrop = 3; available = DwmSetWindowAttribute(hwnd, 38, ref backdrop, 4) == 0; }
        }
        if (available)
        {
            if (HwndSource.FromHwnd(hwnd)?.CompositionTarget is { } target) target.BackgroundColor = Colors.Transparent;
            var margins = new Margins { Left = -1, Right = -1, Top = -1, Bottom = -1 };
            _ = DwmExtendFrameIntoClientArea(hwnd, ref margins);
            window.Background = Brushes.Transparent;
        }
        else window.Background = new SolidColorBrush(dark ? Color.FromRgb(24, 33, 48) : Color.FromRgb(241, 245, 250));
        return available;
    }

    static bool SetAccent(IntPtr hwnd, int state, bool dark)
    {
        var policy = new AccentPolicy { State = state, Flags = 2, GradientColor = unchecked((int)(dark ? 0x902B2117u : 0xA0F9F6F2u)) };
        IntPtr memory = Marshal.AllocHGlobal(Marshal.SizeOf<AccentPolicy>());
        try
        {
            Marshal.StructureToPtr(policy, memory, false);
            var data = new CompositionData { Attribute = 19, Data = memory, Size = Marshal.SizeOf<AccentPolicy>() };
            bool accepted = SetWindowCompositionAttribute(hwnd, ref data) != 0;
            if (accepted) AcceptedStates[hwnd] = state;
            return accepted;
        }
        catch (EntryPointNotFoundException) { return false; }
        finally { Marshal.FreeHGlobal(memory); }
    }

    public static int Backdrop(Window window)
    {
        _ = DwmGetWindowAttribute(new WindowInteropHelper(window).Handle, 38, out int value, 4);
        return value;
    }

    public static int AcceptedAccentState(Window window) => AcceptedStates.GetValueOrDefault(new WindowInteropHelper(window).Handle, -1);

    public static bool IsTopLevel(Window window) => GetParent(new WindowInteropHelper(window).Handle) == IntPtr.Zero;

    [StructLayout(LayoutKind.Sequential)] struct Margins { public int Left, Right, Top, Bottom; }
    [StructLayout(LayoutKind.Sequential)] struct AccentPolicy { public int State, Flags, GradientColor, AnimationId; }
    [StructLayout(LayoutKind.Sequential)] struct CompositionData { public int Attribute; public IntPtr Data; public int Size; }
    [DllImport("user32.dll")] static extern int SetWindowCompositionAttribute(IntPtr hwnd, ref CompositionData data);
    [DllImport("dwmapi.dll")] static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
    [DllImport("dwmapi.dll")] static extern int DwmGetWindowAttribute(IntPtr hwnd, int attribute, out int value, int size);
    [DllImport("dwmapi.dll")] static extern int DwmExtendFrameIntoClientArea(IntPtr hwnd, ref Margins margins);
    [DllImport("user32.dll")] static extern IntPtr GetParent(IntPtr hwnd);
}
