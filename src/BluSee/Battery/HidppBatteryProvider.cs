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

    public string Name => "Logitech HID++";

    public async Task<IReadOnlyList<DeviceBattery>> GetDevicesAsync(CancellationToken ct)
    {
        var result = new List<DeviceBattery>();

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
                result.Add(new DeviceBattery(
                    Id: $"{group.Key}#dev{reading.DeviceIndex}",
                    Name: reading.Name ?? $"Logitech 0x{transport.ProductId:X4} #{reading.DeviceIndex}",
                    Transport: DeviceTransport.UsbReceiver,
                    BatteryPercent: reading.Percent,
                    IsConnected: true,
                    Source: BatterySource.Hidpp));
            }
        }

        return result;
    }
}
