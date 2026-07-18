using System.Runtime.InteropServices;

namespace BluSee.Tray.Win32;

/// <summary>
/// Hidden top-level window that receives tray callbacks and system broadcasts. A message-only
/// (HWND_MESSAGE) window would not get WM_SETTINGCHANGE or TaskbarCreated broadcasts, so this is an
/// invisible WS_POPUP window instead. Single instance per process; the WndProc is a static
/// UnmanagedCallersOnly method (AOT-friendly, no delegate marshalling).
/// </summary>
internal sealed unsafe partial class MessageWindow : IDisposable
{
    public const uint TrayCallbackMessage = Native.WM_APP + 1;
    public const uint DevicesUpdatedMessage = Native.WM_APP + 2;

    private const string ClassName = "BluSeeTrayWnd";

    private static MessageWindow? _instance;
    private static uint _taskbarCreatedMessage;

    public nint Handle { get; }

    /// <summary>Left or right click on the tray icon — show the menu.</summary>
    public event Action? TrayActivated;

    /// <summary>Posted from the monitor thread after a battery refresh.</summary>
    public event Action? DevicesUpdated;

    /// <summary>WM_SETTINGCHANGE — system theme may have flipped.</summary>
    public event Action? SettingChanged;

    /// <summary>Explorer restarted; the tray icon must be re-added.</summary>
    public event Action? TaskbarCreated;

    public MessageWindow()
    {
        if (_instance is not null)
            throw new InvalidOperationException("Only one MessageWindow per process.");
        _instance = this;

        _taskbarCreatedMessage = Native.RegisterWindowMessageW("TaskbarCreated");

        fixed (char* className = ClassName)
        {
            var wc = new Native.WNDCLASSW
            {
                lpfnWndProc = &WndProc,
                hInstance = Native.GetModuleHandleW(null),
                lpszClassName = className,
            };
            if (Native.RegisterClassW(&wc) == 0)
                throw new InvalidOperationException($"RegisterClassW failed ({Marshal.GetLastPInvokeError()}).");
        }

        Handle = Native.CreateWindowExW(
            0, ClassName, "BluSee", Native.WS_POPUP,
            0, 0, 0, 0, 0, 0, Native.GetModuleHandleW(null), 0);
        if (Handle == 0)
            throw new InvalidOperationException($"CreateWindowExW failed ({Marshal.GetLastPInvokeError()}).");
    }

    /// <summary>Thread-safe: post a message to the UI thread.</summary>
    public void Post(uint message) => Native.PostMessageW(Handle, message, 0, 0);

    [UnmanagedCallersOnly]
    private static nint WndProc(nint hwnd, uint msg, nint wParam, nint lParam)
    {
        var self = _instance;
        if (self is null)
            return Native.DefWindowProcW(hwnd, msg, wParam, lParam);

        // Exceptions must never cross the native frame boundary.
        try
        {
            switch (msg)
            {
                case TrayCallbackMessage:
                    var mouse = (uint)(lParam & 0xFFFF);
                    if (mouse is Native.WM_RBUTTONUP or Native.WM_LBUTTONUP or Native.WM_CONTEXTMENU)
                        self.TrayActivated?.Invoke();
                    return 0;

                case DevicesUpdatedMessage:
                    self.DevicesUpdated?.Invoke();
                    return 0;

                case Native.WM_SETTINGCHANGE:
                    self.SettingChanged?.Invoke();
                    break;

                default:
                    if (_taskbarCreatedMessage != 0 && msg == _taskbarCreatedMessage)
                        self.TaskbarCreated?.Invoke();
                    break;
            }
        }
        catch
        {
            // swallow — a failing handler must not crash the message loop
        }

        return Native.DefWindowProcW(hwnd, msg, wParam, lParam);
    }

    public void Dispose()
    {
        if (Handle != 0)
            Native.DestroyWindow(Handle);
        _instance = null;
    }
}
