using System.Text;

namespace BluSee.Battery.Hidpp;

/// <summary>Battery reading for one device behind a receiver, via HID++ 2.0 feature calls.</summary>
public sealed record HidppBatteryReading(byte DeviceIndex, int Percent, ushort Feature, string? Name);

/// <summary>
/// Minimal HID++ 2.0 client: resolves features through the root feature (0x0000) and reads battery
/// via UnifiedBattery (0x1004) or the legacy BatteryStatus (0x1000). Speaks short reports (0x10).
/// </summary>
public sealed class HidppClient(HidppTransport transport)
{
    private const byte RootFeatureIndex = 0x00;
    private const ushort FeatureDeviceName = 0x0005;
    private const ushort FeatureUnifiedBattery = 0x1004;
    private const ushort FeatureBatteryStatus = 0x1000;
    private const byte ErrorIndex = 0xFF;

    private static readonly TimeSpan Timeout = TimeSpan.FromMilliseconds(500);

    // Rotating software id (1..15) tags each request so a reply can be correlated to it. A constant
    // id let a late reply from one request (e.g. DeviceName getCount) satisfy a different request
    // (battery), producing wrong values — the rotation makes every in-flight request distinct.
    private byte _swId;

    private byte NextSwId() => _swId = (byte)(_swId % 15 + 1);

    /// <summary>Probe device indices 1..6 on the receiver and return any with a battery reading.</summary>
    public async Task<IReadOnlyList<HidppBatteryReading>> ReadAllAsync(CancellationToken ct)
    {
        var result = new List<HidppBatteryReading>();
        for (byte index = 1; index <= 6; index++)
        {
            ct.ThrowIfCancellationRequested();

            // Probe battery directly with retries: a paired device may be asleep and miss the first
            // request. A cheap one-shot connectivity gate would wrongly drop such devices.
            var reading = await ReadDeviceAsync(index, ct);
            if (reading is not null)
                result.Add(reading);
        }

        return result;
    }

    public async Task<HidppBatteryReading?> ReadDeviceAsync(byte deviceIndex, CancellationToken ct)
    {
        // Prefer UnifiedBattery (newer), fall back to legacy BatteryStatus.
        foreach (var feature in (ushort[])[FeatureUnifiedBattery, FeatureBatteryStatus])
        {
            var featureIndex = await GetFeatureIndexAsync(deviceIndex, feature, ct);
            if (featureIndex is null or 0)
                continue;

            // A sleeping device may not answer the first status request; retry a couple of times.
            for (var attempt = 0; attempt < 3; attempt++)
            {
                var percent = await ReadPercentAsync(deviceIndex, featureIndex.Value, feature, ct);
                if (percent is not null)
                {
                    var name = await ReadDeviceNameAsync(deviceIndex, ct);
                    return new HidppBatteryReading(deviceIndex, percent.Value, feature, name);
                }
            }
        }

        return null;
    }

    /// <summary>
    /// Read the friendly device name via DeviceNameAndType (0x0005): getCount (fn0) then getName
    /// (fn1) in 15-char chunks. Best-effort — returns null if the feature is absent or errors.
    /// </summary>
    public async Task<string?> ReadDeviceNameAsync(byte deviceIndex, CancellationToken ct)
    {
        var featureIndex = await GetFeatureIndexAsync(deviceIndex, FeatureDeviceName, ct);
        if (featureIndex is null or 0)
            return null;

        // The device may sleep mid-sequence; retry the whole read a few times.
        for (var attempt = 0; attempt < 3; attempt++)
        {
            var name = await TryReadNameAsync(deviceIndex, featureIndex.Value, ct);
            if (name is not null)
                return name;
        }

        return null;
    }

    private async Task<string?> TryReadNameAsync(byte deviceIndex, byte featureIndex, CancellationToken ct)
    {
        var countReply = await CallAsync(deviceIndex, featureIndex, funcId: 0x00, p0: 0, p1: 0, ct);
        if (countReply is null || IsError(countReply, deviceIndex))
            return null;

        int count = countReply[4];
        if (count is <= 0 or > 64)
            return null;

        var name = new StringBuilder(count);
        for (byte charIndex = 0; name.Length < count; charIndex += 15)
        {
            var chunk = await CallAsync(deviceIndex, featureIndex, funcId: 0x01, charIndex, p1: 0, ct);
            if (chunk is null || IsError(chunk, deviceIndex))
                return null; // incomplete — let the caller retry the whole name

            // ASCII chars start at param byte 4; stop at NUL or the reported length.
            var added = 0;
            for (var i = 4; i < chunk.Length && name.Length < count; i++)
            {
                if (chunk[i] == 0)
                    break;
                name.Append((char)chunk[i]);
                added++;
            }

            if (added == 0)
                break; // no progress — avoid an infinite loop
        }

        var text = name.ToString().Trim();
        return text.Length == 0 ? null : text;
    }

