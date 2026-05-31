using BluSee.Battery;
using BluSee.Battery.Hidpp;
using Windows.Devices.Enumeration;

namespace BluSee.Diagnostics;

/// <summary>
/// Stage 1 console diagnostic. Enumerates Kind=Device devnodes (where the battery DEVPKEY lives) and
/// reports which nodes expose a battery key, so we can lock the real property key on real hardware.
/// </summary>
public static class DiagnosticsRunner
{
    private const string BatteryKeyPrefix = "{104EA319";

    public static async Task RunAsync(CancellationToken ct)
    {
        // Tee output to a log file so the full run survives without racing the console buffer.
        var logPath = Path.Combine(AppContext.BaseDirectory, "blusee-diag.log");
        var original = Console.Out;
        await using var file = new StreamWriter(logPath, append: false) { AutoFlush = true };
        Console.SetOut(new TeeTextWriter(original, file));
        try
        {
            await RunCoreAsync(ct);
        }
        finally
        {
            Console.SetOut(original);
            Console.WriteLine($"Full log written to: {logPath}");
        }
    }

    /// <summary>
    /// Stress mode: repeatedly read battery + name for the known device indices, logging raw bytes and
    /// parsed percent each iteration. Used to verify reply correlation is stable (no value flipping).
    /// </summary>
    public static async Task StressRunAsync(CancellationToken ct)
    {
        var logPath = Path.Combine(AppContext.BaseDirectory, "blusee-stress.log");
        var original = Console.Out;
        await using var file = new StreamWriter(logPath, append: false) { AutoFlush = true };
        Console.SetOut(new TeeTextWriter(original, file));
        try
        {
            Console.WriteLine($"== BluSee stress {DateTime.Now:O} ==");
            var groups = await HidppTransport.FindReceiverGroupsAsync(ct);

            for (var iter = 1; iter <= 10 && !ct.IsCancellationRequested; iter++)
            {
                Console.WriteLine($"-- iteration {iter} --");
                foreach (var group in groups)
                {
                    await using var transport = await HidppTransport.OpenAsync(group, ct);
                    if (transport is null || transport.VendorId != 0x046D)
                        continue;

                    var client = new HidppClient(transport);
                    for (byte idx = 1; idx <= 2; idx++)
                    {
                        var gf = await client.DebugGetFeatureAsync(idx, 0x1004, ct);
                        if (gf is null || gf[2] == 0xFF || gf[4] == 0)
                        {
                            Console.WriteLine($"  dev#{idx}: no UnifiedBattery");
                            continue;
                        }

                        // Interleave a name read with the battery read — this is what exposed the
                        // cross-request correlation bug; the parsed percent must stay constant.
                        var call = await client.DebugCallAsync(idx, gf[4], 0x01, ct);
                        var name = await client.ReadDeviceNameAsync(idx, ct);
                        var raw = call is null ? "null" : Convert.ToHexString(call);
                        var pct = call is null ? -1 : call[4];
                        Console.WriteLine($"  dev#{idx} pct={pct} raw={raw} name='{name}'");
                    }
                }
            }
        }
        finally
        {
            Console.SetOut(original);
            Console.WriteLine($"Stress log written to: {logPath}");
        }
    }

    private static async Task RunCoreAsync(CancellationToken ct)
    {
        Console.WriteLine("== BluSee diagnostics ==");
        Console.WriteLine("Exhaustive sweep of ALL device nodes (Kind=Device)...");
        Console.WriteLine();

        var devices = await PnpBatteryProvider.EnumerateAllAsync(ct);
        Console.WriteLine($"Found {devices.Count} device nodes.");
        Console.WriteLine();

        // 1) The answer we are hunting: any node carrying a battery key.
        var withBattery = devices.Where(HasBatteryKey).ToList();
        Console.WriteLine(new string('=', 60));
        Console.WriteLine($"NODES WITH A BATTERY KEY: {withBattery.Count}");
        Console.WriteLine();
        foreach (var info in withBattery)
            DumpFull(info);

        if (withBattery.Count == 0)
            Console.WriteLine("  (none — Windows exposes no battery DEVPKEY for these devices)");

        // 2) Full dump for likely input devices, so we can spot an alternative key.
        Console.WriteLine(new string('=', 60));
        Console.WriteLine("FULL DUMP — likely input devices (Logitech / mouse / keyboard):");
        Console.WriteLine();
        foreach (var info in devices.Where(IsLikelyInput).Where(d => !HasBatteryKey(d)))
            DumpFull(info);

        // 3) Compact line list of relevant nodes (battery or likely input) — name + id + flag.
        Console.WriteLine(new string('=', 60));
        Console.WriteLine("RELEVANT NODES (compact):");
        foreach (var info in devices.Where(d => HasBatteryKey(d) || IsLikelyInput(d)))
        {
            var name = string.IsNullOrWhiteSpace(info.Name) ? "(no name)" : info.Name;
            var battery = HasBatteryKey(info) ? "[BATTERY]" : "         ";
            info.Properties.TryGetValue("System.Devices.DeviceInstanceId", out var iid);
            Console.WriteLine($"  {battery} {name,-28} {iid}");
        }

        // 4) Parsed result via the PnP provider.
        Console.WriteLine(new string('=', 60));
        Console.WriteLine("Parsed battery results (PnP provider):");
        var parsed = await new PnpBatteryProvider().GetDevicesAsync(ct);
        if (parsed.Count == 0)
            Console.WriteLine("  (no battery values parsed)");
        else
            foreach (var d in parsed)
                Console.WriteLine($"  [{d.Transport}] {d.Display}  connected={d.IsConnected}");

        // 5) HID++ probe (primary path for Logitech receivers).
        Console.WriteLine(new string('=', 60));
        Console.WriteLine("HID++ probe (Logitech receivers):");
        await ProbeHidppAsync(ct);
    }

