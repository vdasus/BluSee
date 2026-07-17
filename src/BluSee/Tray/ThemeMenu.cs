namespace BluSee.Tray;

/// <summary>Applies a light or dark look to the tray context menu, following the Windows app theme.</summary>
public static class ThemeMenu
{
    private static readonly Color DarkBack = Color.FromArgb(0x2B, 0x2B, 0x2B);
    private static readonly Color DarkText = Color.FromArgb(0xF0, 0xF0, 0xF0);
    private static readonly Color DarkDisabledText = Color.FromArgb(0xC8, 0xC8, 0xC8);
    private static readonly Color LightDisabledText = Color.FromArgb(0x40, 0x40, 0x40);

    public static void Apply(ContextMenuStrip menu, bool lightTheme)
    {
        ToolStripRenderer renderer = lightTheme
            ? new ThemedRenderer(new ProfessionalColorTable(), LightDisabledText) { RoundedEdges = true }
            : new ThemedRenderer(new DarkColors(), DarkDisabledText) { RoundedEdges = false };
        ApplyTo(menu, renderer, lightTheme);
    }

    /// <summary>
    /// Themes one strip and recurses into submenus: each DropDown is a separate ToolStrip with its
    /// own renderer and colors, so theming only the top-level menu leaves submenus unstyled.
    /// </summary>
    private static void ApplyTo(ToolStrip strip, ToolStripRenderer renderer, bool lightTheme)
    {
        strip.Renderer = renderer;
        strip.BackColor = lightTheme ? SystemColors.Window : DarkBack;
        strip.ForeColor = lightTheme ? SystemColors.WindowText : DarkText;

        foreach (var item in strip.Items.OfType<ToolStripItem>())
        {
            item.ForeColor = lightTheme ? SystemColors.WindowText : DarkText;
            if (item is ToolStripMenuItem { HasDropDownItems: true } menuItem)
                ApplyTo(menuItem.DropDown, renderer, lightTheme);
        }
    }

    /// <summary>
    /// WinForms paints disabled items in SystemColors.GrayText regardless of ForeColor, which is
    /// unreadable on the dark background. Substitute a theme-aware color before the base draw.
    /// </summary>
    private sealed class ThemedRenderer(ProfessionalColorTable table, Color disabledText)
        : ToolStripProfessionalRenderer(table)
    {
        protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
        {
            if (!e.Item.Enabled)
                e.TextColor = disabledText;
            base.OnRenderItemText(e);
        }
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
