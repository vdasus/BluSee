using BluSee.Diagnostics;
using BluSee.Tray;

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

        // No await runs before this point, so we are still on the STA entry thread for the UI loop.
        ApplicationConfiguration.Initialize();
        Application.Run(new TrayAppContext());
        return 0;
    }
}
