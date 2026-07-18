using System.Text;
using BluSee.Logging;

namespace BluSee.Battery.Hidpp;

/// <summary>Battery reading for one device behind a receiver, via HID++ 2.0 feature calls.</summary>
public sealed record HidppBatteryReading(byte DeviceIndex, int Percent, ushort Feature, string? Name);

/// <summary>
/// Minimal HID++ 2.0 client: resolves features through the root feature (0x0000) and reads battery
/// via UnifiedBattery (0x1004) or the legacy BatteryStatus (0x1000). Speaks short reports (0x10).
/// The optional <paramref name="featureIndexCache"/> (owned by the provider, survives across polls)
/// skips the root.getFeature round-trip for already-resolved features — a Bolt receiver has one
/// outgoing RF queue and every saved frame is seconds saved when a dozing device clogs it.
/// </summary>
public sealed class HidppClient(HidppTransport transport, Dictionary<(byte DeviceIndex, ushort Feature), byte>? featureIndexCache = null)
{
    private const byte RootFeatureIndex = 0x00;
    private const ushort FeatureDeviceName = 0x0005;
    private const ushort FeatureUnifiedBattery = 0x1004;
    private const ushort FeatureBatteryStatus = 0x1000;
    private const byte ErrorIndex = 0xFF;

    // HID++ 1.0 error marker (sub-id 0x8F). The receiver answers a request to an empty device slot
    // with this instantly (e.g. err 0x09 unknown device); treating it as a reply saves a full
    // 500 ms timeout per request — with 6 slots and 2 battery features that is seconds per poll.
    private const byte LegacyErrorSubId = 0x8F;

    // Reply window. Empty receiver slots answer instantly (HID++ 1.0 error, matched), so a longer
    // window costs nothing there — but a drowsy device that just woke answers in ~0.5-1.5 s, and
    // a 500 ms window dropped those replies as "unmatched" right after the timeout (seen in v0.3.3
    // debug logs), wasting a poll the device was actually reachable in.
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(2);

    // Rotating software id (1..15) tags each request so a reply can be correlated to it. A constant
    // id let a late reply from one request (e.g. DeviceName getCount) satisfy a different request
    // (battery), producing wrong values — the rotation makes every in-flight request distinct.
    private byte _swId;

    private byte NextSwId() => _swId = (byte)(_swId % 15 + 1);

    // After this many requests in a row that the receiver refused to accept (write timeouts), its
    // RF buffer is clogged — further probing this poll only adds 3 s stalls per request.
    private const int MaxConsecutiveWriteTimeouts = 3;

    /// <summary>Probe device indices 1..6 on the receiver and return any with a battery reading.</summary>
    public async Task<IReadOnlyList<HidppBatteryReading>> ReadAllAsync(CancellationToken ct)
        => await ReadSlotsAsync([1, 2, 3, 4, 5, 6], allowDirectFallback: true, needName: null, ct);

    /// <summary>
    /// Probe the given receiver slots. 1..6 = devices paired to a receiver; battery is probed
    /// directly with retries (a paired device may be asleep and miss the first request, so a
    /// one-shot connectivity gate would drop it). <paramref name="needName"/> lets the caller skip
    /// the multi-frame name read for slots whose name it already knows (null = always read) —
    /// fewer frames means fewer chances to clog the receiver's RF queue.
    /// </summary>
    public async Task<IReadOnlyList<HidppBatteryReading>> ReadSlotsAsync(
        IReadOnlyList<byte> slots, bool allowDirectFallback, Func<byte, bool>? needName, CancellationToken ct)
    {
        var result = new List<HidppBatteryReading>();

        foreach (var index in slots)
        {
            ct.ThrowIfCancellationRequested();
            if (transport.ConsecutiveWriteTimeouts >= MaxConsecutiveWriteTimeouts)
            {
                DebugLog.Write("hidpp", $"scan aborted before dev {index}: receiver not accepting writes");
                return result;
            }

            var reading = await ReadDeviceAsync(index, needName?.Invoke(index) ?? true, ct);
            if (reading is not null)
                result.Add(reading);
        }

        // 0xFF = a device connected directly (e.g. Bluetooth) with no receiver. Probe it only as a
        // fallback — on a real receiver it would alias an already-listed slot and create duplicates.
        if (result.Count == 0 && allowDirectFallback
            && transport.ConsecutiveWriteTimeouts < MaxConsecutiveWriteTimeouts)
        {
            var direct = await ReadDeviceAsync(0xFF, needName?.Invoke(0xFF) ?? true, ct);
            if (direct is not null)
                result.Add(direct);
        }

        return result;
    }

