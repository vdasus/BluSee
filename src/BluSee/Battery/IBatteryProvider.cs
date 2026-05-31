namespace BluSee.Battery;

/// <summary>
/// A source of battery data. The abstraction lets us test aggregation logic without WinRT
/// and keep the proprietary HID++ path isolated from the main one (BT + OS PnP property).
/// </summary>
public interface IBatteryProvider
{
    /// <summary>Human-readable provider name (for logs/diagnostics).</summary>
    string Name { get; }

    /// <summary>Take the current list of devices with battery that this provider can see.</summary>
    Task<IReadOnlyList<DeviceBattery>> GetDevicesAsync(CancellationToken ct);
}
