using System.Text.Json;
using System.Text.Json.Serialization;

namespace BluSee.Settings;

/// <summary>
/// Portable user settings stored as JSON next to the exe. Missing or invalid file falls back to
/// defaults (and a default file is written so the knob is discoverable). Serialization is
/// source-generated: reflection-based System.Text.Json is unavailable under NativeAOT.
/// </summary>
public sealed class AppSettings
{
    private const int DefaultIntervalMinutes = 10;

    private static string FilePath => Path.Combine(AppContext.BaseDirectory, "blusee.settings.json");

    /// <summary>Poll interval in minutes. Defaults to 10 when unset.</summary>
    public int PollIntervalMinutes { get; set; } = DefaultIntervalMinutes;

    /// <summary>Clamped interval (1 minute .. 24 hours) used by the monitor. Not persisted.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public TimeSpan PollInterval => TimeSpan.FromMinutes(Math.Clamp(PollIntervalMinutes, 1, 1440));

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                var loaded = JsonSerializer.Deserialize(File.ReadAllText(FilePath), AppSettingsJsonContext.Default.AppSettings);
                if (loaded is not null)
                    return loaded;
            }
        }
        catch
        {
            // unreadable/corrupt — fall back to defaults
        }

        var defaults = new AppSettings();
        defaults.TrySave();
        return defaults;
    }

    public void TrySave()
    {
        try
        {
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, AppSettingsJsonContext.Default.AppSettings));
        }
        catch
        {
            // read-only location — keep running with in-memory values
        }
    }
}

[JsonSourceGenerationOptions(WriteIndented = true, PropertyNameCaseInsensitive = true)]
[JsonSerializable(typeof(AppSettings))]
internal sealed partial class AppSettingsJsonContext : JsonSerializerContext;
