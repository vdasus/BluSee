using System.Text.Json;
using System.Text.Json.Serialization;
using BluSee.Battery;
using BluSee.Logging;

namespace BluSee.Monitoring;

/// <summary>A device reading together with the time it was taken.</summary>
public sealed record PersistedDevice(DeviceBattery Device, DateTime SavedAtUtc);

/// <summary>
/// Persists last known battery readings next to the exe, so a freshly started process can list a
/// sleeping device with its last known percent instead of hiding it until the first successful
/// read (same idea as Logi Options+). Provider caches die with the process; this is the
/// cross-restart complement. All failures are swallowed — the cache is an enhancement, never a
/// blocker. Serialization is source-generated to stay NativeAOT-compatible.
/// Stored as plain text deliberately: the file holds device instance ids (which embed Bluetooth
/// MAC addresses), names and battery values — data any local process can already enumerate via
/// Windows device APIs without elevation — and no pairing keys or other secrets. Encrypting it
/// would add no protection (a same-user process could decrypt it anyway) while breaking hand
/// inspection of a portable data file.
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
                    {
                        // Files written by v0.3.1 may carry a synthetic "Logitech 0xC548 #1" name
                        // without the flag — re-derive it so a real name can still replace it.
                        var device = entry.Device;
                        if (!device.IsFallbackName
                            && device.Name.StartsWith("Logitech 0x", StringComparison.Ordinal)
                            && device.Name.Contains(" #"))
                            device = device with { IsFallbackName = true };

                        cache._entries[device.Id] = entry with { Device = device };
                    }
            }
        }
        catch
        {
            // corrupt/unreadable — start empty
        }

        return cache;
    }

    /// <summary>
    /// Swap a provider's synthetic fallback name for the remembered real one under the same id
    /// (a sleeping device often answers battery but not its name).
    /// </summary>
    public DeviceBattery ResolveName(DeviceBattery fresh)
    {
        if (!fresh.IsFallbackName
            || !_entries.TryGetValue(fresh.Id, out var known)
            || known.Device.IsFallbackName)
            return fresh;

        DebugLog.Write("cache", $"restored name '{known.Device.Name}' for fallback '{fresh.Name}'");
        return fresh with { Name = known.Device.Name, IsFallbackName = false };
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
