using CodexKeyboard.Protocol;
using Microsoft.Win32.SafeHandles;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;

namespace CodexKeyboard.Device;

public static class CodexKeyboardIdentity
{
    public const ushort VendorId = 0x1209;
    public const ushort ProductId = 0xC55D;
    public const ushort DeviceRelease = 0x0110;
    public const ushort UsagePage = 0xFF00;
    public const ushort Usage = 0x0001;
    public const string Manufacturer = "CodexKeyboard";
    public const string Product = "CodexKeyboard";
    public const string Serial = "CK498AED4EBD";
    public const string UsbInstanceId = "USB\\VID_1209&PID_C55D\\CK498AED4EBD";
    public const string OwnershipName = @"Local\CodexKeyboard.Device.1209-C55D.CK498AED4EBD";
    public const string TransitionVerificationName =
        @"Local\CodexKeyboard.Device.BootloaderVerification.1209-C55D.CK498AED4EBD";
    public static readonly string TransitionMarkerPath = Path.Combine(
        Path.GetTempPath(), $"CodexKeyboard-{Serial}.bootloader-transition");
    public static readonly TimeSpan TransitionAuthorizationLifetime = TimeSpan.FromSeconds(10);

    public static void Validate(DeviceInfo info)
    {
        if (info.FirmwareMajor != 1 || info.FirmwareMinor != 1 || info.FirmwarePatch != 0 ||
            info.Capabilities != Capabilities.RequiredV1 || info.ButtonCount != 4 ||
            info.EncoderCount != 1 || info.LedCount != 3 || info.MaximumBrightness != byte.MaxValue)
        {
            throw new InvalidDataException("The CodexKeyboard DEVICE_INFO response does not match firmware 1.1.0.");
        }
    }
}

internal static class WindowsHid
{
    private const uint GenericRead = 0x80000000;
    private const uint GenericWrite = 0x40000000;
    private const uint FileShareRead = 0x00000001;
    private const uint FileShareWrite = 0x00000002;
    private const uint OpenExisting = 3;
    private const uint FileFlagOverlapped = 0x40000000;
    private const uint DigcfPresent = 0x00000002;
    private const uint DigcfDeviceInterface = 0x00000010;
    private const int ErrorNoMoreItems = 259;
    private const int ErrorInsufficientBuffer = 122;
    private const int HidpStatusSuccess = 0x00110000;
    private const uint CrSuccess = 0;

