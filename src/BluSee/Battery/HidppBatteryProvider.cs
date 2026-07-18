using BluSee.Battery.Hidpp;
using BluSee.Logging;

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

    // Receiver slots that ever produced a reading, per receiver key. Steady-state polls probe only
    // these: probing all 6 slots each time costs seconds of write timeouts when the receiver's RF
    // buffer is busy (each empty-slot frame still has to be accepted). A full 1..6 rescan runs
    // every FullScanEvery-th poll to pick up newly paired devices.
    private readonly Dictionary<string, HashSet<byte>> _knownSlots = new(StringComparer.OrdinalIgnoreCase);
    private const int FullScanEvery = 6;
    private int _pollCount;

    // Resolved feature indices per receiver, reused across polls (feature tables are stable per
    // device). Saves one root.getFeature frame per device per poll — see HidppClient doc.
    private readonly Dictionary<string, Dictionary<(byte DeviceIndex, ushort Feature), byte>> _featureIndices =
        new(StringComparer.OrdinalIgnoreCase);

    public string Name => "Logitech HID++";

    public async Task<IReadOnlyList<DeviceBattery>> GetDevicesAsync(CancellationToken ct)
    {
        var fresh = new Dictionary<string, DeviceBattery>(StringComparer.OrdinalIgnoreCase);
        _pollCount++;

        var groups = await HidppTransport.FindReceiverGroupsAsync(ct);
        DebugLog.Write("hidpp", $"{groups.Count} receiver group(s) found");
        foreach (var group in groups)
        {
            ct.ThrowIfCancellationRequested();

            await using var transport = await HidppTransport.OpenAsync(group, ct);
            if (transport is null || transport.VendorId != LogitechVendorId)
            {
                DebugLog.Write("hidpp", transport is null
                    ? $"receiver '{group.Key}': could not open"
                    : $"receiver '{group.Key}': skipped, vendor 0x{transport.VendorId:X4} is not Logitech");
                continue;
            }

            DebugLog.Write("hidpp", $"receiver '{group.Key}': open, VID=0x{transport.VendorId:X4} PID=0x{transport.ProductId:X4}");
            if (!_featureIndices.TryGetValue(group.Key, out var featureIndices))
                _featureIndices[group.Key] = featureIndices = [];
            var client = new HidppClient(transport, featureIndices);

            // Skip the multi-frame name read for slots we already resolved a real name for.
            bool NeedName(byte slot) => !_names.ContainsKey($"{group.Key}#dev{slot}");

            var known = _knownSlots.GetValueOrDefault(group.Key);
            var fullScan = known is null || known.Count == 0 || _pollCount % FullScanEvery == 1;
            IReadOnlyList<HidppBatteryReading> readings;
            if (fullScan)
            {
                DebugLog.Write("hidpp", "full slot scan (1..6)");
                readings = await client.ReadSlotsAsync([1, 2, 3, 4, 5, 6], allowDirectFallback: true, NeedName, ct);
            }
            else
            {
                var slots = known!.Order().ToList();
                DebugLog.Write("hidpp", $"probing known slots [{string.Join(",", slots)}]");
                readings = await client.ReadSlotsAsync(slots, allowDirectFallback: false, NeedName, ct);
            }

            foreach (var reading in readings)
            {
                if (known is null)
                    _knownSlots[group.Key] = known = [];
                known.Add(reading.DeviceIndex);
            }

            foreach (var reading in readings)
            {
                var id = $"{group.Key}#dev{reading.DeviceIndex}";
                if (reading.Name is not null)
                    _names[id] = reading.Name;

                var resolved = reading.Name ?? _names.GetValueOrDefault(id);
                var name = resolved ?? $"Logitech 0x{transport.ProductId:X4} #{reading.DeviceIndex}";

                DebugLog.Write("hidpp", $"device #{reading.DeviceIndex} '{name}': {reading.Percent}% via feature 0x{reading.Feature:X4}");

                fresh[id] = new DeviceBattery(
                    Id: id,
                    Name: name,
                    Transport: DeviceTransport.UsbReceiver,
                    BatteryPercent: reading.Percent,
                    IsConnected: true,
                    Source: BatterySource.Hidpp,
                    IsFallbackName: resolved is null);
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
