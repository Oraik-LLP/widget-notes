using System.Runtime.InteropServices;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shell;
using Wpf.Ui.Appearance;
using Wpf.Ui.Controls;

namespace WidgetNotes;

internal static class NativeGlass
{
    static bool resourcesReady;
    sealed class MaterialState { public bool KeptActive; public bool Paused; }
    static readonly ConditionalWeakTable<Window, MaterialState> States = new();
    public static bool KeepsActive(Window window) => States.TryGetValue(window, out var state) && state.KeptActive;
    public static void EnsureResources(bool dark)
    {
        if (resourcesReady) return;
        var dictionaries = System.Windows.Application.Current.Resources.MergedDictionaries;
        dictionaries.Add(new Wpf.Ui.Markup.ThemesDictionary { Theme = dark ? ApplicationTheme.Dark : ApplicationTheme.Light });
        dictionaries.Add(new Wpf.Ui.Markup.ControlsDictionary());
        resourcesReady = true;
    }

    public static void Configure(Window window)
    {
        if (window is not FluentWindow fluent) throw new ArgumentException("Glass windows must use FluentWindow.");
        fluent.ExtendsContentIntoTitleBar = true;
        fluent.WindowCornerPreference = WindowCornerPreference.Round;
        fluent.WindowBackdropType = WindowBackdropType.Acrylic;
        fluent.ResizeMode = ResizeMode.CanResize;
        window.SourceInitialized += (_, _) =>
        {
            if (WindowChrome.GetWindowChrome(window) is { } chrome) chrome.CaptionHeight = 54;
        };
        window.Deactivated += (_, _) => window.Dispatcher.BeginInvoke(() =>
        {
            if (States.TryGetValue(window, out var state) && state.KeptActive && !state.Paused) KeepActive(window, true);
        });
    }

    public static bool Apply(Window window, bool dark)
    {
        States.GetOrCreateValue(window).Paused = false;
        EnsureResources(dark);
        var theme = dark ? ApplicationTheme.Dark : ApplicationTheme.Light;
        if (ApplicationThemeManager.GetAppTheme() != theme) ApplicationThemeManager.Apply(theme, WindowBackdropType.Acrylic, false);
        if (window is FluentWindow fluent && fluent.WindowBackdropType != WindowBackdropType.Acrylic) fluent.WindowBackdropType = WindowBackdropType.Acrylic;
        if (SystemParameters.HighContrast || !WindowBackdrop.IsSupported(WindowBackdropType.Acrylic))
        {
            KeepActive(window, false);
            WindowBackdrop.RemoveBackdrop(window);
            window.Background = new SolidColorBrush(dark ? Color.FromRgb(25, 35, 50) : Color.FromRgb(240, 245, 251));
            return false;
        }
        bool enabled = WindowBackdrop.ApplyBackdrop(window, WindowBackdropType.Acrylic);
        if (enabled) { WindowBackdrop.RemoveBackground(window); WindowBackdrop.RemoveTitlebarBackground(window); }
        // A desktop widget spends most of its life unfocused. Keep this window's
        // compositor appearance active without taking keyboard focus from other apps.
        KeepActive(window, enabled);
        if (WindowChrome.GetWindowChrome(window) is { } chrome) chrome.CaptionHeight = 54;
        return enabled;
    }

    public static void Pause(Window window, bool dark)
    {
        States.GetOrCreateValue(window).Paused = true;
        // Keep FluentWindow's chrome intact throughout the system move/size loop.
        // Changing WindowBackdropType rebuilds WindowChrome and stalls that loop.
        var color = dark ? Color.FromRgb(29, 42, 59) : Color.FromRgb(235, 242, 250);
        window.Background = new SolidColorBrush(color);
        if (HwndSource.FromHwnd(new WindowInteropHelper(window).Handle)?.CompositionTarget is { } target) target.BackgroundColor = color;
        KeepActive(window, false);
        int none = 1;
        _ = DwmSetWindowAttribute(new WindowInteropHelper(window).Handle, 38, ref none, sizeof(int));
    }

    static void KeepActive(Window window, bool enabled)
    {
        IntPtr value = Marshal.AllocHGlobal(sizeof(int));
        try
        {
            Marshal.WriteInt32(value, enabled ? 1 : 0);
            var data = new CompositionData { Attribute = 15, Pointer = value, Size = sizeof(int) }; // WCA_FORCE_ACTIVEWINDOW_APPEARANCE
            bool accepted = SetWindowCompositionAttribute(new WindowInteropHelper(window).Handle, ref data) != 0;
            States.GetOrCreateValue(window).KeptActive = enabled && accepted;
            if (enabled && !window.IsActive) _ = DefWindowProc(new WindowInteropHelper(window).Handle, 0x86, new IntPtr(1), new IntPtr(-1));
        }
        catch (EntryPointNotFoundException) { States.GetOrCreateValue(window).KeptActive = false; }
        finally { Marshal.FreeHGlobal(value); }
    }

    public static int Backdrop(Window window)
    {
        _ = DwmGetWindowAttribute(new WindowInteropHelper(window).Handle, 38, out int value, 4);
        return value;
    }
    public static bool IsTopLevel(Window window)
    {
        var handle = new WindowInteropHelper(window).Handle;
        return handle != IntPtr.Zero && GetAncestor(handle, 2) == handle;
    }
    internal static void SendTestMessage(Window window, uint message)
    {
        if (App.TestRoot == null) throw new InvalidOperationException("Native message diagnostics require an isolated test root.");
        _ = SendMessage(new WindowInteropHelper(window).Handle, message, IntPtr.Zero, IntPtr.Zero);
    }
    [DllImport("user32.dll")] static extern IntPtr SendMessage(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam);
    [StructLayout(LayoutKind.Sequential)] struct CompositionData { public int Attribute; public IntPtr Pointer; public nuint Size; }
    [DllImport("user32.dll", SetLastError = true)] static extern int SetWindowCompositionAttribute(IntPtr hwnd, ref CompositionData data);
    [DllImport("user32.dll", CharSet=CharSet.Unicode)] static extern IntPtr DefWindowProc(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam);
    [DllImport("dwmapi.dll")] static extern int DwmGetWindowAttribute(IntPtr hwnd, int attribute, out int value, int size);
    [DllImport("dwmapi.dll")] static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
    [DllImport("user32.dll")] static extern IntPtr GetAncestor(IntPtr hwnd, uint flags);
}
