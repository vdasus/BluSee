namespace BluSee.Tray;

/// <summary>Applies a light or dark look to the tray context menu, following the Windows app theme.</summary>
public static class ThemeMenu
{
    private static readonly Color DarkBack = Color.FromArgb(0x2B, 0x2B, 0x2B);
    private static readonly Color DarkText = Color.FromArgb(0xF0, 0xF0, 0xF0);

    public static void Apply(ContextMenuStrip menu, bool lightTheme)
    {
        if (lightTheme)
        {
            menu.Renderer = new ToolStripProfessionalRenderer { RoundedEdges = true };
            menu.BackColor = SystemColors.Window;
            menu.ForeColor = SystemColors.WindowText;
        }
        else
        {
            menu.Renderer = new ToolStripProfessionalRenderer(new DarkColors()) { RoundedEdges = false };
            menu.BackColor = DarkBack;
            menu.ForeColor = DarkText;
        }

        foreach (var item in menu.Items.OfType<ToolStripItem>())
            item.ForeColor = lightTheme ? SystemColors.WindowText : DarkText;
    }

    /// <summary>Dark palette for the professional renderer.</summary>
    private sealed class DarkColors : ProfessionalColorTable
    {
        public override Color ToolStripDropDownBackground => DarkBack;
        public override Color ImageMarginGradientBegin => DarkBack;
        public override Color ImageMarginGradientMiddle => DarkBack;
        public override Color ImageMarginGradientEnd => DarkBack;
        public override Color MenuBorder => Color.FromArgb(0x55, 0x55, 0x55);
        public override Color MenuItemBorder => Color.FromArgb(0x3D, 0x3D, 0x3D);
        public override Color MenuItemSelected => Color.FromArgb(0x3D, 0x3D, 0x3D);
        public override Color MenuItemSelectedGradientBegin => Color.FromArgb(0x3D, 0x3D, 0x3D);
        public override Color MenuItemSelectedGradientEnd => Color.FromArgb(0x3D, 0x3D, 0x3D);
        public override Color SeparatorDark => Color.FromArgb(0x55, 0x55, 0x55);
        public override Color SeparatorLight => Color.FromArgb(0x55, 0x55, 0x55);
    }
}