    /// <summary>Diagnostic: raw root.getFeature(IFeatureSet 0x0001) reply for a device index.</summary>
    public Task<byte[]?> DebugRootPingAsync(byte deviceIndex, CancellationToken ct)
        => CallAsync(deviceIndex, RootFeatureIndex, funcId: 0x00, p0: 0x00, p1: 0x01, ct);

    /// <summary>Diagnostic: raw root.getFeature reply for an arbitrary feature id.</summary>
    public Task<byte[]?> DebugGetFeatureAsync(byte deviceIndex, ushort featureId, CancellationToken ct)
        => CallAsync(deviceIndex, RootFeatureIndex, funcId: 0x00,
            (byte)(featureId >> 8), (byte)(featureId & 0xFF), ct);

    /// <summary>Diagnostic: raw reply of calling a feature function.</summary>
    public Task<byte[]?> DebugCallAsync(byte deviceIndex, byte featureIndex, byte funcId, CancellationToken ct)
        => CallAsync(deviceIndex, featureIndex, funcId, p0: 0, p1: 0, ct);

    /// <summary>Root.getFeature: map a feature id to its per-device feature index (0 = unsupported).</summary>
    private async Task<byte?> GetFeatureIndexAsync(byte deviceIndex, ushort featureId, CancellationToken ct)
    {
        var reply = await CallAsync(deviceIndex, RootFeatureIndex, funcId: 0x00,
            (byte)(featureId >> 8), (byte)(featureId & 0xFF), ct);
        if (reply is null || IsError(reply, deviceIndex))
            return null;

        return reply[4]; // featureIndex
    }

    private async Task<int?> ReadPercentAsync(byte deviceIndex, byte featureIndex, ushort feature, CancellationToken ct)
    {
        // 0x1004 get_status = function 0x01; 0x1000 get_battery = function 0x00.
        var funcId = feature == FeatureUnifiedBattery ? (byte)0x01 : (byte)0x00;

        var reply = await CallAsync(deviceIndex, featureIndex, funcId, p0: 0, p1: 0, ct);
        if (reply is null || IsError(reply, deviceIndex))
            return null;

        // Both features place state-of-charge percent in the first parameter byte (index 4).
        var percent = reply[4];
        return percent is >= 0 and <= 100 ? percent : null;
    }

    /// <summary>Send a feature call tagged with a fresh software id and await its correlated reply.</summary>
    private Task<byte[]?> CallAsync(byte deviceIndex, byte featureIndex, byte funcId, byte p0, byte p1, CancellationToken ct)
    {
        var swId = NextSwId();
        var frame = LongFrame(deviceIndex, featureIndex, funcId, swId, p0, p1);
        return transport.RequestAsync(frame, r => Matches(r, deviceIndex, featureIndex, swId), Timeout, ct);
    }

    // Long HID++ frame (report 0x11, 20 bytes): id, deviceIndex, featureIndex, (funcId<<4)|swId, 16 params.
    private static byte[] LongFrame(byte deviceIndex, byte featureIndex, byte funcId, byte swId, byte p0, byte p1)
    {
        var frame = new byte[20];
        frame[0] = HidppTransport.LongReportId;
        frame[1] = deviceIndex;
        frame[2] = featureIndex;
        frame[3] = (byte)((funcId << 4) | swId);
        frame[4] = p0;
        frame[5] = p1;
        return frame;
    }

    /// <summary>Match a normal reply or a 2.0 error reply for the given device + feature + swId.</summary>
    private static bool Matches(byte[] r, byte deviceIndex, byte featureIndex, byte swId)
    {
        if (r.Length < 5 || r[1] != deviceIndex)
            return false;

        // normal: [.. , featureIndex, (funcId<<4)|swId, ..]
        if (r[2] == featureIndex && (r[3] & 0x0F) == swId)
            return true;

        // HID++ 2.0 error: [.. , 0xFF, failedFeatureIndex, (funcId<<4)|swId, errorCode]
        return r[2] == ErrorIndex && r[3] == featureIndex && r.Length >= 6 && (r[4] & 0x0F) == swId;
    }

    private static bool IsError(byte[] r, byte deviceIndex) => r.Length >= 3 && r[1] == deviceIndex && r[2] == ErrorIndex;
}
