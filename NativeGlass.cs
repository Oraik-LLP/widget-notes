using System.Runtime.InteropServices;
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
    }

    public static bool Apply(Window window, bool dark)
    {
        EnsureResources(dark);
        var theme = dark ? ApplicationTheme.Dark : ApplicationTheme.Light;
        if (ApplicationThemeManager.GetAppTheme() != theme) ApplicationThemeManager.Apply(theme, WindowBackdropType.Acrylic, false);
        if (window is FluentWindow fluent && fluent.WindowBackdropType != WindowBackdropType.Acrylic) fluent.WindowBackdropType = WindowBackdropType.Acrylic;
        if (SystemParameters.HighContrast || !WindowBackdrop.IsSupported(WindowBackdropType.Acrylic))
        {
            WindowBackdrop.RemoveBackdrop(window);
            window.Background = new SolidColorBrush(dark ? Color.FromRgb(25, 35, 50) : Color.FromRgb(240, 245, 251));
            return false;
        }
        WindowBackdrop.RemoveBackground(window);
        bool enabled = WindowBackdrop.ApplyBackdrop(window, WindowBackdropType.Acrylic);
        WindowBackdrop.RemoveTitlebarBackground(window);
        if (WindowChrome.GetWindowChrome(window) is { } chrome) chrome.CaptionHeight = 54;
        return enabled;
    }

    public static void Pause(Window window, bool dark)
    {
        if (window is FluentWindow fluent) fluent.WindowBackdropType = WindowBackdropType.None;
        WindowBackdrop.RemoveBackdrop(window);
        window.Background = new SolidColorBrush(dark ? Color.FromRgb(29, 42, 59) : Color.FromRgb(235, 242, 250));
        if (WindowChrome.GetWindowChrome(window) is { } chrome) chrome.CaptionHeight = 54;
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
    [DllImport("dwmapi.dll")] static extern int DwmGetWindowAttribute(IntPtr hwnd, int attribute, out int value, int size);
    [DllImport("user32.dll")] static extern IntPtr GetAncestor(IntPtr hwnd, uint flags);
}
