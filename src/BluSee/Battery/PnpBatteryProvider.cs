using Windows.Devices.Enumeration;

namespace BluSee.Battery;

/// <summary>
/// Cheap unified provider: reads battery from the OS cache (device PnP property).
/// Covers Bluetooth Classic and devices behind a USB receiver (Logi Bolt / Unifying),
/// when Windows has cached their battery (visible in Settings → Bluetooth &amp; devices).
/// BLE devices without a cache are picked up by <see cref="BleGattProvider"/>.
/// </summary>
public sealed class PnpBatteryProvider : IBatteryProvider
{
    // Device setup-class GUIDs (Kind=Device devnodes carry the battery DEVPKEY, not the interfaces).
    private const string HidClass = "{745a17a0-74d3-11d0-b6fe-00a0c90f57da}";       // HIDClass
    private const string BluetoothClass = "{e0cbf06c-cd8b-4647-bb8a-263b43f0f974}"; // Bluetooth

    public string Name => "PnP (OS cache)";

    public async Task<IReadOnlyList<DeviceBattery>> GetDevicesAsync(CancellationToken ct)
    {
        var byContainer = new Dictionary<string, DeviceBattery>(StringComparer.OrdinalIgnoreCase);

        foreach (var info in await EnumerateAsync(ct))
        {
            ct.ThrowIfCancellationRequested();

            var props = info.Properties;
            var battery = DeviceProperties.ReadBatteryPercent(props);
            if (battery is null)
                continue; // this provider only cares about devices that expose battery

            var transport = DeviceProperties.ResolveTransport(props);
            if (transport == DeviceTransport.Unknown)
                transport = DeviceTransport.UsbReceiver; // not a BT AEP → dongle/wired

            var connected = props.TryGetValue(DeviceProperties.AepIsConnected, out var c) && c is true;
            var key = props.TryGetValue(DeviceProperties.ContainerId, out var cid) && cid is Guid g
                ? g.ToString()
                : info.Id;

            var device = new DeviceBattery(
                Id: info.Id,
                Name: string.IsNullOrWhiteSpace(info.Name) ? "(unknown)" : info.Name,
                Transport: transport,
                BatteryPercent: battery,
                IsConnected: connected,
                Source: BatterySource.PnpProperty);

            // dedup: one physical device may arrive via several interfaces
            byContainer[key] = device;
        }

        return [.. byContainer.Values];
    }

    /// <summary>
    /// Enumerate Kind=Device devnodes that carry the battery DEVPKEY. The battery property lives on
    /// the device node (HID collection / Bluetooth / mouse / keyboard), not on AEPs or interfaces.
    /// Covers Logi Bolt / Unifying children and Bluetooth devices.
    /// </summary>
    internal static async Task<IReadOnlyList<DeviceInformation>> EnumerateAsync(CancellationToken ct)
    {
        // Mouse and keyboard function devnodes often hold battery, under their own setup classes.
        const string miceClass = "{4d36e96f-e325-11ce-bfc1-08002be10318}";
        const string keyboardClass = "{4d36e96b-e325-11ce-bfc1-08002be10318}";

        var result = new List<DeviceInformation>();
        foreach (var classGuid in (string[])[HidClass, BluetoothClass, miceClass, keyboardClass])
        {
            ct.ThrowIfCancellationRequested();
            var selector = $"System.Devices.ClassGuid:=\"{classGuid}\"";
            var nodes = await DeviceInformation
                .FindAllAsync(selector, DeviceProperties.Requested, DeviceInformationKind.Device)
                .AsTask(ct);
            result.AddRange(nodes);
        }

        return result;
    }

    /// <summary>
    /// Exhaustive sweep: every Kind=Device node with battery keys requested. Diagnostic-only and slow;
    /// used to settle definitively whether Windows exposes a battery DEVPKEY anywhere on this machine.
    /// </summary>
    internal static async Task<IReadOnlyList<DeviceInformation>> EnumerateAllAsync(CancellationToken ct)
    {
        var nodes = await DeviceInformation
            .FindAllAsync(string.Empty, DeviceProperties.Requested, DeviceInformationKind.Device)
            .AsTask(ct);
        return nodes;
    }
}
