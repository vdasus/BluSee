namespace BluSee.Battery;

/// <summary>
/// Fallback provider for BLE devices whose battery is not cached in the OS PnP property.
/// Reads GATT Battery Service (0x180F) characteristic Battery Level (0x2A19) from connected devices.
/// </summary>
/// <remarks>
/// Stage 1 placeholder. Implementation is enabled only after diagnostics show which BLE devices
/// are missed by <see cref="PnpBatteryProvider"/>, to avoid waking sleeping devices unnecessarily.
/// </remarks>
public sealed class BleGattProvider : IBatteryProvider
{
    public string Name => "BLE GATT";

    public Task<IReadOnlyList<DeviceBattery>> GetDevicesAsync(CancellationToken ct)
        => Task.FromResult<IReadOnlyList<DeviceBattery>>([]);
}