    internal static RuntimeHidHandles? OpenExactRuntime()
    {
        string? matchPath = null;
        foreach (var hidInterface in EnumerateHidInterfaces())
        {
            if (!string.Equals(
                    hidInterface.ParentInstanceId,
                    CodexKeyboardIdentity.UsbInstanceId,
                    StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (matchPath is not null)
            {
                throw new InvalidOperationException(
                    "Multiple HID collections exist under the exact CodexKeyboard USB parent.");
            }

            using var queryHandle = CreateFile(
                hidInterface.Path,
                0,
                FileShareRead | FileShareWrite,
                IntPtr.Zero,
                OpenExisting,
                0,
                IntPtr.Zero);
            if (queryHandle.IsInvalid)
            {
                throw new Win32Exception(
                    Marshal.GetLastWin32Error(),
                    "The exact CodexKeyboard HID collection could not be opened for identity verification.");
            }

            if (!MatchesRuntime(queryHandle))
            {
                throw new InvalidOperationException(
                    "The HID collection under the exact CodexKeyboard USB parent does not match the frozen identity.");
            }
            matchPath = hidInterface.Path;
        }

        if (matchPath is null)
        {
            return null;
        }

        var readHandle = CreateFile(
            matchPath,
            GenericRead,
            FileShareRead | FileShareWrite,
            IntPtr.Zero,
            OpenExisting,
            FileFlagOverlapped,
            IntPtr.Zero);
        if (readHandle.IsInvalid)
        {
            var error = Marshal.GetLastWin32Error();
            readHandle.Dispose();
            throw new Win32Exception(error, "The verified CodexKeyboard HID interface could not be opened for input.");
        }

        var writeHandle = CreateFile(
            matchPath,
            GenericWrite,
            FileShareRead | FileShareWrite,
            IntPtr.Zero,
            OpenExisting,
            FileFlagOverlapped,
            IntPtr.Zero);
        if (writeHandle.IsInvalid)
        {
            var error = Marshal.GetLastWin32Error();
            writeHandle.Dispose();
            readHandle.Dispose();
            throw new Win32Exception(error, "The verified CodexKeyboard HID interface could not be opened for output.");
        }
        return new RuntimeHidHandles(readHandle, writeHandle);
    }

    private static bool MatchesRuntime(SafeFileHandle handle)
    {
        var attributes = new HiddAttributes { Size = Marshal.SizeOf<HiddAttributes>() };
        if (!HidD_GetAttributes(handle, ref attributes))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "HidD_GetAttributes failed.");
        }
        if (attributes.VendorId != CodexKeyboardIdentity.VendorId ||
            attributes.ProductId != CodexKeyboardIdentity.ProductId ||
            attributes.VersionNumber != CodexKeyboardIdentity.DeviceRelease)
        {
            return false;
        }

        if (!HidD_GetPreparsedData(handle, out var preparsedData))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "HidD_GetPreparsedData failed.");
        }
        try
        {
            if (HidP_GetCaps(preparsedData, out var caps) != HidpStatusSuccess)
            {
                throw new InvalidOperationException("HidP_GetCaps failed.");
            }
            return caps.UsagePage == CodexKeyboardIdentity.UsagePage &&
                   caps.Usage == CodexKeyboardIdentity.Usage &&
                   caps.InputReportByteLength == HidProtocol.ReportLength &&
                   caps.OutputReportByteLength == HidProtocol.ReportLength;
        }
        finally
        {
            HidD_FreePreparsedData(preparsedData);
        }
    }

    private static IEnumerable<(string Path, string ParentInstanceId)> EnumerateHidInterfaces()
    {
        HidD_GetHidGuid(out var hidGuid);
        var deviceInfoSet = SetupDiGetClassDevs(
            ref hidGuid, null, IntPtr.Zero, DigcfPresent | DigcfDeviceInterface);
        if (deviceInfoSet == new IntPtr(-1))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "SetupDiGetClassDevs failed.");
        }

        try
        {
            for (uint index = 0; ; index++)
            {
                var interfaceData = new SpDeviceInterfaceData
                {
                    Size = Marshal.SizeOf<SpDeviceInterfaceData>()
                };
                if (!SetupDiEnumDeviceInterfaces(
                        deviceInfoSet, IntPtr.Zero, ref hidGuid, index, ref interfaceData))
                {
                    var error = Marshal.GetLastWin32Error();
                    if (error == ErrorNoMoreItems)
                    {
                        yield break;
                    }
                    throw new Win32Exception(error, "SetupDiEnumDeviceInterfaces failed.");
                }

                var sizeProbeSucceeded = SetupDiGetDeviceInterfaceDetail(
                    deviceInfoSet, ref interfaceData, IntPtr.Zero, 0, out var requiredSize, IntPtr.Zero);
                var sizeProbeError = Marshal.GetLastWin32Error();
                var minimumSize = (uint)(IntPtr.Size == 8 ? 8 : 6);
                if (sizeProbeSucceeded || sizeProbeError != ErrorInsufficientBuffer || requiredSize < minimumSize)
                {
                    throw new Win32Exception(sizeProbeError, "The HID detail-size probe failed.");
                }

                var detail = Marshal.AllocHGlobal(checked((int)requiredSize));
                try
                {
                    Marshal.WriteInt32(detail, IntPtr.Size == 8 ? 8 : 6);
                    var deviceInfo = new SpDeviceInfoData
                    {
                        Size = Marshal.SizeOf<SpDeviceInfoData>()
                    };
                    if (!SetupDiGetDeviceInterfaceDetailWithDeviceInfo(
                            deviceInfoSet,
                            ref interfaceData,
                            detail,
                            requiredSize,
                            out _,
                            ref deviceInfo))
                    {
                        throw new Win32Exception(
                            Marshal.GetLastWin32Error(), "SetupDiGetDeviceInterfaceDetail failed.");
                    }
                    var path = Marshal.PtrToStringUni(IntPtr.Add(detail, sizeof(uint))) ??
                        throw new InvalidOperationException("A HID interface path was null.");
                    yield return (path, GetParentInstanceId(deviceInfo.DeviceInstance));
                }
                finally
                {
                    Marshal.FreeHGlobal(detail);
                }
            }
        }
        finally
        {
            SetupDiDestroyDeviceInfoList(deviceInfoSet);
        }
    }

    private static string GetParentInstanceId(uint deviceInstance)
    {
        var result = CM_Get_Parent(out var parent, deviceInstance, 0);
        if (result != CrSuccess)
        {
            throw new InvalidOperationException($"CM_Get_Parent failed with CONFIGRET 0x{result:X8}.");
        }

        result = CM_Get_Device_ID_Size(out var characterCount, parent, 0);
        if (result != CrSuccess)
        {
            throw new InvalidOperationException($"CM_Get_Device_ID_Size failed with CONFIGRET 0x{result:X8}.");
        }

        var buffer = new StringBuilder(checked((int)characterCount + 1));
        result = CM_Get_Device_ID(parent, buffer, characterCount + 1, 0);
        if (result != CrSuccess)
        {
            throw new InvalidOperationException($"CM_Get_Device_ID failed with CONFIGRET 0x{result:X8}.");
        }
        return buffer.ToString();
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct HiddAttributes
    {
        public int Size;
        public ushort VendorId;
        public ushort ProductId;
        public ushort VersionNumber;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SpDeviceInterfaceData
    {
        public int Size;
        public Guid InterfaceClassGuid;
        public int Flags;
        public IntPtr Reserved;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SpDeviceInfoData
    {
        public int Size;
        public Guid ClassGuid;
        public uint DeviceInstance;
        public IntPtr Reserved;
    }

    [StructLayout(LayoutKind.Sequential)]
    private unsafe struct HidpCaps
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

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFile(
        string fileName,
        uint desiredAccess,
        uint shareMode,
        IntPtr securityAttributes,
        uint creationDisposition,
        uint flagsAndAttributes,
        IntPtr templateFile);

    [DllImport("hid.dll")]
    private static extern void HidD_GetHidGuid(out Guid hidGuid);

    [DllImport("hid.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.U1)]
    private static extern bool HidD_GetAttributes(SafeFileHandle handle, ref HiddAttributes attributes);

    [DllImport("hid.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.U1)]
    private static extern bool HidD_GetPreparsedData(SafeFileHandle handle, out IntPtr preparsedData);

    [DllImport("hid.dll")]
    [return: MarshalAs(UnmanagedType.U1)]
    private static extern bool HidD_FreePreparsedData(IntPtr preparsedData);

    [DllImport("hid.dll")]
    private static extern int HidP_GetCaps(IntPtr preparsedData, out HidpCaps caps);

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr SetupDiGetClassDevs(
        ref Guid classGuid, string? enumerator, IntPtr parent, uint flags);

    [DllImport("setupapi.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetupDiEnumDeviceInterfaces(
        IntPtr deviceInfoSet,
        IntPtr deviceInfoData,
        ref Guid interfaceClassGuid,
        uint memberIndex,
        ref SpDeviceInterfaceData deviceInterfaceData);

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetupDiGetDeviceInterfaceDetail(
        IntPtr deviceInfoSet,
        ref SpDeviceInterfaceData deviceInterfaceData,
        IntPtr deviceInterfaceDetailData,
        uint deviceInterfaceDetailDataSize,
        out uint requiredSize,
        IntPtr deviceInfoData);

    [DllImport(
        "setupapi.dll",
        EntryPoint = "SetupDiGetDeviceInterfaceDetailW",
        CharSet = CharSet.Unicode,
        SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetupDiGetDeviceInterfaceDetailWithDeviceInfo(
        IntPtr deviceInfoSet,
        ref SpDeviceInterfaceData deviceInterfaceData,
        IntPtr deviceInterfaceDetailData,
        uint deviceInterfaceDetailDataSize,
        out uint requiredSize,
        ref SpDeviceInfoData deviceInfoData);

    [DllImport("setupapi.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetupDiDestroyDeviceInfoList(IntPtr deviceInfoSet);

    [DllImport("cfgmgr32.dll")]
    private static extern uint CM_Get_Parent(out uint parent, uint deviceInstance, uint flags);

    [DllImport("cfgmgr32.dll")]
    private static extern uint CM_Get_Device_ID_Size(
        out uint characterCount, uint deviceInstance, uint flags);

    [DllImport("cfgmgr32.dll", EntryPoint = "CM_Get_Device_IDW", CharSet = CharSet.Unicode)]
    private static extern uint CM_Get_Device_ID(
        uint deviceInstance, StringBuilder buffer, uint bufferLength, uint flags);
}

internal sealed record RuntimeHidHandles(SafeFileHandle Read, SafeFileHandle Write);
