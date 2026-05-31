using BluSee.Battery;

namespace BluSee.Monitoring;

/// <summary>
/// Polls all battery providers on an interval and merges their results into one device list.
/// Battery level on wireless devices changes slowly, so the default interval is minutes, not seconds
/// (frequent polling drains the device itself). Raises <see cref="Updated"/> after every refresh.
/// </summary>
public sealed class BatteryMonitor(IReadOnlyList<IBatteryProvider> providers, TimeSpan interval)
{
    private readonly CancellationTokenSource _cts = new();

    /// <summary>Latest merged snapshot. Replaced on every refresh.</summary>
    public IReadOnlyList<DeviceBattery> Current { get; private set; } = [];

    /// <summary>Raised after each refresh (on a background thread — marshal before touching UI).</summary>
    public event Action<IReadOnlyList<DeviceBattery>>? Updated;

    /// <summary>Start the background poll loop (returns immediately).</summary>
    public void Start() => _ = Task.Run(() => RunAsync(_cts.Token));

    public void Stop() => _cts.Cancel();

    /// <summary>Force an immediate refresh outside the timer (e.g. menu "Refresh").</summary>
    public Task RefreshNowAsync() => RefreshAsync(_cts.Token);

    private async Task RunAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(interval);
        try
        {
            await RefreshAsync(ct); // first reading right away
            while (await timer.WaitForNextTickAsync(ct))
                await RefreshAsync(ct);
        }
        catch (OperationCanceledException)
        {
            // stopped
        }
    }

    private async Task RefreshAsync(CancellationToken ct)
    {
        var merged = new List<DeviceBattery>();
        foreach (var provider in providers)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                merged.AddRange(await provider.GetDevicesAsync(ct));
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch
            {
                // a failing provider must not break the others
            }
        }

        // Dedup by display name, preferring a real battery value over n/a.
        Current = merged
            .GroupBy(d => d.Name, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.OrderByDescending(d => d.HasBattery).First())
            .OrderBy(d => d.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        Updated?.Invoke(Current);
    }

    /// <summary>Lowest battery across connected devices that report one (drives the tray icon).</summary>
    public static int? LowestPercent(IReadOnlyList<DeviceBattery> devices)
    {
        var values = devices.Where(d => d is { HasBattery: true, IsConnected: true })
            .Select(d => d.BatteryPercent!.Value)
            .ToList();
        return values.Count == 0 ? null : values.Min();
    }
}
