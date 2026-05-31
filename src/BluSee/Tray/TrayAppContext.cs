using BluSee.Battery;
using BluSee.Monitoring;
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
    private readonly TrayIconRenderer _renderer = new();
    private readonly BatteryMonitor _monitor;
    private readonly AutostartManager _autostart = new();
    private readonly SynchronizationContext _ui;
    private readonly ToolStripMenuItem _autostartItem;
    private readonly HashSet<string> _lowNotified = new(StringComparer.OrdinalIgnoreCase);

    public TrayAppContext()
    {
        _ui = SynchronizationContext.Current ?? new SynchronizationContext();

        _autostartItem = new ToolStripMenuItem("Start with Windows", null, OnToggleAutostart)
        {
            Checked = _autostart.IsEnabled(),
            CheckOnClick = true,
        };

        var menu = new ContextMenuStrip();
        menu.Opening += (_, _) => ThemeMenu.Apply(menu, ThemeReader.IsLightTheme());
        _icon = new NotifyIcon
        {
            Text = "BluSee",
            Visible = true,
            ContextMenuStrip = menu,
        };
        _renderer.Apply(_icon, null, ThemeReader.IsLightTheme());

        IReadOnlyList<IBatteryProvider> providers =
        [
            new HidppBatteryProvider(),
            new PnpBatteryProvider(),
            new BleGattProvider(),
        ];
        _monitor = new BatteryMonitor(providers, TimeSpan.FromMinutes(10));
        _monitor.Updated += OnMonitorUpdated;

        BuildMenu([]);
        _monitor.Start();
    }

    private void OnMonitorUpdated(IReadOnlyList<DeviceBattery> devices)
        => _ui.Post(_ => Apply(devices), null);

    private void Apply(IReadOnlyList<DeviceBattery> devices)
    {
        var lowest = BatteryMonitor.LowestPercent(devices);
        _renderer.Apply(_icon, lowest, ThemeReader.IsLightTheme());
        _icon.Text = Truncate(lowest is null ? "BluSee — no battery data" : $"BluSee — lowest {lowest}%");
        BuildMenu(devices);
        NotifyLowBattery(devices);
    }

    private void BuildMenu(IReadOnlyList<DeviceBattery> devices)
    {
        var menu = _icon.ContextMenuStrip!;
        menu.Items.Clear();

        if (devices.Count == 0)
        {
            menu.Items.Add(new ToolStripMenuItem("No devices") { Enabled = false });
        }
        else
        {
            foreach (var d in devices)
                menu.Items.Add(new ToolStripMenuItem(d.Display) { Enabled = false });
        }

        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(new ToolStripMenuItem("Refresh", null, OnRefresh));
        menu.Items.Add(_autostartItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(new ToolStripMenuItem("Exit", null, OnExit));

        ThemeMenu.Apply(menu, ThemeReader.IsLightTheme());
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
        _renderer.Dispose();
        ExitThread();
    }

    private static string Truncate(string text) => text.Length <= 63 ? text : text[..63];
}
