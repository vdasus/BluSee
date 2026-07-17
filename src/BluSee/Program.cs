#if DEBUG
using BluSee.Diagnostics;
#endif
using BluSee.Tray;
using BluSee.Tray.Win32;

namespace BluSee;

internal static class Program
{
    private const string MutexName = "BluSee.SingleInstance.v1";

    [STAThread]
    private static async Task<int> Main(string[] args)
    {
        // Single instance — a second launch should not add a second tray icon.
        using var mutex = new Mutex(initiallyOwned: true, MutexName, out var isNew);
        if (!isNew)
            return 0;

#if DEBUG
        // Diagnostic modes ship only in Debug builds; the released portable exe is tray-only.
        if (args.Contains("--diag", StringComparer.OrdinalIgnoreCase))
        {
            using var cts = new CancellationTokenSource();
            await DiagnosticsRunner.RunAsync(cts.Token);
            return 0;
        }

        if (args.Contains("--stress", StringComparer.OrdinalIgnoreCase))
        {
            using var cts = new CancellationTokenSource();
            await DiagnosticsRunner.StressRunAsync(cts.Token);
            return 0;
        }
#endif

        // No await runs before this point, so we are still on the STA entry thread for the UI loop.
        using var app = new TrayApp();
        while (Native.GetMessageW(out var msg, 0, 0, 0) > 0)
        {
            Native.TranslateMessage(in msg);
            Native.DispatchMessageW(in msg);
        }

        return 0;
    }
}
