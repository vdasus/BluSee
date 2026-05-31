using Microsoft.Win32;

namespace BluSee.Tray;

/// <summary>Reads the Windows light/dark app theme from the registry.</summary>
public static class ThemeReader
{
    private const string PersonalizeKey = @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";

    /// <summary>True when apps use the light theme (so the tray icon should draw dark glyphs).</summary>
    public static bool IsLightTheme()
    {
        using var key = Registry.CurrentUser.OpenSubKey(PersonalizeKey);
        return key?.GetValue("AppsUseLightTheme") is int value ? value != 0 : true;
    }
}
