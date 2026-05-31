using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using Windows.Devices.Enumeration;
using Windows.Devices.HumanInterfaceDevice;

namespace BluSee.Battery.Hidpp;

/// <summary>One receiver's HID++ collections, grouped by the shared device instance.</summary>
public sealed record HidppReceiverGroup(string Key, IReadOnlyList<string> InterfacePaths);

/// <summary>
/// HID++ transport over a Logitech receiver. Windows splits the channel into two top-level
/// collections: usage 0x0001 carries short reports (0x10), usage 0x0002 carries long reports (0x11).
/// We open both, route a frame to the collection that owns its report id, and merge replies from both
/// readers into one queue (a short request may be answered with a long report and vice versa).
/// Opened with Win32 CreateFile + shared access so it coexists with Logitech software.
/// </summary>
public sealed class HidppTransport : IAsyncDisposable
{
    private const ushort VendorUsagePage = 0xFF00;
    private const ushort ShortUsageId = 0x0001;
    private const ushort LongUsageId = 0x0002;
    public const byte ShortReportId = 0x10;
    public const byte LongReportId = 0x11;

    private sealed record Endpoint(SafeFileHandle Handle, FileStream Stream, int InLength, int OutLength, ushort Usage);

    private readonly List<Endpoint> _endpoints;
    private readonly System.Threading.Channels.Channel<byte[]> _incoming =
        System.Threading.Channels.Channel.CreateUnbounded<byte[]>();
    private readonly CancellationTokenSource _readCts = new();
    private readonly List<Task> _readers = [];
    private readonly SemaphoreSlim _io = new(1, 1);

    private HidppTransport(List<Endpoint> endpoints, ushort vendorId, ushort productId)
    {
        _endpoints = endpoints;
        VendorId = vendorId;
        ProductId = productId;
        foreach (var ep in _endpoints)
            _readers.Add(ReadLoopAsync(ep, _readCts.Token));
    }

    public ushort VendorId { get; }
    public ushort ProductId { get; }

    /// <summary>Enumerate Logitech HID++ vendor collections and group them per receiver.</summary>
    public static async Task<IReadOnlyList<HidppReceiverGroup>> FindReceiverGroupsAsync(CancellationToken ct)
    {
        var paths = new List<string>();
        foreach (var usage in (ushort[])[ShortUsageId, LongUsageId])
        {
            var selector = HidDevice.GetDeviceSelector(VendorUsagePage, usage);
            var found = await DeviceInformation.FindAllAsync(selector).AsTask(ct);
            foreach (var info in found)
                if (!paths.Contains(info.Id, StringComparer.OrdinalIgnoreCase))
                    paths.Add(info.Id);
        }

        return paths
            .GroupBy(GroupKey, StringComparer.OrdinalIgnoreCase)
            .Select(g => new HidppReceiverGroup(g.Key, g.ToList()))
            .ToList();
    }

    // Both collections of a receiver share everything except the &ColNN and trailing &000N segments.
    private static string GroupKey(string interfacePath)
    {
        var parts = interfacePath.Split('#');
        if (parts.Length < 3)
            return interfacePath;
        var collection = System.Text.RegularExpressions.Regex.Replace(parts[1], "&Col[0-9A-Fa-f]{2}", "");
        var instance = System.Text.RegularExpressions.Regex.Replace(parts[2], "&[0-9A-Fa-f]{4}$", "");
        return $"{collection}|{instance}";
    }

    /// <summary>Open all collections of a receiver group, or null if none could be opened.</summary>
    public static Task<HidppTransport?> OpenAsync(HidppReceiverGroup group, CancellationToken ct)
    {
        var endpoints = new List<Endpoint>();
        ushort vid = 0, pid = 0;

        foreach (var path in group.InterfacePaths)
        {
            var handle = NativeHid.CreateFile(
                path,
                NativeHid.GenericRead | NativeHid.GenericWrite,
                NativeHid.FileShareRead | NativeHid.FileShareWrite,
                IntPtr.Zero, NativeHid.OpenExisting, NativeHid.FileFlagOverlapped, IntPtr.Zero);

            if (handle.IsInvalid)
            {
                handle.Dispose();
                continue;
            }

            var attrs = new NativeHid.HiddAttributes { Size = Marshal.SizeOf<NativeHid.HiddAttributes>() };
            if (NativeHid.HidD_GetAttributes(handle, ref attrs))
            {
                vid = attrs.VendorId;
                pid = attrs.ProductId;
            }

            int inLen = 20, outLen = 20;
            ushort usage = 0;
            if (NativeHid.TryGetCaps(handle, out var caps))
            {
                inLen = caps.InputReportByteLength > 0 ? caps.InputReportByteLength : 20;
                outLen = caps.OutputReportByteLength > 0 ? caps.OutputReportByteLength : 20;
                usage = caps.Usage;
            }

            NativeHid.HidD_SetNumInputBuffers(handle, 64);
            var stream = new FileStream(handle, FileAccess.ReadWrite, inLen, isAsync: true);
            endpoints.Add(new Endpoint(handle, stream, inLen, outLen, usage));
        }

        if (endpoints.Count == 0)
            return Task.FromResult<HidppTransport?>(null);

        return Task.FromResult<HidppTransport?>(new HidppTransport(endpoints, vid, pid));
    }

    /// <summary>Send a HID++ frame (byte 0 = report id) and await the first matching reply.</summary>
    public async Task<byte[]?> RequestAsync(byte[] frame, Func<byte[], bool> match, TimeSpan timeout, CancellationToken ct)
    {
        await _io.WaitAsync(ct);
        try
        {
            // Drop stale/unsolicited reports queued before this request.
            while (_incoming.Reader.TryRead(out _)) { }

            await WriteFrameAsync(frame, ct);

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(timeout);
            try
            {
                while (await _incoming.Reader.WaitToReadAsync(cts.Token))
                    while (_incoming.Reader.TryRead(out var reply))
                        if (match(reply))
                            return reply;
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                return null; // timed out waiting for a matching reply
            }

            return null;
        }
        finally
        {
            _io.Release();
        }
    }

    private async Task WriteFrameAsync(byte[] frame, CancellationToken ct)
    {
        // Route by report id: 0x11 -> long collection, 0x10 -> short collection (fallback to the other).
        var wantUsage = frame[0] == LongReportId ? LongUsageId : ShortUsageId;
        var ep = _endpoints.FirstOrDefault(e => e.Usage == wantUsage) ?? _endpoints[0];

        var buffer = new byte[ep.OutLength];
        Array.Copy(frame, buffer, Math.Min(frame.Length, ep.OutLength));
        await ep.Stream.WriteAsync(buffer, ct);
        await ep.Stream.FlushAsync(ct);
    }

    private async Task ReadLoopAsync(Endpoint ep, CancellationToken ct)
    {
        var buffer = new byte[ep.InLength];
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var read = await ep.Stream.ReadAsync(buffer, ct);
                if (read <= 0)
                    continue;
                _incoming.Writer.TryWrite(buffer[..read]);
            }
        }
        catch (OperationCanceledException) { }
        catch (IOException) { } // device removed / collection closed
    }

    public async ValueTask DisposeAsync()
    {
        await _readCts.CancelAsync();
        foreach (var ep in _endpoints)
            await ep.Stream.DisposeAsync();
        try { await Task.WhenAll(_readers); } catch { /* readers cancelled */ }
        foreach (var ep in _endpoints)
            ep.Handle.Dispose();
        _readCts.Dispose();
        _io.Dispose();
    }
}
