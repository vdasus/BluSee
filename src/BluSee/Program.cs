using BluSee.Diagnostics;

namespace BluSee;

internal static class Program
{
    // Stage 1: console diagnostic only. Stage 2 will switch to [STAThread] + tray ApplicationContext.
    private static async Task<int> Main(string[] args)
    {
        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            cts.Cancel();
        };

        // Default to diagnostic mode while the tray UI does not exist yet.
        if (args.Length == 0 || args.Contains("--diag", StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                await DiagnosticsRunner.RunAsync(cts.Token);
                return 0;
            }
            catch (OperationCanceledException)
            {
                Console.WriteLine("Cancelled.");
                return 1;
            }
        }

        Console.WriteLine("Tray mode not implemented yet (Stage 2). Run with --diag.");
        return 0;
    }
}
