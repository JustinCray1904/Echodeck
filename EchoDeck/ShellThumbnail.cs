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

    public static BitmapSource? Get(string path, int width, int height)
    {
        try
        {
            var iid = new Guid("bcc18b79-ba16-442f-80c4-8a59c30c463b");
            SHCreateItemFromParsingName(path, IntPtr.Zero, ref iid, out var factory);

            int hr = factory.GetImage(new NativeSize { cx = width, cy = height }, ThumbnailOnly | BiggerSizeOk, out var hbitmap);
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
