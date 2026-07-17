using System.Text.Json;
using System.Text.Json.Serialization;
using BluSee.Battery;

namespace BluSee.Monitoring;

/// <summary>A device reading together with the time it was taken.</summary>
public sealed record PersistedDevice(DeviceBattery Device, DateTime SavedAtUtc);

/// <summary>
/// Persists last known battery readings next to the exe, so a freshly started process can list a
/// sleeping device with its last known percent instead of hiding it until the first successful
/// read (same idea as Logi Options+). Provider caches die with the process; this is the
/// cross-restart complement. All failures are swallowed — the cache is an enhancement, never a
/// blocker. Serialization is source-generated to stay NativeAOT-compatible.
/// </summary>
public sealed class DeviceCache
{
    private static readonly TimeSpan MaxAge = TimeSpan.FromDays(30);

    private static string FilePath => Path.Combine(AppContext.BaseDirectory, "blusee.devices.json");

    private readonly Dictionary<string, PersistedDevice> _entries = new(StringComparer.OrdinalIgnoreCase);
    private string _lastSaved = "";

    /// <summary>Cached devices, always marked disconnected — only a fresh read proves presence.</summary>
    public IReadOnlyList<DeviceBattery> Devices =>
        _entries.Values.Select(e => e.Device with { IsConnected = false }).ToList();

    public static DeviceCache Load()
    {
        var cache = new DeviceCache();
        try
        {
            if (File.Exists(FilePath))
            {
                var entries = JsonSerializer.Deserialize(
                    File.ReadAllText(FilePath), DeviceCacheJsonContext.Default.ListPersistedDevice);
                var cutoff = DateTime.UtcNow - MaxAge;
                foreach (var entry in entries ?? [])
                    if (entry.SavedAtUtc >= cutoff && entry.Device.HasBattery)
                        cache._entries[entry.Device.Id] = entry;
            }
        }
        catch
        {
            // corrupt/unreadable — start empty
        }

        return cache;
    }

    /// <summary>Remembers every reading that carries a battery value, then saves if anything changed.</summary>
    public void Update(IReadOnlyList<DeviceBattery> fresh)
    {
        foreach (var device in fresh)
            if (device.HasBattery)
                _entries[device.Id] = new PersistedDevice(device, DateTime.UtcNow);
        TrySave();
    }

    private void TrySave()
    {
        try
        {
            // Fingerprint without timestamps: values are usually stable between polls, and a stable
            // poll must not rewrite the file every few minutes.
            var devices = _entries.Values
                .Select(e => e.Device)
                .OrderBy(d => d.Id, StringComparer.OrdinalIgnoreCase)
                .ToList();
            var fingerprint = JsonSerializer.Serialize(devices, DeviceCacheJsonContext.Default.ListDeviceBattery);
            if (fingerprint == _lastSaved)
                return;

            File.WriteAllText(
                FilePath, JsonSerializer.Serialize(_entries.Values.ToList(), DeviceCacheJsonContext.Default.ListPersistedDevice));
            _lastSaved = fingerprint;
        }
        catch
        {
            // read-only location — keep the cache in memory only
        }
    }
}

[JsonSourceGenerationOptions(WriteIndented = true)]
[JsonSerializable(typeof(List<PersistedDevice>))]
[JsonSerializable(typeof(List<DeviceBattery>))]
internal sealed partial class DeviceCacheJsonContext : JsonSerializerContext;