    public async Task<HidppBatteryReading?> ReadDeviceAsync(byte deviceIndex, CancellationToken ct)
        => await ReadDeviceAsync(deviceIndex, readName: true, ct);

    private async Task<HidppBatteryReading?> ReadDeviceAsync(byte deviceIndex, bool readName, CancellationToken ct)
    {
        // Prefer UnifiedBattery (newer), fall back to legacy BatteryStatus.
        foreach (var feature in (ushort[])[FeatureUnifiedBattery, FeatureBatteryStatus])
        {
            var featureIndex = await GetFeatureIndexAsync(deviceIndex, feature, ct);
            if (featureIndex is null)
            {
                // Only meaningful when a frame was actually sent (a cache hit returns non-null
                // without touching the transport, leaving a stale flag from the previous device).
                if (transport.LastWriteTimedOut)
                {
                    DebugLog.Write("hidpp", $"dev {deviceIndex}: unreachable (write timeout), giving up this poll");
                    return null; // the receiver is not even taking frames for it — retries only stall
                }

                continue;
            }

            if (featureIndex == 0)
                continue;

            if (DebugLog.Enabled)
                DebugLog.Write("hidpp", $"dev {deviceIndex}: feature 0x{feature:X4} at index 0x{featureIndex:X2}");

            // A sleeping device may not answer the first status request; retry a couple of times.
            for (var attempt = 1; attempt <= 3; attempt++)
            {
                var percent = await ReadPercentAsync(deviceIndex, featureIndex.Value, feature, ct);
                if (percent is not null)
                {
                    var name = readName ? await ReadDeviceNameAsync(deviceIndex, ct) : null;
                    if (DebugLog.Enabled)
                        DebugLog.Write("hidpp", $"dev {deviceIndex}: {percent}% (attempt {attempt}), name {(readName ? name is null ? "unresolved" : $"'{name}'" : "already known, skipped")}");
                    return new HidppBatteryReading(deviceIndex, percent.Value, feature, name);
                }

                if (transport.LastWriteTimedOut)
                {
                    DebugLog.Write("hidpp", $"dev {deviceIndex}: unreachable (write timeout), giving up this poll");
                    return null;
                }
            }

            if (DebugLog.Enabled)
                DebugLog.Write("hidpp", $"dev {deviceIndex}: feature 0x{feature:X4} gave no percent after 3 attempts");
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
            if (transport.LastWriteTimedOut)
                break; // link is down — the fallback name and DeviceCache cover us
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
        for (byte charIndex = 0; name.Length < count;)
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

            // Advance by what the reply actually carried: a long report holds up to 16 chars, so a
            // fixed +15 step re-reads the seam char ("MX Anywhere 3S ffor Busines") and truncates.
            charIndex += (byte)added;
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
        if (featureIndexCache?.TryGetValue((deviceIndex, featureId), out var known) is true)
            return known;

        var reply = await CallAsync(deviceIndex, RootFeatureIndex, funcId: 0x00,
            (byte)(featureId >> 8), (byte)(featureId & 0xFF), ct);
        if (reply is null || IsError(reply, deviceIndex))
            return null;

        var index = reply[4]; // featureIndex
        if (index != 0)
            featureIndexCache?[(deviceIndex, featureId)] = index;
        return index;
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
        // HID++ 1.0 error: [.. , 0x8F, failedFeatureIndex, (funcId<<4)|swId, errorCode]
        return r[2] is ErrorIndex or LegacyErrorSubId
            && r[3] == featureIndex && r.Length >= 6 && (r[4] & 0x0F) == swId;
    }

    private static bool IsError(byte[] r, byte deviceIndex)
        => r.Length >= 3 && r[1] == deviceIndex && r[2] is ErrorIndex or LegacyErrorSubId;
}
