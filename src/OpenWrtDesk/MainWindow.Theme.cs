using Microsoft.UI.Xaml;
using System.Runtime.InteropServices;

namespace OpenWrtDesk;

public sealed partial class MainWindow
{
    private void ApplyTheme(string theme)
    {
        root.RequestedTheme = theme switch { "Dark" => ElementTheme.Dark, "Light" => ElementTheme.Light, _ => ElementTheme.Default };
        UpdateCaptionTheme();
    }

    private void UpdateCaptionTheme()
    {
        // The standard Win32 caption is outside the XAML visual tree.
        var dark = root.ActualTheme == ElementTheme.Dark ? 1 : 0;
        _ = DwmSetWindowAttribute(WinRT.Interop.WindowNative.GetWindowHandle(this), 20, ref dark, sizeof(int));
    }

    [DllImport("dwmapi.dll", ExactSpelling = true)]
    private static extern int DwmSetWindowAttribute(nint window, uint attribute, ref int value, int size);
}
