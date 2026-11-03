using System;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace WidgetNotes;

internal static class AppArtwork
{
    private static readonly Uri IconUri = new("pack://application:,,,/Assets/WidgetNotes.ico", UriKind.Absolute);
    public static ImageSource WindowIcon { get; } = BitmapFrame.Create(IconUri);
    public static System.Drawing.Icon TrayIcon { get; } = LoadTrayIcon();

    private static System.Drawing.Icon LoadTrayIcon()
    {
        using var stream = Application.GetResourceStream(IconUri)!.Stream;
        using var source = new System.Drawing.Icon(stream, new System.Drawing.Size(32, 32));
        return (System.Drawing.Icon)source.Clone();
    }
}
