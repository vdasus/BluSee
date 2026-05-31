namespace BluSee.Battery;

/// <summary>
/// WinRT <c>DeviceInformation.Properties</c> keys.
/// WinRT does not return "all" properties — they must be requested by name in additionalProperties.
/// In Stage 1 we request both battery-key candidates: the real one depends on driver/device.
/// </summary>
public static class DeviceProperties
{
    // Battery-level key candidates (DEVPKEY battery). Verify both on hardware (see claudeplan.md).
    public const string BatteryLevel2 = "{104EA319-6EE2-4701-BD47-8D0F1493C853} 2";
    public const string BatteryLevel9 = "{104EA319-6EE2-4701-BD47-8D0F1493C853} 9";

    // Transport / association endpoint state.
    public const string AepProtocolId = "System.Devices.Aep.ProtocolId";
    public const string AepIsConnected = "System.Devices.Aep.IsConnected";
    public const string AepIsPaired = "System.Devices.Aep.IsPaired";
    public const string ContainerId = "System.Devices.ContainerId";
    public const string ItemNameDisplay = "System.ItemNameDisplay";

    /// <summary>Property bag requested for every device in Stage 1.</summary>
    public static readonly string[] Requested =
    [
        BatteryLevel2,
        BatteryLevel9,
        AepProtocolId,
        AepIsConnected,
        AepIsPaired,
        ContainerId,
        ItemNameDisplay,
    ];

    // Association endpoint ProtocolId GUIDs (used to resolve transport).
    public static readonly Guid BluetoothProtocol = new("e0cbf06c-cd8b-4647-bb8a-263b43f0f974");
    public static readonly Guid BluetoothLeProtocol = new("bb7bb05e-5972-42b5-94fc-76eaa7084d49");

    /// <summary>Read the battery value from the property bag using any of the candidate keys.</summary>
    public static int? ReadBatteryPercent(IReadOnlyDictionary<string, object?> props)
    {
        foreach (var key in (string[])[BatteryLevel2, BatteryLevel9])
        {
            if (props.TryGetValue(key, out var raw) && raw is not null)
            {
                try
                {
                    var value = Convert.ToInt32(raw);
                    if (value is >= 0 and <= 100)
                        return value;
                }
                catch (Exception ex) when (ex is FormatException or InvalidCastException or OverflowException)
                {
                    // non-numeric value under this key — try the next candidate
                }
            }
        }

        return null;
    }

    public static DeviceTransport ResolveTransport(IReadOnlyDictionary<string, object?> props)
    {
        if (props.TryGetValue(AepProtocolId, out var raw) && raw is Guid protocol)
        {
            if (protocol == BluetoothLeProtocol) return DeviceTransport.BluetoothLowEnergy;
            if (protocol == BluetoothProtocol) return DeviceTransport.BluetoothClassic;
        }

        return DeviceTransport.Unknown;
    }
}