    private static async Task ProbeHidppAsync(CancellationToken ct)
    {
        var groups = await HidppTransport.FindReceiverGroupsAsync(ct);
        Console.WriteLine($"  HID++ receivers found: {groups.Count}");
        Console.WriteLine();

        foreach (var group in groups)
        {
            ct.ThrowIfCancellationRequested();
            Console.WriteLine($"  receiver {group.Key} ({group.InterfacePaths.Count} collections)");

            HidppTransport? transport = null;
            try
            {
                transport = await HidppTransport.OpenAsync(group, ct);
                if (transport is null)
                {
                    Console.WriteLine("    open FAILED (busy/denied — Logitech software may hold it exclusively)");
                    continue;
                }

                Console.WriteLine($"    opened: VID=0x{transport.VendorId:X4} PID=0x{transport.ProductId:X4}");
                var client = new HidppClient(transport);

                // Per device index: ping, then immediately dump battery feature resolution + call.
                // Inline so connected devices (1,2) print before the slow no-reply indices (3..6).
                for (byte index = 1; index <= 6; index++)
                {
                    var raw = await client.DebugRootPingAsync(index, ct);
                    Console.WriteLine($"    dev#{index} rootPing: {(raw is null ? "(no reply)" : Hex(raw))}");
                    Console.Out.Flush();
                    if (raw is null)
                        continue;

                    foreach (var feature in (ushort[])[0x1004, 0x1000])
                    {
                        var getFeat = await client.DebugGetFeatureAsync(index, feature, ct);
                        Console.WriteLine($"      getFeature(0x{feature:X4}): {(getFeat is null ? "(no reply)" : Hex(getFeat))}");
                        if (getFeat is null || getFeat[2] == 0xFF || getFeat[4] == 0)
                            continue;

                        var funcId = feature == 0x1004 ? (byte)0x01 : (byte)0x00;
                        var call = await client.DebugCallAsync(index, getFeat[4], funcId, ct);
                        Console.WriteLine($"      call(0x{feature:X4} fn{funcId}): {(call is null ? "(no reply)" : Hex(call))}");
                        Console.Out.Flush();
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"    probe error: {ex.GetType().Name}: {ex.Message}");
            }
            finally
            {
                if (transport is not null)
                    await transport.DisposeAsync();
            }

            Console.WriteLine();
        }
    }

    private static string Hex(byte[] b) => Convert.ToHexString(b);

    private static bool HasBatteryKey(DeviceInformation info)
        => info.Properties.Any(p =>
            p.Key.StartsWith(BatteryKeyPrefix, StringComparison.OrdinalIgnoreCase) && p.Value is not null);

    private static bool IsLikelyInput(DeviceInformation info)
    {
        var name = info.Name ?? "";
        info.Properties.TryGetValue("System.Devices.DeviceInstanceId", out var iidObj);
        var iid = iidObj as string ?? "";
        return name.Contains("mouse", StringComparison.OrdinalIgnoreCase)
            || name.Contains("keyboard", StringComparison.OrdinalIgnoreCase)
            || name.Contains("logi", StringComparison.OrdinalIgnoreCase)
            || name.Contains("receiver", StringComparison.OrdinalIgnoreCase)
            || iid.Contains("VID_046D", StringComparison.OrdinalIgnoreCase); // Logitech
    }

    private static void DumpFull(DeviceInformation info)
    {
        Console.WriteLine($"* {(string.IsNullOrWhiteSpace(info.Name) ? "(no name)" : info.Name)}");
        Console.WriteLine($"    Id: {info.Id}");
        foreach (var (key, value) in info.Properties)
        {
            if (value is null)
                continue;
            var marker = key.StartsWith(BatteryKeyPrefix, StringComparison.OrdinalIgnoreCase) ? "  <== BATTERY" : "";
            Console.WriteLine($"    {key} = {value} ({value.GetType().Name}){marker}");
        }

        Console.WriteLine();
    }
}
