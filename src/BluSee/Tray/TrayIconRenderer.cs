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
            null => lightTheme ? Color.DimGray : Color.WhiteSmoke,
            <= 15 => lightTheme ? Color.FromArgb(0xE8, 0x11, 0x23) : Color.FromArgb(0xFF, 0x55, 0x5F), // red
            <= 40 => lightTheme ? Color.FromArgb(0xE0, 0x9A, 0x00) : Color.FromArgb(0xFF, 0xCC, 0x33), // amber
            _ => lightTheme ? Color.FromArgb(0x0E, 0x70, 0x0E) : Color.FromArgb(0x8C, 0xEC, 0x6F), // green
        };

        var font = FitFont(g, text, dimension);
        using var brush = new SolidBrush(color);
        using var format = new StringFormat
        {
            Alignment = StringAlignment.Center,
            LineAlignment = StringAlignment.Center,
        };
        g.DrawString(text, font, brush, new RectangleF(0, 0, dimension, dimension), format);
        font.Dispose();
    }

    /// <summary>
    /// Largest bold font whose glyphs still fit the icon box. Start big for punch, then shrink until
    /// both digits fit the width — otherwise a wide value like "70" gets clipped on a 16px icon.
    /// </summary>
    private static Font FitFont(Graphics g, string text, int dimension)
    {
        var size = dimension * 0.80f;
        while (size > dimension * 0.4f)
        {
            var font = new Font("Segoe UI", size, FontStyle.Bold, GraphicsUnit.Pixel);
            var bounds = g.MeasureString(text, font);
            if (bounds.Width <= dimension && bounds.Height <= dimension)
                return font;
            font.Dispose();
            size -= 1f;
        }

        return new Font("Segoe UI", dimension * 0.4f, FontStyle.Bold, GraphicsUnit.Pixel);
    }

    public void Dispose()
    {
        _previousIcon?.Dispose();
        if (_previousHandle != IntPtr.Zero)
            DestroyIcon(_previousHandle);
        _previousHandle = IntPtr.Zero;
    }
}
