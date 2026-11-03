using System;
using System.Collections.Generic;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

if (args.Length != 2)
{
    Console.Error.WriteLine("Usage: IconBuilder <source.png> <destination.ico>");
    return 2;
}

string sourcePath = Path.GetFullPath(args[0]);
string destinationPath = Path.GetFullPath(args[1]);
BitmapFrame source;
using (var input = File.OpenRead(sourcePath))
{
    source = BitmapFrame.Create(input, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
    source.Freeze();
}

if (source.PixelWidth != source.PixelHeight)
    throw new InvalidDataException("The icon source must be square.");

var images = new List<(int Size, byte[] Data)>();
foreach (int size in new[] { 16, 20, 24, 32, 40, 48, 64, 96, 128, 256 })
{
    var visual = new DrawingVisual();
    RenderOptions.SetBitmapScalingMode(visual, BitmapScalingMode.HighQuality);
    using (var drawing = visual.RenderOpen())
        drawing.DrawImage(source, new Rect(0, 0, size, size));

    var bitmap = new RenderTargetBitmap(size, size, 96, 96, PixelFormats.Pbgra32);
    bitmap.Render(visual);
    bitmap.Freeze();

    var png = new PngBitmapEncoder();
    png.Frames.Add(BitmapFrame.Create(bitmap));
    using var data = new MemoryStream();
    png.Save(data);
    images.Add((size, data.ToArray()));
}

Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);
using var output = File.Create(destinationPath);
using var writer = new BinaryWriter(output);
writer.Write((ushort)0); // reserved
writer.Write((ushort)1); // icon resource
writer.Write((ushort)images.Count);

uint offset = (uint)(6 + 16 * images.Count);
foreach (var image in images)
{
    byte dimension = image.Size == 256 ? (byte)0 : (byte)image.Size;
    writer.Write(dimension);
    writer.Write(dimension);
    writer.Write((byte)0); // palette colors
    writer.Write((byte)0); // reserved
    writer.Write((ushort)1); // color planes
    writer.Write((ushort)32); // bits per pixel
    writer.Write((uint)image.Data.Length);
    writer.Write(offset);
    offset += (uint)image.Data.Length;
}

foreach (var image in images)
    writer.Write(image.Data);

Console.WriteLine($"Created {destinationPath} with {images.Count} sizes from {source.PixelWidth}×{source.PixelHeight} PNG.");
return 0;
