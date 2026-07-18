using System.Diagnostics;
using BluSee.Battery;
using BluSee.Logging;

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
    private readonly SemaphoreSlim _refreshGate = new(1, 1);
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
    public Task RefreshNowAsync() => RefreshAsync("manual", _cts.Token);

    private async Task RunAsync(CancellationToken ct)
    {
        _timer = new PeriodicTimer(interval);
        try
        {
            await RefreshAsync("startup", ct); // first reading right away
            while (await _timer.WaitForNextTickAsync(ct))
                await RefreshAsync("timer", ct);
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

    private async Task RefreshAsync(string trigger, CancellationToken ct)
    {
        // Coalesce overlapping refreshes (timer tick + menu Refresh): two concurrent polls open
        // two HID++ transports to the same receiver and cross-slow each other into minutes.
        if (!await _refreshGate.WaitAsync(0, ct))
        {
            DebugLog.Write("poll", $"refresh ({trigger}) skipped: another refresh is already running");
            return;
        }

        try
        {
            await RefreshCoreAsync(trigger, ct);
        }
        finally
        {
            _refreshGate.Release();
        }
    }

    private async Task RefreshCoreAsync(string trigger, CancellationToken ct)
    {
        DebugLog.Write("poll", $"refresh started ({trigger})");
        var total = Stopwatch.StartNew();
        var merged = new List<DeviceBattery>();
        foreach (var provider in providers)
        {
            ct.ThrowIfCancellationRequested();
            var sw = Stopwatch.StartNew();
            try
            {
                var devices = await provider.GetDevicesAsync(ct);
                if (DebugLog.Enabled)
                {
                    DebugLog.Write("poll", $"{provider.Name}: {devices.Count} device(s) in {sw.ElapsedMilliseconds} ms");
                    foreach (var d in devices)
                        DebugLog.Write("poll", $"  {d.Name}: {(d.HasBattery ? $"{d.BatteryPercent}%" : "n/a")}, connected={d.IsConnected}, source={d.Source}, id={d.Id}");
                }

                merged.AddRange(devices);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                // a failing provider must not break the others
                DebugLog.Write("poll", $"{provider.Name} FAILED after {sw.ElapsedMilliseconds} ms: {ex.GetType().Name}: {ex.Message}");
            }
        }

        if (cache is not null)
        {
            // A sleeping device may answer battery but not its name: reuse the persisted name
            // instead of showing (and re-persisting) the provider's synthetic fallback.
            for (var i = 0; i < merged.Count; i++)
                merged[i] = cache.ResolveName(merged[i]);

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

        if (DebugLog.Enabled)
            DebugLog.Write("poll", $"refresh done ({trigger}) in {total.ElapsedMilliseconds} ms: {string.Join("; ", Current.Select(d => $"{d.Name}={(d.HasBattery ? $"{d.BatteryPercent}%" : "n/a")}{(d.IsConnected ? "" : " (cached)")}"))}");

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
