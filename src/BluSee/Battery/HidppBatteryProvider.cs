using BluSee.Battery.Hidpp;

namespace BluSee.Battery;

/// <summary>
/// Battery for Logitech devices behind a receiver (Bolt / Unifying / LIGHTSPEED) via HID++.
/// This is the primary path on hardware where Windows exposes no battery DEVPKEY (the value is only
/// visible in Logi Options+, which itself reads it over HID++).
/// </summary>
public sealed class HidppBatteryProvider : IBatteryProvider
{
    private const ushort LogitechVendorId = 0x046D;

    // Last known reading per device id. A wireless device that is asleep may miss a poll; rather than
    // make it vanish from the menu we keep showing the last value (marked disconnected), as Options+ does.
    private readonly Dictionary<string, DeviceBattery> _cache = new(StringComparer.OrdinalIgnoreCase);

    // Real device names (0x0005), kept once resolved so a later sleepy poll doesn't drop back to the
    // "0xC548 #1" fallback.
    private readonly Dictionary<string, string> _names = new(StringComparer.OrdinalIgnoreCase);

    public string Name => "Logitech HID++";

    public async Task<IReadOnlyList<DeviceBattery>> GetDevicesAsync(CancellationToken ct)
    {
        var fresh = new Dictionary<string, DeviceBattery>(StringComparer.OrdinalIgnoreCase);

        var groups = await HidppTransport.FindReceiverGroupsAsync(ct);
        foreach (var group in groups)
        {
            ct.ThrowIfCancellationRequested();

            await using var transport = await HidppTransport.OpenAsync(group, ct);
            if (transport is null || transport.VendorId != LogitechVendorId)
                continue;

            var client = new HidppClient(transport);
            foreach (var reading in await client.ReadAllAsync(ct))
            {
                var id = $"{group.Key}#dev{reading.DeviceIndex}";
                if (reading.Name is not null)
                    _names[id] = reading.Name;

                var name = reading.Name
                    ?? _names.GetValueOrDefault(id)
                    ?? $"Logitech 0x{transport.ProductId:X4} #{reading.DeviceIndex}";

                fresh[id] = new DeviceBattery(
                    Id: id,
                    Name: name,
                    Transport: DeviceTransport.UsbReceiver,
                    BatteryPercent: reading.Percent,
                    IsConnected: true,
                    Source: BatterySource.Hidpp);
            }
        }

        // Merge: refresh the cache with this poll, and re-emit cached devices that were silent now.
        foreach (var (id, device) in fresh)
            _cache[id] = device;

        var result = new List<DeviceBattery>(fresh.Values);
        foreach (var (id, cached) in _cache)
            if (!fresh.ContainsKey(id))
                result.Add(cached with { IsConnected = false });

        return result;
    }
}
