using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Imaging;

namespace EchoDeck;

/// <summary>
/// Asks Windows Explorer for the same thumbnail it shows in a folder (video frame, album art). Returns null when Windows
/// has no thumbnail for the file, and the library falls back to the icon tile.
/// </summary>
internal static class ShellThumbnail
{
    [StructLayout(LayoutKind.Sequential)]
    private struct NativeSize { public int cx; public int cy; }

    [ComImport, Guid("bcc18b79-ba16-442f-80c4-8a59c30c463b"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellItemImageFactory
    {
        [PreserveSig]
        int GetImage(NativeSize size, int flags, out IntPtr hbitmap);
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = false)]
    private static extern void SHCreateItemFromParsingName(
        [MarshalAs(UnmanagedType.LPWStr)] string path, IntPtr bindContext, ref Guid riid,
        [MarshalAs(UnmanagedType.Interface)] out IShellItemImageFactory factory);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(IntPtr obj);

    private const int ThumbnailOnly = 0x08;
    private const int BiggerSizeOk = 0x01;

    /// <summary>Crops the black / transparent padding Explorer puts around art that isn't square.</summary>
    public static BitmapSource TrimBorders(BitmapSource source)
    {
        try
        {
            var bgra = new FormatConvertedBitmap(source, System.Windows.Media.PixelFormats.Bgra32, null, 0);
            int w = bgra.PixelWidth, h = bgra.PixelHeight;
            int stride = w * 4;
            var pixels = new byte[stride * h];
            bgra.CopyPixels(pixels, stride, 0);

            bool Filled(int x, int y)
            {
                int i = y * stride + x * 4;
                return pixels[i + 3] > 24 && (pixels[i] > 22 || pixels[i + 1] > 22 || pixels[i + 2] > 22);
            }

            int minX = w, minY = h, maxX = -1, maxY = -1;
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                    if (Filled(x, y))
                    {
                        if (x < minX) minX = x;
                        if (x > maxX) maxX = x;
                        if (y < minY) minY = y;
                        if (y > maxY) maxY = y;
                    }

            int cw = maxX - minX + 1, ch = maxY - minY + 1;
            if (maxX < 0 || cw < w / 4 || ch < h / 4) return source;
            if (cw >= w - 2 && ch >= h - 2) return source;

            var cropped = new CroppedBitmap(source, new Int32Rect(minX, minY, cw, ch));
            cropped.Freeze();
            return cropped;
        }
        catch
        {
            return source;
        }
    }

    /// <summary>The most characteristic colour of an image (vivid, not near-black or near-white), or null if it has none.</summary>
    public static System.Windows.Media.Color? DominantColor(BitmapSource source)
    {
        try
        {
            double scale = 24.0 / Math.Max(source.PixelWidth, source.PixelHeight);
            var small = new TransformedBitmap(source, new System.Windows.Media.ScaleTransform(scale, scale));
            var bgra = new FormatConvertedBitmap(small, System.Windows.Media.PixelFormats.Bgra32, null, 0);
            int w = bgra.PixelWidth, h = bgra.PixelHeight, stride = w * 4;
            var px = new byte[stride * h];
            bgra.CopyPixels(px, stride, 0);

            double sr = 0, sg = 0, sb = 0, sw = 0;
            for (int i = 0; i + 3 < px.Length; i += 4)
            {
                double b = px[i], g = px[i + 1], r = px[i + 2], a = px[i + 3];
                if (a < 40) continue;
                double max = Math.Max(r, Math.Max(g, b)), min = Math.Min(r, Math.Min(g, b));
                if (max < 50 || min > 225) continue;                     // skip black / white
                double weight = 0.15 + (max - min) / 255.0;              // favour saturated pixels
                sr += r * weight; sg += g * weight; sb += b * weight; sw += weight;
            }
            if (sw < 0.5) return null;

            double R = sr / sw, G = sg / sw, B = sb / sw;
            double peak = Math.Max(R, Math.Max(G, B));
            if (peak < 140) { double f = 140 / Math.Max(peak, 1); R *= f; G *= f; B *= f; }
            return System.Windows.Media.Color.FromRgb((byte)Math.Min(255, R), (byte)Math.Min(255, G), (byte)Math.Min(255, B));
        }
        catch
        {
            return null;
        }
    }

    public static BitmapSource? Get(string path, int width, int height, bool biggerOk = false)
    {
        try
        {
            var iid = new Guid("bcc18b79-ba16-442f-80c4-8a59c30c463b");
            SHCreateItemFromParsingName(path, IntPtr.Zero, ref iid, out var factory);

            int hr = factory.GetImage(new NativeSize { cx = width, cy = height }, ThumbnailOnly | (biggerOk ? BiggerSizeOk : 0), out var hbitmap);
            if (hr != 0 || hbitmap == IntPtr.Zero) return null;

            try
            {
                var source = Imaging.CreateBitmapSourceFromHBitmap(
                    hbitmap, IntPtr.Zero, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
                source.Freeze();
                return source;
            }
            finally
            {
                DeleteObject(hbitmap);
            }
        }
        catch
        {
            return null;
        }
    }
}
