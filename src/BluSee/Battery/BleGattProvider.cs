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

    public string Name => "BLE GATT";

    public async Task<IReadOnlyList<DeviceBattery>> GetDevicesAsync(CancellationToken ct)
    {
        var result = new List<DeviceBattery>();

        var selector = BluetoothLEDevice.GetDeviceSelectorFromPairingState(true);
        var paired = await DeviceInformation.FindAllAsync(selector).AsTask(ct);
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
            }
            catch
            {
                // unreachable / access error — skip
            }
        }

        return result;
    }

    private static async Task<DeviceBattery?> ReadDeviceAsync(DeviceInformation info, CancellationToken ct)
    {
        using var device = await BluetoothLEDevice.FromIdAsync(info.Id).AsTask(ct);
        if (device is null)
            return null;

        var services = await device
            .GetGattServicesForUuidAsync(GattServiceUuids.Battery, BluetoothCacheMode.Uncached)
            .AsTask(ct);
        if (services.Status != GattCommunicationStatus.Success || services.Services.Count == 0)
            return null;

        try
        {
            foreach (var service in services.Services)
            {
                var chars = await service
                    .GetCharacteristicsForUuidAsync(GattCharacteristicUuids.BatteryLevel, BluetoothCacheMode.Uncached)
                    .AsTask(ct);
                if (chars.Status != GattCommunicationStatus.Success || chars.Characteristics.Count == 0)
                    continue;

                var read = await chars.Characteristics[0].ReadValueAsync(BluetoothCacheMode.Uncached).AsTask(ct);
                if (read.Status != GattCommunicationStatus.Success)
                    continue;

                using var reader = DataReader.FromBuffer(read.Value);
                if (reader.UnconsumedBufferLength < 1)
                    continue;

                int percent = reader.ReadByte();
                if (percent is < 0 or > 100)
                    continue;

                var connected = device.ConnectionStatus == BluetoothConnectionStatus.Connected;
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
