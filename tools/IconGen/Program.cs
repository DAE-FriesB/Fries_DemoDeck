// Renders the "AppIcon" DrawingImage from a XAML resource dictionary into a multi-size .ico (PNG entries).
//
//   dotnet run --project tools/IconGen -- src/Ex.App/Themes/AppIcon.xaml src/Ex.App/Assets/app.ico
//
// Also writes <name>-256.png next to the .ico for previewing.

using System.IO;
using System.Windows;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Imaging;

internal static class Program
{
    private static readonly int[] Sizes = [16, 20, 24, 32, 40, 48, 64, 128, 256];

    [STAThread]
    private static int Main(string[] args)
    {
        if (args.Length != 2)
        {
            Console.Error.WriteLine("usage: IconGen <AppIcon.xaml> <out.ico>");
            return 1;
        }

        ResourceDictionary dictionary;
        using (var stream = File.OpenRead(args[0]))
            dictionary = (ResourceDictionary)XamlReader.Load(stream);
        var icon = (DrawingImage)dictionary["AppIcon"];

        // Convention: PNG for 256 px, classic 32-bit bitmaps for the small sizes (readable by every Windows API).
        var entries = Sizes.Select(size =>
        {
            var bitmap = Render(icon, size);
            return (size, size >= 256 ? EncodePng(bitmap) : EncodeDib(bitmap));
        }).ToList();
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(args[1]))!);
        WriteIco(args[1], entries);
        File.WriteAllBytes(Path.ChangeExtension(args[1], null) + "-256.png", entries[^1].Item2);
        Console.WriteLine($"Wrote {args[1]} ({string.Join(", ", Sizes)} px)");
        return 0;
    }

    private static BitmapSource Render(DrawingImage image, int size)
    {
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
            dc.DrawImage(image, new Rect(0, 0, size, size));
        var bitmap = new RenderTargetBitmap(size, size, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        // Icons store straight (not premultiplied) alpha.
        return new FormatConvertedBitmap(bitmap, PixelFormats.Bgra32, null, 0);
    }

    private static byte[] EncodePng(BitmapSource bitmap)
    {
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var ms = new MemoryStream();
        encoder.Save(ms);
        return ms.ToArray();
    }

    /// <summary>BITMAPINFOHEADER + bottom-up BGRA pixels + an empty AND mask (alpha does the masking).</summary>
    private static byte[] EncodeDib(BitmapSource bitmap)
    {
        var size = bitmap.PixelWidth;
        var stride = size * 4;
        var pixels = new byte[stride * size];
        bitmap.CopyPixels(pixels, stride, 0);
        var maskStride = (size + 31) / 32 * 4;

        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms);
        w.Write(40);              // header size
        w.Write(size);            // width
        w.Write(size * 2);        // height: colour + mask
        w.Write((short)1);        // planes
        w.Write((short)32);       // bits per pixel
        w.Write(0);               // BI_RGB
        w.Write(pixels.Length + maskStride * size);
        w.Write(0); w.Write(0); w.Write(0); w.Write(0);
        for (var y = size - 1; y >= 0; y--)
            w.Write(pixels, y * stride, stride);
        w.Write(new byte[maskStride * size]);
        w.Flush();
        return ms.ToArray();
    }

    /// <summary>ICO container: one directory entry per size, then the image data.</summary>
    private static void WriteIco(string path, IReadOnlyList<(int Size, byte[] Png)> images)
    {
        using var writer = new BinaryWriter(File.Create(path));
        writer.Write((short)0);              // reserved
        writer.Write((short)1);              // type: icon
        writer.Write((short)images.Count);

        var offset = 6 + 16 * images.Count;
        foreach (var (size, png) in images)
        {
            writer.Write((byte)(size >= 256 ? 0 : size)); // width (0 = 256)
            writer.Write((byte)(size >= 256 ? 0 : size)); // height
            writer.Write((byte)0);           // palette colours
            writer.Write((byte)0);           // reserved
            writer.Write((short)1);          // colour planes
            writer.Write((short)32);         // bits per pixel
            writer.Write(png.Length);
            writer.Write(offset);
            offset += png.Length;
        }
        foreach (var (_, png) in images)
            writer.Write(png);
    }
}
