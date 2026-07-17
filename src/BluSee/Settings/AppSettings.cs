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

    private static string FilePath => Path.Combine(AppContext.BaseDirectory, "blusee.ini");

    /// <summary>Poll interval in minutes. Defaults to 10 when unset.</summary>
    public int PollIntervalMinutes { get; set; } = DefaultIntervalMinutes;

    /// <summary>Clamped interval (1 minute .. 24 hours) used by the monitor. Not persisted.</summary>
    public TimeSpan PollInterval => TimeSpan.FromMinutes(Math.Clamp(PollIntervalMinutes, 1, 1440));

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
                $"# BluSee settings{Environment.NewLine}PollIntervalMinutes={PollIntervalMinutes}{Environment.NewLine}");
        }
        catch
        {
            // read-only location — keep running with in-memory values
        }
    }
}
