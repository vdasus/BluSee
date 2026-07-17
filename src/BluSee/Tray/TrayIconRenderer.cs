using System.Drawing;
using System.Drawing.Drawing2D;
using BluSee.Tray.Win32;

namespace BluSee.Tray;

/// <summary>
/// Generates the tray icon on the fly from the current battery percent. Each render allocates an
/// HICON via <see cref="Bitmap.GetHicon"/>, which must be released with DestroyIcon — otherwise the
/// process leaks GDI handles over its lifetime. The shell copies the icon on Shell_NotifyIcon, so
/// the previous handle is freed on the next render.
/// </summary>
public sealed class TrayIconRenderer : IDisposable
{
    private nint _previousHandle;

    /// <summary>
    /// Renders the percent into a small icon. <paramref name="scale"/> (0..1] shrinks the digits
    /// within the fixed icon box, centered. The handle stays valid until the next call.
    /// </summary>
    public nint Render(int? percent, bool lightTheme, float scale = 1f)
    {
        var dimension = Math.Max(16, Native.GetSystemMetrics(Native.SM_CXSMICON));

        using var bitmap = new Bitmap(dimension, dimension);
        using (var g = Graphics.FromImage(bitmap))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Color.Transparent);
            Draw(g, dimension, percent, lightTheme, scale);
        }

        var handle = bitmap.GetHicon();
        if (_previousHandle != 0)
            Native.DestroyIcon(_previousHandle);
        _previousHandle = handle;
        return handle;
    }

    private static void Draw(Graphics g, int dimension, int? percent, bool lightTheme, float userScale)
    {
        var text = percent is null ? "?" : percent.Value >= 100 ? "F" : percent.Value.ToString();
        var color = percent switch
        {
            null => lightTheme ? Color.DimGray : Color.WhiteSmoke,
            <= 15 => lightTheme ? Color.FromArgb(0xE8, 0x11, 0x23) : Color.FromArgb(0xFF, 0x55, 0x5F), // red
            <= 40 => lightTheme ? Color.FromArgb(0xE0, 0x9A, 0x00) : Color.FromArgb(0xFF, 0xCC, 0x33), // amber
            _ => lightTheme ? Color.FromArgb(0x0E, 0x70, 0x0E) : Color.FromArgb(0x8C, 0xEC, 0x6F), // green
        };

        // Font-size based drawing wastes ~35% of the box on line spacing (ascent/descent), which is
        // why the digits looked small. Instead take the actual glyph outline, measure its true
        // bounds and scale it to fill the icon almost edge to edge.
        using var path = new GraphicsPath();
        using (var family = new FontFamily("Segoe UI"))
            path.AddString(text, family, (int)FontStyle.Bold, dimension, PointF.Empty, StringFormat.GenericTypographic);

        var bounds = path.GetBounds();
        if (bounds.Width <= 0f || bounds.Height <= 0f)
            return;

        var target = (dimension - 1f) * userScale;
        var scale = Math.Min(target / bounds.Width, target / bounds.Height);
        using var transform = new Matrix();
        transform.Translate(
            (dimension - bounds.Width * scale) / 2f - bounds.X * scale,
            (dimension - bounds.Height * scale) / 2f - bounds.Y * scale);
        transform.Scale(scale, scale, MatrixOrder.Prepend);
        path.Transform(transform);

        using var brush = new SolidBrush(color);
        g.FillPath(brush, path);
    }

    public void Dispose()
    {
        if (_previousHandle != 0)
            Native.DestroyIcon(_previousHandle);
        _previousHandle = 0;
    }
}
