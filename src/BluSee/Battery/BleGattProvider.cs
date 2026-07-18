using BluSee.Logging;
using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.GenericAttributeProfile;
using Windows.Devices.Enumeration;
using Windows.Storage.Streams;

namespace BluSee.Battery;

/// <summary>
/// Battery for Bluetooth Low Energy devices via the standard GATT Battery Service (0x180F),
/// characteristic Battery Level (0x2A19). This is the path for devices connected directly over
/// Bluetooth (no Logitech receiver, and Windows exposes no battery DEVPKEY for them).
/// </summary>
public sealed class BleGattProvider : IBatteryProvider
{
    private static readonly TimeSpan PerDeviceTimeout = TimeSpan.FromSeconds(5);

    // When no paired BLE devices exist, re-enumerating every poll only churns WinRT COM wrappers
    // (native memory until a gen2 GC). Recheck every Nth poll — pairing a new device is rare.
    private const int RecheckEvery = 6;
    private int _pollCount;
    private bool _anyPaired = true; // assume yes until the first enumeration says otherwise

    public string Name => "BLE GATT";

    public async Task<IReadOnlyList<DeviceBattery>> GetDevicesAsync(CancellationToken ct)
    {
        var result = new List<DeviceBattery>();

        _pollCount++;
        if (!_anyPaired && _pollCount % RecheckEvery != 1)
        {
            DebugLog.Write("ble", "skipped (no paired BLE devices; periodic recheck pending)");
            return result;
        }

        var selector = BluetoothLEDevice.GetDeviceSelectorFromPairingState(true);
        var paired = await DeviceInformation.FindAllAsync(selector).AsTask(ct);
        _anyPaired = paired.Count > 0;
        DebugLog.Write("ble", $"{paired.Count} paired BLE device(s)");
        foreach (var info in paired)
        {
            ct.ThrowIfCancellationRequested();

            // A single unreachable device must not hang the whole poll.
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(PerDeviceTimeout);
            try
            {
                var device = await ReadDeviceAsync(info, cts.Token);
                if (device is not null)
                    result.Add(device);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                // device timed out — skip it
                DebugLog.Write("ble", $"'{info.Name}': timed out after {PerDeviceTimeout.TotalSeconds:0}s");
            }
            catch (Exception ex)
            {
                // unreachable / access error — skip
                DebugLog.Write("ble", $"'{info.Name}': {ex.GetType().Name}: {ex.Message}");
            }
        }

        return result;
    }

    private static async Task<DeviceBattery?> ReadDeviceAsync(DeviceInformation info, CancellationToken ct)
    {
        using var device = await BluetoothLEDevice.FromIdAsync(info.Id).AsTask(ct);
        if (device is null)
        {
            DebugLog.Write("ble", $"'{info.Name}': FromIdAsync returned null");
            return null;
        }

        var services = await device
            .GetGattServicesForUuidAsync(GattServiceUuids.Battery, BluetoothCacheMode.Uncached)
            .AsTask(ct);
        if (services.Status != GattCommunicationStatus.Success || services.Services.Count == 0)
        {
            DebugLog.Write("ble", $"'{info.Name}': battery service query -> {services.Status}, {services.Services.Count} service(s)");
            return null;
        }

        try
        {
            foreach (var service in services.Services)
            {
                var chars = await service
                    .GetCharacteristicsForUuidAsync(GattCharacteristicUuids.BatteryLevel, BluetoothCacheMode.Uncached)
                    .AsTask(ct);
                if (chars.Status != GattCommunicationStatus.Success || chars.Characteristics.Count == 0)
                {
                    DebugLog.Write("ble", $"'{info.Name}': battery level characteristic query -> {chars.Status}, {chars.Characteristics.Count} char(s)");
                    continue;
                }

                var read = await chars.Characteristics[0].ReadValueAsync(BluetoothCacheMode.Uncached).AsTask(ct);
                if (read.Status != GattCommunicationStatus.Success)
                {
                    DebugLog.Write("ble", $"'{info.Name}': battery level read -> {read.Status}");
                    continue;
                }

                using var reader = DataReader.FromBuffer(read.Value);
                if (reader.UnconsumedBufferLength < 1)
                    continue;

                int percent = reader.ReadByte();
                if (percent is < 0 or > 100)
                    continue;

                var connected = device.ConnectionStatus == BluetoothConnectionStatus.Connected;
                DebugLog.Write("ble", $"'{info.Name}': battery level read -> {percent}%, connected={connected}");
                return new DeviceBattery(
                    Id: info.Id,
                    Name: string.IsNullOrWhiteSpace(device.Name) ? info.Name : device.Name,
                    Transport: DeviceTransport.BluetoothLowEnergy,
                    BatteryPercent: percent,
                    IsConnected: connected,
                    Source: BatterySource.BleGatt);
            }
        }
        finally
        {
            foreach (var service in services.Services)
                service.Dispose();
        }

        return null;
    }
}
