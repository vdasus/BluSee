using BluSee.Logging;
using Windows.Devices.Enumeration;

namespace BluSee.Battery;

/// <summary>
/// Cheap unified provider: reads battery from the OS cache (device PnP property).
/// Covers Bluetooth Classic and devices behind a USB receiver (Logi Bolt / Unifying),
/// when Windows has cached their battery (visible in Settings → Bluetooth &amp; devices).
/// BLE devices without a cache are picked up by <see cref="BleGattProvider"/>.
/// Reads via CfgMgr32 (<see cref="NativePnp"/>), not WinRT — the WinRT property-request list
/// cannot be marshalled under NativeAOT (see NativePnp doc comment for the history).
/// </summary>
public sealed class PnpBatteryProvider : IBatteryProvider
{
    // Device setup-class GUIDs (Kind=Device devnodes carry the battery DEVPKEY, not the interfaces).
    private const string HidClass = "{745a17a0-74d3-11d0-b6fe-00a0c90f57da}";       // HIDClass
    private const string BluetoothClass = "{e0cbf06c-cd8b-4647-bb8a-263b43f0f974}"; // Bluetooth
    private const string MiceClass = "{4d36e96f-e325-11ce-bfc1-08002be10318}";
    private const string KeyboardClass = "{4d36e96b-e325-11ce-bfc1-08002be10318}";

    private static readonly string[] ClassGuids = [HidClass, BluetoothClass, MiceClass, KeyboardClass];

    public string Name => "PnP (OS cache)";

    public Task<IReadOnlyList<DeviceBattery>> GetDevicesAsync(CancellationToken ct)
        => Task.Run<IReadOnlyList<DeviceBattery>>(() => ReadAll(ct), ct);

    private static List<DeviceBattery> ReadAll(CancellationToken ct)
    {
        var byContainer = new Dictionary<string, DeviceBattery>(StringComparer.OrdinalIgnoreCase);

        foreach (var classGuid in ClassGuids)
        {
            ct.ThrowIfCancellationRequested();

            foreach (var id in NativePnp.GetPresentDeviceIds(classGuid))
            {
                if (NativePnp.CM_Locate_DevNode(out var devInst, id, 0) != NativePnp.CrSuccess)
                    continue;

                var battery = NativePnp.GetIntProperty(devInst, in NativePnp.BatteryLevel2)
                    ?? NativePnp.GetIntProperty(devInst, in NativePnp.BatteryLevel9);
                if (battery is null or < 0 or > 100)
                    continue; // this provider only cares about devices that expose battery

                var name = NativePnp.GetStringProperty(devInst, in NativePnp.NameKey) ?? "(unknown)";
                var transport = ResolveTransport(id);
                var status = NativePnp.GetUIntProperty(devInst, in NativePnp.DevNodeStatus);
                var connected = ((status ?? 0) & NativePnp.DnStarted) != 0;
                var key = NativePnp.GetGuidProperty(devInst, in NativePnp.ContainerId)?.ToString() ?? id;

                var device = new DeviceBattery(
                    Id: id,
                    Name: name,
                    Transport: transport,
                    BatteryPercent: battery,
                    IsConnected: connected,
                    Source: BatterySource.PnpProperty);

                DebugLog.Write("pnp", $"'{device.Name}': {battery}% (OS cache), connected={connected}, transport={transport}, id={id}");

                // dedup: one physical device may arrive via several interfaces
                byContainer[key] = device;
            }
        }

        return [.. byContainer.Values];
    }

    // The bus enumerator prefix of the instance id tells the transport apart.
    private static DeviceTransport ResolveTransport(string instanceId)
        => instanceId.StartsWith(@"BTHENUM\", StringComparison.OrdinalIgnoreCase)
            ? DeviceTransport.BluetoothClassic
            : instanceId.StartsWith("BTHLE", StringComparison.OrdinalIgnoreCase)
                ? DeviceTransport.BluetoothLowEnergy
                : DeviceTransport.UsbReceiver; // not a BT devnode → dongle/wired

    /// <summary>
    /// Exhaustive WinRT sweep: every Kind=Device node with battery keys requested. Diagnostic-only
    /// and slow; used by Debug-build --diag to settle definitively whether Windows exposes a battery
    /// DEVPKEY anywhere on this machine. Fine outside NativeAOT (framework builds marshal the list).
    /// </summary>
    internal static async Task<IReadOnlyList<DeviceInformation>> EnumerateAllAsync(CancellationToken ct)
    {
        var nodes = await DeviceInformation
            .FindAllAsync(string.Empty, DeviceProperties.Requested, DeviceInformationKind.Device)
            .AsTask(ct);
        return nodes;
    }
}
