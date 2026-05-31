using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Runtime.InteropServices;

namespace BluSee.Tray;

/// <summary>
/// Generates the tray icon on the fly from the current battery percent. Each render allocates an
/// HICON via <see cref="Bitmap.GetHicon"/>, which must be released with DestroyIcon — otherwise the
/// process leaks GDI handles over its lifetime. We keep the previous handle and free it on swap.
/// </summary>
public sealed partial class TrayIconRenderer : IDisposable
{
    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DestroyIcon(IntPtr handle);

    private Icon? _previousIcon;
    private IntPtr _previousHandle;

    /// <summary>Render the percent into <paramref name="target"/>'s icon, freeing the previous one.</summary>
    public void Apply(NotifyIcon target, int? percent, bool lightTheme)
    {
        var size = SystemInformation.SmallIconSize;
        var dimension = Math.Max(16, size.Width);

        using var bitmap = new Bitmap(dimension, dimension);
        using (var g = Graphics.FromImage(bitmap))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
            g.Clear(Color.Transparent);
            Draw(g, dimension, percent, lightTheme);
        }

        var handle = bitmap.GetHicon();
        var icon = Icon.FromHandle(handle);
        target.Icon = icon;

        // Free the icon/handle from the previous render now that the new one is installed.
        _previousIcon?.Dispose();
        if (_previousHandle != IntPtr.Zero)
            DestroyIcon(_previousHandle);
        _previousIcon = icon;
        _previousHandle = handle;
    }

    private static void Draw(Graphics g, int dimension, int? percent, bool lightTheme)
    {
        var text = percent is null ? "?" : percent.Value >= 100 ? "F" : percent.Value.ToString();
        var color = percent switch
        {
            null => lightTheme ? Color.Gray : Color.Silver,
            <= 15 => Color.FromArgb(0xE8, 0x11, 0x23),  // red
            <= 40 => Color.FromArgb(0xFF, 0xB9, 0x00),  // amber
            _ => lightTheme ? Color.FromArgb(0x10, 0x7C, 0x10) : Color.FromArgb(0x6C, 0xCB, 0x5F), // green
        };

        var fontSize = text.Length >= 3 ? dimension * 0.42f : dimension * 0.62f;
        using var font = new Font("Segoe UI", fontSize, FontStyle.Bold, GraphicsUnit.Pixel);
        using var brush = new SolidBrush(color);
        using var format = new StringFormat
        {
            Alignment = StringAlignment.Center,
            LineAlignment = StringAlignment.Center,
        };
        g.DrawString(text, font, brush, new RectangleF(0, 0, dimension, dimension), format);
    }

    public void Dispose()
    {
        _previousIcon?.Dispose();
        if (_previousHandle != IntPtr.Zero)
            DestroyIcon(_previousHandle);
        _previousHandle = IntPtr.Zero;
    }
}
