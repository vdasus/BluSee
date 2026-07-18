namespace BluSee.Tray.Win32;

/// <summary>
/// Opts the process into dark popup menus via undocumented uxtheme exports (ordinals 135/136,
/// present since Windows 10 1809 and still shipped in Windows 11). With AllowDark set, native menus
/// follow the system app theme automatically — no owner-draw needed. If the exports ever disappear,
/// the app silently falls back to standard light menus.
/// </summary>
internal static unsafe class DarkMode
{
    private const int AllowDark = 1;

    private static delegate* unmanaged<int, int> _setPreferredAppMode;
    private static delegate* unmanaged<void> _flushMenuThemes;

    /// <summary>Call once at startup, before any window is created.</summary>
    public static void EnableForProcess()
    {
        var uxtheme = Native.LoadLibraryW("uxtheme.dll");
        if (uxtheme == 0)
            return;

        _setPreferredAppMode = (delegate* unmanaged<int, int>)Native.GetProcAddress(uxtheme, 135);
        _flushMenuThemes = (delegate* unmanaged<void>)Native.GetProcAddress(uxtheme, 136);

        if (_setPreferredAppMode is not null)
            _setPreferredAppMode(AllowDark);
        Flush();
    }

    /// <summary>Re-apply menu theming after the system theme changes at runtime.</summary>
    public static void Flush()
    {
        if (_flushMenuThemes is not null)
            _flushMenuThemes();
    }
}
