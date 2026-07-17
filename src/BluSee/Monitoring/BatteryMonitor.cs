using BluSee.Battery;

namespace BluSee.Monitoring;

/// <summary>
/// Polls all battery providers on an interval and merges their results into one device list.
/// Battery level on wireless devices changes slowly, so the default interval is minutes, not seconds
/// (frequent polling drains the device itself). Raises <see cref="Updated"/> after every refresh.
/// With a <see cref="DeviceCache"/>, devices missing from a poll (asleep) are re-emitted with their
/// last persisted reading, and <see cref="Current"/> starts pre-seeded from the previous run.
/// </summary>
public sealed class BatteryMonitor(IReadOnlyList<IBatteryProvider> providers, TimeSpan interval, DeviceCache? cache = null)
{
    private readonly CancellationTokenSource _cts = new();
    private PeriodicTimer? _timer;

    /// <summary>Change the poll cadence on the fly (applies to the running timer).</summary>
    public void SetInterval(TimeSpan value)
    {
        if (_timer is not null)
            _timer.Period = value;
    }

    /// <summary>Latest merged snapshot. Replaced on every refresh.</summary>
    public IReadOnlyList<DeviceBattery> Current { get; private set; } = cache?.Devices ?? [];

    /// <summary>Raised after each refresh (on a background thread — marshal before touching UI).</summary>
    public event Action<IReadOnlyList<DeviceBattery>>? Updated;

    /// <summary>Start the background poll loop (returns immediately).</summary>
    public void Start() => _ = Task.Run(() => RunAsync(_cts.Token));

    public void Stop() => _cts.Cancel();

    /// <summary>Force an immediate refresh outside the timer (e.g. menu "Refresh").</summary>
    public Task RefreshNowAsync() => RefreshAsync(_cts.Token);

    private async Task RunAsync(CancellationToken ct)
    {
        _timer = new PeriodicTimer(interval);
        try
        {
            await RefreshAsync(ct); // first reading right away
            while (await _timer.WaitForNextTickAsync(ct))
                await RefreshAsync(ct);
        }
        catch (OperationCanceledException)
        {
            // stopped
        }
        finally
        {
            _timer.Dispose();
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

        if (cache is not null)
        {
            cache.Update(merged);

            // Re-emit remembered devices this poll did not see (asleep or provider hiccup).
            var seen = merged.Select(d => d.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var cached in cache.Devices)
                if (!seen.Contains(cached.Id))
                    merged.Add(cached);
        }

        // Dedup by display name, preferring a real battery value over n/a, and a live reading over
        // a cached (disconnected) one.
        Current = merged
            .GroupBy(d => d.Name, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.OrderByDescending(d => d.HasBattery).ThenByDescending(d => d.IsConnected).First())
            .OrderBy(d => d.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        Updated?.Invoke(Current);
    }

    /// <summary>
    /// Lowest battery across devices that report one (drives the tray icon). Includes cached values
    /// of sleeping wireless devices (IsConnected=false) — otherwise a dozing keyboard would drop out
    /// of the icon even though its last known level still matters.
    /// </summary>
    public static int? LowestPercent(IReadOnlyList<DeviceBattery> devices)
    {
        var values = devices.Where(d => d.HasBattery)
            .Select(d => d.BatteryPercent!.Value)
            .ToList();
        return values.Count == 0 ? null : values.Min();
    }
}
