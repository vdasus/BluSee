namespace BluSee.Battery;

/// <summary>How the device is connected.</summary>
public enum DeviceTransport
{
    Unknown,
    BluetoothClassic,
    BluetoothLowEnergy,
    UsbReceiver, // Logi Bolt / Unifying and other USB dongles
}

/// <summary>Which provider produced the battery value (for diagnostics/prioritization).</summary>
public enum BatterySource
{
    None,
    PnpProperty, // OS cache in the device property (BT Classic + Bolt)
    BleGatt,     // GATT Battery Service 0x180F / 0x2A19
    Hidpp,       // direct Logitech HID++ (Stage 6)
}

/// <summary>Snapshot of a device and its battery state.</summary>
/// <remarks>
/// <see cref="IsFallbackName"/> marks a synthetic provider name (e.g. "Logitech 0xC548 #1" when a
/// sleeping device answered battery but not its name) so <c>DeviceCache</c> can substitute the
/// remembered real name instead of overwriting it.
/// </remarks>
public sealed record DeviceBattery(
    string Id,
    string Name,
    DeviceTransport Transport,
    int? BatteryPercent,
    bool IsConnected,
    BatterySource Source,
    bool IsFallbackName = false)
{
    public bool HasBattery => BatteryPercent is >= 0 and <= 100;

    public string Display => HasBattery ? $"{Name} — {BatteryPercent}%" : $"{Name} — n/a";
}
