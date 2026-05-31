using BluSee.Battery;
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
        Console.WriteLine("== BluSee diagnostics ==");
        Console.WriteLine("Enumerating HID + Bluetooth device nodes (Kind=Device)...");
        Console.WriteLine();

        var devices = await PnpBatteryProvider.EnumerateAsync(ct);
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

        // 3) Compact line list of everything else (name + instance id + battery flag).
        Console.WriteLine(new string('=', 60));
        Console.WriteLine("ALL NODES (compact):");
        foreach (var info in devices)
        {
            var name = string.IsNullOrWhiteSpace(info.Name) ? "(no name)" : info.Name;
            var battery = HasBatteryKey(info) ? "[BATTERY]" : "         ";
            info.Properties.TryGetValue("System.Devices.DeviceInstanceId", out var iid);
            Console.WriteLine($"  {battery} {name,-28} {iid}");
        }

        // 4) Parsed result via the provider.
        Console.WriteLine(new string('=', 60));
        Console.WriteLine("Parsed battery results (PnP provider):");
        var parsed = await new PnpBatteryProvider().GetDevicesAsync(ct);
        if (parsed.Count == 0)
            Console.WriteLine("  (no battery values parsed)");
        else
            foreach (var d in parsed)
                Console.WriteLine($"  [{d.Transport}] {d.Display}  connected={d.IsConnected}");
    }

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
