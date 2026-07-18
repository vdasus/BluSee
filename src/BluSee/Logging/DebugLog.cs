using System.Text;

namespace BluSee.Logging;

/// <summary>
/// Opt-in trace log, switched on by <c>Debug=on</c> in blusee.ini. Appends timestamped lines to
/// blusee.log next to the exe so a user can see every poll: which providers ran, what was sent to
/// each device and what came back. Off by default and cheap when off (hot call sites additionally
/// check <see cref="Enabled"/> before formatting). All I/O failures are swallowed — tracing must
/// never break the app. Lives outside Diagnostics/ because that folder is excluded from Release
/// builds while this knob must work in the released portable exe.
/// </summary>
public static class DebugLog
{
    private const long MaxBytes = 5 * 1024 * 1024;

    // No BOM: the log must start with a plain timestamp, not EF BB BF (grep/parser friendliness).
    private static readonly UTF8Encoding Utf8 = new(encoderShouldEmitUTF8Identifier: false);
    private static readonly Lock Sync = new();

    // Approximate log size, maintained to rotate mid-session — a tray app runs for weeks, so
    // rotating only at startup would let the file grow unbounded. -1 = probe on first write.
    private static long _length = -1;

    private static string FilePath => Path.Combine(AppContext.BaseDirectory, "blusee.log");

    /// <summary>
    /// Set once at startup before the poll loop starts; that ordering is what makes the
    /// unsynchronized reads from background threads safe. A runtime toggle would need volatile.
    /// </summary>
    public static bool Enabled { get; private set; }

    public static void Enable() => Enabled = true;

    /// <summary>Append one line: <c>timestamp [category] message</c>. No-op unless enabled.</summary>
    public static void Write(string category, string message)
    {
        if (!Enabled)
            return;

        var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [{category}] {message}{Environment.NewLine}";
        try
        {
            lock (Sync)
            {
                if (_length < 0)
                    _length = File.Exists(FilePath) ? new FileInfo(FilePath).Length : 0;

                if (_length > MaxBytes)
                {
                    File.Move(FilePath, FilePath + ".old", overwrite: true);
                    _length = 0;
                }

                File.AppendAllText(FilePath, line, Utf8);
                _length += Utf8.GetByteCount(line);
            }
        }
        catch
        {
            // read-only location or file locked — drop the line
        }
    }

    /// <summary>Raw frame bytes as hex for request/response tracing.</summary>
    public static string Hex(ReadOnlySpan<byte> bytes) => Convert.ToHexString(bytes);
}
