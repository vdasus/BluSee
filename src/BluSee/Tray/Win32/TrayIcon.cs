namespace BluSee.Tray.Win32;

/// <summary>
/// Shell_NotifyIcon wrapper. The shell copies the HICON on add/modify, so callers keep ownership of
/// the handle they pass in. Survives explorer restarts via <see cref="Readd"/>.
/// </summary>
internal sealed unsafe partial class TrayIcon(nint ownerHwnd) : IDisposable
{
    private const uint IconId = 1;

    private bool _added;
    private nint _lastIcon;
    private string _lastTip = "BluSee";

    public void Update(nint hicon, string tip)
    {
        _lastIcon = hicon;
        _lastTip = tip;

        var data = CreateData();
        data.uFlags = Native.NIF_MESSAGE | Native.NIF_ICON | Native.NIF_TIP;
        data.uCallbackMessage = MessageWindow.TrayCallbackMessage;
        data.hIcon = hicon;
        Native.CopyTo(tip, data.szTip, 128);

        if (_added)
        {
            // Modify fails if explorer was restarted behind our back — fall through to re-add.
            if (Native.Shell_NotifyIconW(Native.NIM_MODIFY, &data))
                return;
            _added = false;
        }

        _added = Native.Shell_NotifyIconW(Native.NIM_ADD, &data);
    }

    /// <summary>Re-add the icon after a TaskbarCreated broadcast (explorer restart).</summary>
    public void Readd()
    {
        _added = false;
        if (_lastIcon != 0)
            Update(_lastIcon, _lastTip);
    }

    public void ShowWarningBalloon(string title, string text)
    {
        var data = CreateData();
        data.uFlags = Native.NIF_INFO;
        data.dwInfoFlags = Native.NIIF_WARNING;
        Native.CopyTo(title, data.szInfoTitle, 64);
        Native.CopyTo(text, data.szInfo, 256);
        Native.Shell_NotifyIconW(Native.NIM_MODIFY, &data);
    }

    private Native.NOTIFYICONDATAW CreateData() => new()
    {
        cbSize = (uint)sizeof(Native.NOTIFYICONDATAW),
        hWnd = ownerHwnd,
        uID = IconId,
    };

    public void Dispose()
    {
        if (!_added)
            return;
        var data = CreateData();
        Native.Shell_NotifyIconW(Native.NIM_DELETE, &data);
        _added = false;
    }
}
