using BluSee.Battery;
using BluSee.Monitoring;
using BluSee.Settings;
using BluSee.Startup;

namespace BluSee.Tray;

/// <summary>
/// Windowless tray application. Owns the NotifyIcon, its menu and the battery monitor, and marshals
/// monitor updates (raised on a background thread) onto the UI thread before touching WinForms.
/// </summary>
public sealed class TrayAppContext : ApplicationContext
{
    private const int LowBatteryThreshold = 15;

    private readonly NotifyIcon _icon;
    private readonly ContextMenuStrip _menu = new();
    private readonly TrayIconRenderer _renderer = new();
    private readonly BatteryMonitor _monitor;
    private readonly AutostartManager _autostart = new();
    private readonly SynchronizationContext _ui;
    private readonly ToolStripMenuItem _autostartItem;
    private readonly ToolStripMenuItem _intervalItem;
    private readonly ToolStripSeparator _deviceSeparator = new();
    private readonly AppSettings _settings = AppSettings.Load();
    private readonly HashSet<string> _lowNotified = new(StringComparer.OrdinalIgnoreCase);

    private static readonly int[] IntervalChoices = [5, 10, 15, 30, 60];

    public TrayAppContext()
    {
        _ui = SynchronizationContext.Current ?? new SynchronizationContext();

        _autostartItem = new ToolStripMenuItem("Start with Windows", null, OnToggleAutostart)
        {
            Checked = _autostart.IsEnabled(),
            CheckOnClick = true,
        };
        _intervalItem = BuildIntervalMenu();

        // Build the static part of the menu once. Only the device items (above _deviceSeparator) are
        // rebuilt on each poll — re-adding Refresh/Exit/etc. every time duplicated them in the menu.
        _menu.Items.Add(_deviceSeparator);
        _menu.Items.Add(new ToolStripMenuItem("Refresh", null, OnRefresh));
        _menu.Items.Add(_intervalItem);
        _menu.Items.Add(_autostartItem);
        _menu.Items.Add(new ToolStripSeparator());
        _menu.Items.Add(new ToolStripMenuItem("Exit", null, OnExit));
        _menu.Opening += (_, _) => ThemeMenu.Apply(_menu, ThemeReader.IsLightTheme());
        UpdateDeviceItems([]);

        _icon = new NotifyIcon
        {
            Text = "BluSee",
            Visible = true,
            ContextMenuStrip = _menu,
        };
        _renderer.Apply(_icon, null, ThemeReader.IsLightTheme());

        IReadOnlyList<IBatteryProvider> providers =
        [
            new HidppBatteryProvider(),
            new PnpBatteryProvider(),
            new BleGattProvider(),
        ];
        _monitor = new BatteryMonitor(providers, _settings.PollInterval, DeviceCache.Load());
        _monitor.Updated += OnMonitorUpdated;
        // Show last run's readings immediately (marked disconnected); the first poll replaces them.
        Apply(_monitor.Current);
        _monitor.Start();
    }

    private void OnMonitorUpdated(IReadOnlyList<DeviceBattery> devices)
        => _ui.Post(_ => Apply(devices), null);

    private void Apply(IReadOnlyList<DeviceBattery> devices)
    {
        var lowest = BatteryMonitor.LowestPercent(devices);
        _renderer.Apply(_icon, lowest, ThemeReader.IsLightTheme());
        _icon.Text = Truncate(lowest is null ? "BluSee — no battery data" : $"BluSee — lowest {lowest}%");
        UpdateDeviceItems(devices);
        NotifyLowBattery(devices);
    }

    /// <summary>Replace only the device entries (everything above <see cref="_deviceSeparator"/>).</summary>
    private void UpdateDeviceItems(IReadOnlyList<DeviceBattery> devices)
    {
        while (_menu.Items.Count > 0 && _menu.Items[0] != _deviceSeparator)
        {
            var item = _menu.Items[0];
            _menu.Items.RemoveAt(0);
            item.Dispose();
        }

        var insertAt = 0;
        if (devices.Count == 0)
        {
            _menu.Items.Insert(insertAt, new ToolStripMenuItem("No devices") { Enabled = false });
        }
        else
        {
            foreach (var d in devices)
                _menu.Items.Insert(insertAt++, new ToolStripMenuItem(d.Display) { Enabled = false });
        }

        ThemeMenu.Apply(_menu, ThemeReader.IsLightTheme());
    }

    private ToolStripMenuItem BuildIntervalMenu()
    {
        var root = new ToolStripMenuItem("Poll interval");
        foreach (var minutes in IntervalChoices)
        {
            root.DropDownItems.Add(new ToolStripMenuItem($"{minutes} min", null, OnSelectInterval)
            {
                Tag = minutes,
                Checked = minutes == _settings.PollIntervalMinutes,
            });
        }

        return root;
    }

    private void OnSelectInterval(object? sender, EventArgs e)
    {
        if (sender is not ToolStripMenuItem { Tag: int minutes })
            return;

        _settings.PollIntervalMinutes = minutes;
        _settings.TrySave();
        _monitor.SetInterval(_settings.PollInterval);

        foreach (var item in _intervalItem.DropDownItems.OfType<ToolStripMenuItem>())
            item.Checked = item.Tag is int m && m == minutes;
    }

    private void NotifyLowBattery(IReadOnlyList<DeviceBattery> devices)
    {
        foreach (var d in devices)
        {
            if (d is { HasBattery: true, IsConnected: true } && d.BatteryPercent <= LowBatteryThreshold)
            {
                if (_lowNotified.Add(d.Name))
                    _icon.ShowBalloonTip(5000, "Low battery", d.Display, ToolTipIcon.Warning);
            }
            else
            {
                _lowNotified.Remove(d.Name); // re-arm once it recovers
            }
        }
    }

    private void OnRefresh(object? sender, EventArgs e) => _ = SafeRefreshAsync();

    private async Task SafeRefreshAsync()
    {
        try
        {
            await _monitor.RefreshNowAsync();
        }
        catch
        {
            // ignore manual refresh failures
        }
    }

    private void OnToggleAutostart(object? sender, EventArgs e)
        => _autostart.SetEnabled(_autostartItem.Checked);

    private void OnExit(object? sender, EventArgs e)
    {
        _monitor.Stop();
        _icon.Visible = false;
        _icon.Dispose();
        _menu.Dispose();
        _renderer.Dispose();
        ExitThread();
    }

    private static string Truncate(string text) => text.Length <= 63 ? text : text[..63];
}
