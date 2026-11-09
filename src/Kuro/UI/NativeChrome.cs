using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace Kuro.UI;
public static class NativeChrome
{
    [DllImport("dwmapi.dll")] static extern int DwmSetWindowAttribute(IntPtr window, int attribute, ref int value, int size);
    public static void Match(Window window)
    {
        var handle = new WindowInteropHelper(window).Handle; if (handle == IntPtr.Zero) return;
        int dark = Theme.Dark ? 1 : 0; DwmSetWindowAttribute(handle, 20, ref dark, 4);
        var c = Theme.Color("Panel"); int color = c.R | c.G << 8 | c.B << 16; DwmSetWindowAttribute(handle, 35, ref color, 4);
    }
}
