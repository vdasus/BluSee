using BluSee.Battery;
using BluSee.Monitoring;
using BluSee.Settings;
using BluSee.Startup;
using BluSee.Tray.Win32;

namespace BluSee.Tray;

/// <summary>
/// Windowless tray application on raw Win32: hidden message window, Shell_NotifyIcon and a native
/// popup menu (dark-mode aware via <see cref="DarkMode"/>). Monitor updates arrive on a background
/// thread and are marshalled to the UI thread by posting a window message.
/// </summary>
public sealed class TrayApp : IDisposable
{
    private const int LowBatteryThreshold = 15;

    // Menu command ids. Device rows use id 0: TrackPopupMenuEx returns 0 for "dismissed", so
    // clicking a device row is a natural no-op while keeping full-brightness (enabled) text.
    private const uint CmdRefresh = 1;
    private const uint CmdAutostart = 2;
    private const uint CmdExit = 3;
    private const uint CmdIntervalBase = 100;

    private static readonly int[] IntervalChoices = [5, 10, 15, 30, 60];

    private readonly MessageWindow _window;
    private readonly TrayIcon _tray;
    private readonly TrayIconRenderer _renderer = new();
    private readonly BatteryMonitor _monitor;
    private readonly AutostartManager _autostart = new();
    private readonly AppSettings _settings = AppSettings.Load();
    private readonly HashSet<string> _lowNotified = new(StringComparer.OrdinalIgnoreCase);

    public TrayApp()
    {
        DarkMode.EnableForProcess();

        _window = new MessageWindow();
        _window.TrayActivated += ShowMenu;
        _window.DevicesUpdated += () => Apply(_monitor!.Current);
        _window.SettingChanged += OnSettingChanged;
        _window.TaskbarCreated += () => _tray!.Readd();

        _tray = new TrayIcon(_window.Handle);
        _tray.Update(_renderer.Render(null, ThemeReader.IsLightTheme()), "BluSee");

        IReadOnlyList<IBatteryProvider> providers =
        [
            new HidppBatteryProvider(),
            new PnpBatteryProvider(),
            new BleGattProvider(),
        ];
        _monitor = new BatteryMonitor(providers, _settings.PollInterval, DeviceCache.Load());
        _monitor.Updated += _ => _window.Post(MessageWindow.DevicesUpdatedMessage);
        // Show last run's readings immediately (marked disconnected); the first poll replaces them.
        Apply(_monitor.Current);
        _monitor.Start();
    }

    private void Apply(IReadOnlyList<DeviceBattery> devices)
    {
        var lowest = BatteryMonitor.LowestPercent(devices);
        var tip = Truncate(lowest is null ? "BluSee — no battery data" : $"BluSee — lowest {lowest}%");
        _tray.Update(_renderer.Render(lowest, ThemeReader.IsLightTheme()), tip);
        NotifyLowBattery(devices);
    }

    private void OnSettingChanged()
    {
        // System theme may have flipped: re-tint the icon and refresh menu theming.
        DarkMode.Flush();
        Apply(_monitor.Current);
    }

    private void ShowMenu()
    {
        var menu = Native.CreatePopupMenu();
        try
        {
            var devices = _monitor.Current;
            if (devices.Count == 0)
            {
                Native.AppendMenuW(menu, Native.MF_STRING, 0, "No devices");
            }
            else
            {
                foreach (var d in devices)
                    Native.AppendMenuW(menu, Native.MF_STRING, 0, d.Display);
            }

            Native.AppendMenuW(menu, Native.MF_SEPARATOR, 0, null);
            Native.AppendMenuW(menu, Native.MF_STRING, CmdRefresh, "Refresh");

            var intervals = Native.CreatePopupMenu();
            foreach (var minutes in IntervalChoices)
            {
                var check = minutes == _settings.PollIntervalMinutes ? Native.MF_CHECKED : 0;
                Native.AppendMenuW(intervals, Native.MF_STRING | check, CmdIntervalBase + (uint)minutes, $"{minutes} min");
            }

            Native.AppendMenuW(menu, Native.MF_POPUP, (nuint)intervals, "Poll interval");

            var autostartCheck = _autostart.IsEnabled() ? Native.MF_CHECKED : 0;
            Native.AppendMenuW(menu, Native.MF_STRING | autostartCheck, CmdAutostart, "Start with Windows");
            Native.AppendMenuW(menu, Native.MF_SEPARATOR, 0, null);
            Native.AppendMenuW(menu, Native.MF_STRING, CmdExit, "Exit");

            // Required for tray menus: without foreground status the menu will not dismiss on an
            // outside click. The WM_NULL post afterwards is the classic KB135788 companion fix.
            Native.SetForegroundWindow(_window.Handle);
            Native.GetCursorPos(out var pt);
            var cmd = Native.TrackPopupMenuEx(
                menu,
                Native.TPM_RIGHTBUTTON | Native.TPM_RETURNCMD | Native.TPM_NONOTIFY,
                pt.X, pt.Y, _window.Handle, 0);
            Native.PostMessageW(_window.Handle, Native.WM_NULL, 0, 0);

            HandleCommand((uint)cmd);
        }
        finally
        {
            Native.DestroyMenu(menu); // destroys the interval submenu with it
        }
    }

    private void HandleCommand(uint cmd)
    {
        switch (cmd)
        {
            case CmdRefresh:
                _ = SafeRefreshAsync();
                break;

            case CmdAutostart:
                _autostart.SetEnabled(!_autostart.IsEnabled());
                break;

            case CmdExit:
                _monitor.Stop();
                Native.PostQuitMessage(0);
                break;

            case >= CmdIntervalBase when cmd - CmdIntervalBase is var minutes
                && IntervalChoices.Contains((int)minutes):
                _settings.PollIntervalMinutes = (int)minutes;
                _settings.TrySave();
                _monitor.SetInterval(_settings.PollInterval);
                break;
        }
    }

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

    private void NotifyLowBattery(IReadOnlyList<DeviceBattery> devices)
    {
        foreach (var d in devices)
        {
            if (d is { HasBattery: true, IsConnected: true } && d.BatteryPercent <= LowBatteryThreshold)
            {
                if (_lowNotified.Add(d.Name))
                    _tray.ShowWarningBalloon("Low battery", d.Display);
            }
            else
            {
                _lowNotified.Remove(d.Name); // re-arm once it recovers
            }
        }
    }

    private static string Truncate(string text) => text.Length <= 63 ? text : text[..63];

    public void Dispose()
    {
        _monitor.Stop();
        _tray.Dispose();
        _renderer.Dispose();
        _window.Dispose();
    }
}
