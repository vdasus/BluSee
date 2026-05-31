using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace BluSee.Battery.Hidpp;

/// <summary>
/// Minimal Win32 HID interop. We use CreateFile with shared access (FILE_SHARE_READ | WRITE) so the
/// HID++ collection can be opened even while Logitech software holds a handle — WinRT HidDevice opens
/// with narrow sharing and fails with UnauthorizedAccessException on these collections.
/// </summary>
internal static partial class NativeHid
{
    public const uint GenericRead = 0x80000000;
    public const uint GenericWrite = 0x40000000;
    public const uint FileShareRead = 0x00000001;
    public const uint FileShareWrite = 0x00000002;
    public const uint OpenExisting = 3;
    public const uint FileFlagOverlapped = 0x40000000;

    private const int HidpStatusSuccess = 0x00110000;

    [StructLayout(LayoutKind.Sequential)]
    public struct HiddAttributes
    {
        public int Size;
        public ushort VendorId;
        public ushort ProductId;
        public ushort VersionNumber;
    }

    // Blittable HIDP_CAPS; only the report-length fields are used.
    [StructLayout(LayoutKind.Sequential)]
    public unsafe struct HidpCaps
    {
        public ushort Usage;
        public ushort UsagePage;
        public ushort InputReportByteLength;
        public ushort OutputReportByteLength;
        public ushort FeatureReportByteLength;
        public fixed ushort Reserved[17];
        public ushort NumberLinkCollectionNodes;
        public ushort NumberInputButtonCaps;
        public ushort NumberInputValueCaps;
        public ushort NumberInputDataIndices;
        public ushort NumberOutputButtonCaps;
        public ushort NumberOutputValueCaps;
        public ushort NumberOutputDataIndices;
        public ushort NumberFeatureButtonCaps;
        public ushort NumberFeatureValueCaps;
        public ushort NumberFeatureDataIndices;
    }

    [LibraryImport("kernel32.dll", EntryPoint = "CreateFileW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    public static partial SafeFileHandle CreateFile(
        string fileName, uint access, uint share, IntPtr security, uint disposition, uint flags, IntPtr template);

    [LibraryImport("hid.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.U1)]
    public static partial bool HidD_GetAttributes(SafeFileHandle handle, ref HiddAttributes attributes);

    [LibraryImport("hid.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.U1)]
    public static partial bool HidD_GetPreparsedData(SafeFileHandle handle, out IntPtr preparsedData);

    [LibraryImport("hid.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.U1)]
    public static partial bool HidD_FreePreparsedData(IntPtr preparsedData);

    [LibraryImport("hid.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.U1)]
    public static partial bool HidD_SetNumInputBuffers(SafeFileHandle handle, uint numberBuffers);

    [LibraryImport("hid.dll")]
    public static partial int HidP_GetCaps(IntPtr preparsedData, out HidpCaps caps);

    public static bool TryGetCaps(SafeFileHandle handle, out HidpCaps caps)
    {
        caps = default;
        if (!HidD_GetPreparsedData(handle, out var preparsed))
            return false;
        try
        {
            return HidP_GetCaps(preparsed, out caps) == HidpStatusSuccess;
        }
        finally
        {
            HidD_FreePreparsedData(preparsed);
        }
    }
}
