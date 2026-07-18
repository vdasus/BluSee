namespace BluSee.Settings;

/// <summary>
/// Portable user settings stored as a simple INI-style file next to the exe: one Key=Value per
/// line, '#' or ';' starts a comment. Deliberately not JSON — the whole config is one number a
/// human edits by hand. Missing or invalid file falls back to defaults (and a default file is
/// written so the knob is discoverable).
/// </summary>
public sealed class AppSettings
{
    private const int DefaultIntervalMinutes = 10;
    private const int DefaultIconScalePercent = 100;

    private static string FilePath => Path.Combine(AppContext.BaseDirectory, "blusee.ini");

    /// <summary>Poll interval in minutes. Defaults to 10 when unset.</summary>
    public int PollIntervalMinutes { get; set; } = DefaultIntervalMinutes;

    /// <summary>Clamped interval (1 minute .. 24 hours) used by the monitor. Not persisted.</summary>
    public TimeSpan PollInterval => TimeSpan.FromMinutes(Math.Clamp(PollIntervalMinutes, 1, 1440));

    /// <summary>
    /// Scale of the digits inside the tray icon, in percent. 100 fills the icon edge to edge;
    /// e.g. 90 draws them 10% smaller (centered). The icon box itself is fixed by the system.
    /// </summary>
    public int IconScalePercent { get; set; } = DefaultIconScalePercent;

    /// <summary>Clamped scale factor (0.30 .. 1.00) used by the renderer. Not persisted.</summary>
    public float IconScale => Math.Clamp(IconScalePercent, 30, 100) / 100f;

    /// <summary>
    /// Debug=on in the ini enables trace logging of every poll (device requests and responses)
    /// to blusee.log next to the exe. Off by default.
    /// </summary>
    public bool Debug { get; set; }

    public static AppSettings Load()
    {
        var settings = new AppSettings();
        try
        {
            if (File.Exists(FilePath))
            {
                foreach (var rawLine in File.ReadAllLines(FilePath))
                {
                    var line = rawLine.Trim();
                    if (line.Length == 0 || line[0] is '#' or ';')
                        continue;

                    var separator = line.IndexOf('=');
                    if (separator <= 0)
                        continue;

                    var key = line[..separator].Trim();
                    var value = line[(separator + 1)..].Trim();
                    if (key.Equals(nameof(PollIntervalMinutes), StringComparison.OrdinalIgnoreCase)
                        && int.TryParse(value, out var minutes))
                        settings.PollIntervalMinutes = minutes;
                    else if (key.Equals(nameof(IconScalePercent), StringComparison.OrdinalIgnoreCase)
                        && int.TryParse(value, out var scale))
                        settings.IconScalePercent = scale;
                    else if (key.Equals(nameof(Debug), StringComparison.OrdinalIgnoreCase))
                        settings.Debug = value.ToLowerInvariant() is "on" or "true" or "1" or "yes";
                }

                return settings;
            }
        }
        catch
        {
            // unreadable — fall back to defaults
        }

        settings.TrySave();
        return settings;
    }

    public void TrySave()
    {
        try
        {
            File.WriteAllText(
                FilePath,
                $"# BluSee settings{Environment.NewLine}"
                + $"PollIntervalMinutes={PollIntervalMinutes}{Environment.NewLine}"
                + $"# Tray digits size in percent (30..100); 100 fills the icon, 90 is 10% smaller.{Environment.NewLine}"
                + $"IconScalePercent={IconScalePercent}{Environment.NewLine}"
                + $"# Debug=on traces every poll (device requests/responses) to blusee.log.{Environment.NewLine}"
                + $"Debug={(Debug ? "on" : "off")}{Environment.NewLine}");
        }
        catch
        {
            // read-only location — keep running with in-memory values
        }
    }
}
