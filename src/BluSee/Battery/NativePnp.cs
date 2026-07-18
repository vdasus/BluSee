using System.Runtime.InteropServices;
using System.Text;

namespace BluSee.Battery;

/// <summary>
/// Minimal CfgMgr32 interop: enumerate present devnodes of a setup class and read DEVPKEY
/// properties straight from the PnP tree. Exists because the WinRT DeviceInformation path is
/// unusable under NativeAOT here — passing the requested-property list needs a CCW for
/// IIterable&lt;String&gt;, and CsWinRT's generator emits no marshalling vtables in this project
/// (verified at runtime: string[], List&lt;string&gt; and an assembly-local partial list type all fail
/// with InvalidCastException). CfgMgr32 has no such dependency and is also faster.
/// </summary>
internal static partial class NativePnp
{
    public const uint CrSuccess = 0;
    private const uint CrBufferSmall = 26;

    private const uint GetIdListFilterPresent = 0x00000100;
    private const uint GetIdListFilterClass = 0x00000200;

    // DEVPROP_TYPE_* values we understand.
    private const uint TypeSByte = 0x00000002;
    private const uint TypeByte = 0x00000003;
    private const uint TypeInt16 = 0x00000004;
    private const uint TypeUInt16 = 0x00000005;
    private const uint TypeInt32 = 0x00000006;
    private const uint TypeUInt32 = 0x00000007;
    private const uint TypeGuid = 0x0000000D;
    private const uint TypeString = 0x00000012;

    /// <summary>DN_STARTED bit of DEVPKEY_Device_DevNodeStatus.</summary>
    public const uint DnStarted = 0x00000008;

    [StructLayout(LayoutKind.Sequential)]
    public readonly struct DevPropKey(Guid fmtid, uint pid)
    {
        public readonly Guid Fmtid = fmtid;
        public readonly uint Pid = pid;
    }

    // Battery-level DEVPKEY candidates (same key as the WinRT "{104EA319-...} 2|9" strings).
    public static readonly DevPropKey BatteryLevel2 = new(new Guid("104EA319-6EE2-4701-BD47-8D0F1493C853"), 2);
    public static readonly DevPropKey BatteryLevel9 = new(new Guid("104EA319-6EE2-4701-BD47-8D0F1493C853"), 9);

    /// <summary>DEVPKEY_NAME — same underlying key as System.ItemNameDisplay.</summary>
    public static readonly DevPropKey NameKey = new(new Guid("B725F130-47EF-101A-A5F1-02608C9EEBAC"), 10);

    public static readonly DevPropKey ContainerId = new(new Guid("8C7ED206-3F8A-4827-B3AB-AE9E1FAEFC6C"), 2);
    public static readonly DevPropKey DevNodeStatus = new(new Guid("4340A6C5-93FA-4706-972C-7B648008A5A7"), 2);

    [LibraryImport("cfgmgr32.dll", EntryPoint = "CM_Get_Device_ID_List_SizeW", StringMarshalling = StringMarshalling.Utf16)]
    private static partial uint CM_Get_Device_ID_List_Size(out uint length, string filter, uint flags);

    [LibraryImport("cfgmgr32.dll", EntryPoint = "CM_Get_Device_ID_ListW", StringMarshalling = StringMarshalling.Utf16)]
    private static unsafe partial uint CM_Get_Device_ID_List(string filter, char* buffer, uint bufferLength, uint flags);

    [LibraryImport("cfgmgr32.dll", EntryPoint = "CM_Locate_DevNodeW", StringMarshalling = StringMarshalling.Utf16)]
    public static partial uint CM_Locate_DevNode(out uint devInst, string deviceId, uint flags);

    [LibraryImport("cfgmgr32.dll", EntryPoint = "CM_Get_DevNode_PropertyW")]
    private static unsafe partial uint CM_Get_DevNode_Property(
        uint devInst, in DevPropKey key, out uint propertyType, byte* buffer, ref uint bufferSize, uint flags);

    /// <summary>Instance ids of all present devnodes of a setup class (braced GUID string).</summary>
    public static unsafe IReadOnlyList<string> GetPresentDeviceIds(string classGuid)
    {
        const uint flags = GetIdListFilterClass | GetIdListFilterPresent;
        if (CM_Get_Device_ID_List_Size(out var length, classGuid, flags) != CrSuccess || length == 0)
            return [];

        var chars = new char[length];
        fixed (char* p = chars)
        {
            if (CM_Get_Device_ID_List(classGuid, p, length, flags) != CrSuccess)
                return [];
        }

        // Buffer holds NUL-separated ids ending with a double NUL.
        var result = new List<string>();
        var start = 0;
        for (var i = 0; i < chars.Length; i++)
        {
            if (chars[i] != '\0')
                continue;
            if (i == start)
                break;
            result.Add(new string(chars, start, i - start));
            start = i + 1;
        }

        return result;
    }

    public static unsafe int? GetIntProperty(uint devInst, in DevPropKey key)
    {
        var buffer = stackalloc byte[8];
        uint size = 8;
        if (CM_Get_DevNode_Property(devInst, in key, out var type, buffer, ref size, 0) != CrSuccess)
            return null;

        return type switch
        {
            TypeSByte => (sbyte)buffer[0],
            TypeByte => buffer[0],
            TypeInt16 => *(short*)buffer,
            TypeUInt16 => *(ushort*)buffer,
            TypeInt32 => *(int*)buffer,
            TypeUInt32 => (int)Math.Min(*(uint*)buffer, int.MaxValue),
            _ => null,
        };
    }

    public static unsafe uint? GetUIntProperty(uint devInst, in DevPropKey key)
    {
        var buffer = stackalloc byte[8];
        uint size = 8;
        if (CM_Get_DevNode_Property(devInst, in key, out var type, buffer, ref size, 0) != CrSuccess)
            return null;

        return type is TypeUInt32 or TypeInt32 ? *(uint*)buffer : null;
    }

    public static unsafe Guid? GetGuidProperty(uint devInst, in DevPropKey key)
    {
        var buffer = stackalloc byte[16];
        uint size = 16;
        if (CM_Get_DevNode_Property(devInst, in key, out var type, buffer, ref size, 0) != CrSuccess
            || type != TypeGuid || size != 16)
            return null;

        return new Guid(new ReadOnlySpan<byte>(buffer, 16));
    }

    public static unsafe string? GetStringProperty(uint devInst, in DevPropKey key)
    {
        uint size = 0;
        var cr = CM_Get_DevNode_Property(devInst, in key, out var type, null, ref size, 0);
        if (cr is not (CrSuccess or CrBufferSmall) || type != TypeString || size == 0)
            return null;

        var buffer = new byte[size];
        fixed (byte* p = buffer)
        {
            if (CM_Get_DevNode_Property(devInst, in key, out type, p, ref size, 0) != CrSuccess)
                return null;
        }

        var text = Encoding.Unicode.GetString(buffer).TrimEnd('\0').Trim();
        return text.Length == 0 ? null : text;
    }
}
