using CodexKeyboard.Protocol;
using Microsoft.Win32.SafeHandles;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;

namespace CodexKeyboard.BootloaderTool;

internal static class Program
{
    private const ushort RuntimeVendorId = 0x1209;
    private const ushort RuntimeProductId = 0xC55D;
    private const ushort RuntimeRelease = 0x0110;
    private const string RuntimeSerial = "CK498AED4EBD";
    private const string RuntimeUsbInstanceId = "USB\\VID_1209&PID_C55D\\CK498AED4EBD";
    private const string BootloaderUsbInstancePrefix = "USB\\VID_4348&PID_55E0\\";
    private const ushort RuntimeUsagePage = 0xFF00;
    private const ushort RuntimeUsage = 0x0001;

    private const ushort BootloaderVendorId = 0x4348;
    private const ushort BootloaderProductId = 0x55E0;
    private const ushort BootloaderRelease = 0x0250;
    private const byte BootloaderClass = 0xFF;
    private const byte BootloaderSubclass = 0x80;
    private const byte BootloaderProtocol = 0x55;

    private const uint GenericRead = 0x80000000;
    private const uint GenericWrite = 0x40000000;
    private const uint FileShareRead = 0x00000001;
    private const uint FileShareWrite = 0x00000002;
    private const uint OpenExisting = 3;
    private const uint FileFlagOverlapped = 0x40000000;
    private const uint DigcfPresent = 0x00000002;
    private const uint DigcfAllClasses = 0x00000004;
    private const uint DigcfDeviceInterface = 0x00000010;
    private const int ErrorNoMoreItems = 259;
    private const int ErrorInsufficientBuffer = 122;
    private const int HidpStatusSuccess = 0x00110000;
    private const uint CrSuccess = 0;
    private const int WchDeviceCapacity = 16;

    private static readonly byte[] IspEndRequest = [0xA2, 0x01, 0x00, 0x01];
    private static readonly byte[] IspEndResponse = [0xA2, 0x00, 0x02, 0x00, 0x00, 0x00];
    private static readonly string TransitionMarkerPath = Path.Combine(
        Path.GetTempPath(), $"CodexKeyboard-{RuntimeSerial}.bootloader-transition");

    private static async Task<int> Main(string[] args)
    {
        if (args.Length != 1)
        {
            PrintUsage();
            return 2;
        }

        try
        {
            switch (args[0].ToLowerInvariant())
            {
                case "enter":
                    await EnterBootloaderAsync();
                    break;
                case "exit":
                    await ExitBootloaderAsync();
                    break;
                case "status":
                    await ShowRuntimeStatusAsync();
                    break;
                case "self-test":
                    RunSelfTest();
                    break;
                default:
                    PrintUsage();
                    return 2;
            }

            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"ERROR: {exception.Message}");
            return 1;
        }
    }

    private static async Task EnterBootloaderAsync()
    {
        DeleteStaleTransitionMarker();
        var existingBootloader = OpenBootloader();
        if (existingBootloader is not null)
        {
            CH375CloseDevice(existingBootloader.Value);
            throw new InvalidOperationException("A CH552 ROM bootloader already exists; transition identity is ambiguous.");
        }

        using var handle = OpenRuntime() ??
            throw new InvalidOperationException("The exact CodexKeyboard runtime device was not found.");
        await using var stream = new FileStream(handle, FileAccess.ReadWrite, HidProtocol.ReportLength, true);

        var infoReport = await ExchangeAsync(stream, HidProtocol.CreateGetInfo(0), MessageType.DeviceInfo, 0);
        var info = HidProtocol.DecodeDeviceInfo(infoReport);
        if (info.FirmwareMajor != 1 || info.FirmwareMinor != 1 || info.FirmwarePatch != 0 ||
            !info.Capabilities.HasFlag(Capabilities.RemoteBootloader))
        {
            throw new InvalidOperationException("The connected firmware does not support guarded remote bootloader entry.");
        }

        var ackReport = await ExchangeAsync(
            stream, HidProtocol.CreateEnterBootloader(1), MessageType.Ack, 1);
        var ack = HidProtocol.DecodeAck(ackReport);
        if (ack.Command != MessageType.EnterBootloader)
        {
            throw new InvalidOperationException("The device ACK did not identify ENTER_BOOTLOADER.");
        }

        await stream.DisposeAsync();
        await WaitForBootloaderAsync(TimeSpan.FromSeconds(3));
        using var unexpectedRuntime = OpenRuntime();
        if (unexpectedRuntime is not null)
        {
            throw new InvalidOperationException("The runtime remained present after the bootloader transition.");
        }
        File.WriteAllText(TransitionMarkerPath, DateTimeOffset.UtcNow.ToString("O"));
        Console.WriteLine("BOOTLOADER_READY: 4348:55E0, CH552 ROM 2.50");
    }

    private static async Task ShowRuntimeStatusAsync()
    {
        using var handle = OpenRuntime() ??
            throw new InvalidOperationException("The exact CodexKeyboard runtime device was not found.");
        await using var stream = new FileStream(handle, FileAccess.ReadWrite, HidProtocol.ReportLength, true);
        var report = await ExchangeAsync(stream, HidProtocol.CreateGetInfo(0), MessageType.DeviceInfo, 0);
        var info = HidProtocol.DecodeDeviceInfo(report);
        Console.WriteLine(
            $"RUNTIME_READY: firmware {info.FirmwareMajor}.{info.FirmwareMinor}.{info.FirmwarePatch}, " +
            $"capabilities 0x{(ushort)info.Capabilities:X4}, serial {RuntimeSerial}");
    }

    private static async Task ExitBootloaderAsync()
    {
        var transitionCreated = ValidateRecentTransitionMarker();
        uint? openIndex;
        try
        {
            openIndex = OpenBootloader();
        }
        catch (Exception exception) when (exception is InvalidOperationException or Win32Exception)
        {
            if (await CompleteIfRuntimeReturnedAsync(
                    transitionCreated,
                    "RUNTIME_READY: bootloader returned before ISP_END; CodexKeyboard handshake verified"))
            {
                return;
            }
            throw;
        }

        if (openIndex is null)
        {
            if (await CompleteIfRuntimeReturnedAsync(
                    transitionCreated,
                    "RUNTIME_READY: bootloader returned before ISP_END; CodexKeyboard handshake verified"))
            {
                return;
            }
            throw new InvalidOperationException("Exactly one CH552 ROM bootloader was not found.");
        }

        var index = openIndex.Value;
        var replyReceived = false;
        var deviceOpen = true;
        try
        {
            if (!CH375SetExclusive(index, 1))
            {
                CH375CloseDevice(index);
                deviceOpen = false;
                if (await CompleteIfRuntimeReturnedAsync(
                        transitionCreated,
                        "RUNTIME_READY: bootloader returned before exclusive access; CodexKeyboard handshake verified"))
                {
                    return;
                }
                throw new InvalidOperationException("Exclusive access to the WCH bootloader was denied.");
            }
            if (!CH375SetTimeout(index, 1000, 1000))
            {
                CH375CloseDevice(index);
                deviceOpen = false;
                if (await CompleteIfRuntimeReturnedAsync(
                        transitionCreated,
                        "RUNTIME_READY: bootloader returned before ISP_END; CodexKeyboard handshake verified"))
                {
                    return;
                }
                throw new Win32Exception("CH375SetTimeout failed.");
            }

            ConsumeRecentTransitionMarker(transitionCreated);
            var requestLength = (uint)IspEndRequest.Length;
            if (!CH375WriteData(index, IspEndRequest, ref requestLength) ||
                requestLength != IspEndRequest.Length)
            {
                CH375CloseDevice(index);
                deviceOpen = false;
                if (await CompleteIfRuntimeReturnedAsync(
                        null,
                        "RUNTIME_READY: bootloader disappeared during ISP_END; CodexKeyboard handshake verified"))
                {
                    return;
                }
                throw new IOException("The WCH ISP-end request was not written completely.");
            }

            var response = new byte[64];
            var responseLength = (uint)response.Length;
            if (CH375ReadData(index, response, ref responseLength))
            {
                ValidateIspEndResponse(response.AsSpan(0, checked((int)responseLength)));
                replyReceived = true;
            }
        }
        finally
        {
            if (deviceOpen)
            {
                CH375CloseDevice(index);
            }
        }

        await WaitForRuntimeAsync(TimeSpan.FromSeconds(4));
        Console.WriteLine(replyReceived
            ? "RUNTIME_READY: ISP_END reply and CodexKeyboard handshake verified"
            : "RUNTIME_READY: bootloader disappeared before the reply; CodexKeyboard handshake verified");
    }

    private static async Task<bool> CompleteIfRuntimeReturnedAsync(
        DateTimeOffset? transitionCreated,
        string successMessage)
    {
        try
        {
            await WaitForRuntimeAsync(TimeSpan.FromSeconds(4));
        }
        catch (TimeoutException)
        {
            return false;
        }

        if (CountPresentBootloaderDevnodes() != 0)
        {
            return false;
        }
        if (transitionCreated is not null)
        {
            ConsumeRecentTransitionMarker(transitionCreated.Value);
        }
        Console.WriteLine(successMessage);
        return true;
    }

    private static async Task<byte[]> ExchangeAsync(
        FileStream stream, byte[] command, MessageType expectedType, byte expectedSequence)
    {
        using var timeout = new CancellationTokenSource(HidProtocol.CommandTimeoutMs);
        try
        {
            await stream.WriteAsync(command, timeout.Token);
            await stream.FlushAsync(timeout.Token);

            while (true)
            {
                var report = await ReadReportAsync(stream, timeout.Token);
                var decoded = HidProtocol.Decode(report, ReportDirection.DeviceToHost);
                if (decoded.Type == MessageType.InputEvent)
                {
                    continue;
                }

                if (decoded.Sequence != expectedSequence)
                {
                    throw new InvalidOperationException("A terminal response used an unexpected sequence number.");
                }
                if (decoded.Type == MessageType.Error)
                {
                    var error = HidProtocol.DecodeError(report);
                    throw new InvalidOperationException(
                        $"Device error {error.Error} for message 0x{error.FailedMessageType:X2}, detail {error.Detail}.");
                }
                if (decoded.Type != expectedType)
                {
                    throw new InvalidOperationException(
                        $"Expected {expectedType}, received {decoded.Type}.");
                }

                return report;
            }
        }
        catch (OperationCanceledException exception)
        {
            throw new TimeoutException("The HID command did not complete within 250 ms.", exception);
        }
    }

    private static async Task<byte[]> ReadReportAsync(FileStream stream, CancellationToken cancellationToken)
    {
        var report = new byte[HidProtocol.ReportLength];
        var offset = 0;
        while (offset < report.Length)
        {
            var read = await stream.ReadAsync(report.AsMemory(offset), cancellationToken);
            if (read == 0)
            {
                throw new EndOfStreamException("The HID device closed before returning a complete report.");
            }
            offset += read;
        }
        return report;
    }

    private static async Task WaitForBootloaderAsync(TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            var index = OpenBootloader(allowTransientUnavailable: true);
            if (index is not null)
            {
                CH375CloseDevice(index.Value);
                return;
            }
            await Task.Delay(50);
        }
        throw new TimeoutException("The verified CH552 ROM bootloader did not appear.");
    }

    private static async Task WaitForRuntimeAsync(TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            try
            {
                using var handle = OpenRuntime();
                if (handle is not null)
                {
                    await using var stream = new FileStream(
                        handle, FileAccess.ReadWrite, HidProtocol.ReportLength, true);
                    var report = await ExchangeAsync(
                        stream, HidProtocol.CreateGetInfo(0), MessageType.DeviceInfo, 0);
                    var info = HidProtocol.DecodeDeviceInfo(report);
                    if (info.FirmwareMajor == 1 && info.FirmwareMinor == 1 && info.FirmwarePatch == 0)
                    {
                        return;
                    }
                }
            }
            catch (Exception exception) when (
                exception is IOException or TimeoutException or Win32Exception)
            {
                // The runtime can enumerate before its HID endpoints are ready; retry within the deadline.
            }
            await Task.Delay(50);
        }
        throw new TimeoutException("The exact CodexKeyboard runtime did not return after ISP_END.");
    }

    private static SafeFileHandle? OpenRuntime()
    {
        string? matchPath = null;
        foreach (var hidInterface in EnumerateHidInterfaces())
        {
            if (!string.Equals(
                    hidInterface.ParentInstanceId,
                    RuntimeUsbInstanceId,
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

        var ioHandle = CreateFile(
            matchPath,
            GenericRead | GenericWrite,
            FileShareRead | FileShareWrite,
            IntPtr.Zero,
            OpenExisting,
            FileFlagOverlapped,
            IntPtr.Zero);
        if (ioHandle.IsInvalid)
        {
            var error = Marshal.GetLastWin32Error();
            ioHandle.Dispose();
            throw new Win32Exception(error, "The verified CodexKeyboard HID interface could not be opened for I/O.");
        }
        return ioHandle;
    }

    private static bool MatchesRuntime(SafeFileHandle handle)
    {
        var attributes = new HiddAttributes { Size = Marshal.SizeOf<HiddAttributes>() };
        if (!HidD_GetAttributes(handle, ref attributes))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "HidD_GetAttributes failed.");
        }
        if (attributes.VendorId != RuntimeVendorId || attributes.ProductId != RuntimeProductId)
        {
            return false;
        }

        if (attributes.VersionNumber != RuntimeRelease)
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
            return caps.UsagePage == RuntimeUsagePage && caps.Usage == RuntimeUsage &&
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

        return GetDeviceInstanceId(parent);
    }

    private static string GetDeviceInstanceId(uint deviceInstance)
    {
        var result = CM_Get_Device_ID_Size(out var characterCount, deviceInstance, 0);
        if (result != CrSuccess)
        {
            throw new InvalidOperationException($"CM_Get_Device_ID_Size failed with CONFIGRET 0x{result:X8}.");
        }

        var buffer = new StringBuilder(checked((int)characterCount + 1));
        result = CM_Get_Device_ID(deviceInstance, buffer, characterCount + 1, 0);
        if (result != CrSuccess)
        {
            throw new InvalidOperationException($"CM_Get_Device_ID failed with CONFIGRET 0x{result:X8}.");
        }
        return buffer.ToString();
    }

    private static uint? OpenBootloader(bool allowTransientUnavailable = false)
    {
        var presentCount = CountPresentBootloaderDevnodes();
        uint? match = null;
        var descriptorReadFailed = false;
        for (uint index = 0; index < WchDeviceCapacity; index++)
        {
            if (CH375OpenDevice(index) == new IntPtr(-1))
            {
                continue;
            }

            var descriptor = new UsbDeviceDescriptor();
            var length = (uint)Marshal.SizeOf<UsbDeviceDescriptor>();
            if (!CH375GetDeviceDescr(index, ref descriptor, ref length))
            {
                CH375CloseDevice(index);
                descriptorReadFailed = true;
                continue;
            }

            var isMatch = length == Marshal.SizeOf<UsbDeviceDescriptor>() &&
                          descriptor.Length == Marshal.SizeOf<UsbDeviceDescriptor>() &&
                          descriptor.DescriptorType == 1 &&
                          descriptor.VendorId == BootloaderVendorId &&
                          descriptor.ProductId == BootloaderProductId &&
                          descriptor.DeviceRelease == BootloaderRelease &&
                          descriptor.DeviceClass == BootloaderClass &&
                          descriptor.DeviceSubclass == BootloaderSubclass &&
                          descriptor.DeviceProtocol == BootloaderProtocol;
            if (!isMatch)
            {
                CH375CloseDevice(index);
                continue;
            }

            if (match is not null)
            {
                CH375CloseDevice(index);
                CH375CloseDevice(match.Value);
                throw new InvalidOperationException("Multiple matching CH552 ROM bootloaders were found.");
            }
            match = index;
        }

        if (descriptorReadFailed)
        {
            if (match is not null)
            {
                CH375CloseDevice(match.Value);
            }
            if (allowTransientUnavailable)
            {
                return null;
            }
            throw new InvalidOperationException("A WCH device descriptor could not be read; bootloader identity is ambiguous.");
        }

        if (presentCount == 0)
        {
            if (match is not null)
            {
                CH375CloseDevice(match.Value);
                if (allowTransientUnavailable)
                {
                    return null;
                }
                throw new InvalidOperationException(
                    "The WCH API found a bootloader that is absent from the PnP device tree.");
            }
            return null;
        }

        if (match is null)
        {
            if (allowTransientUnavailable)
            {
                return null;
            }
            throw new InvalidOperationException(
                "The CH552 bootloader is present but cannot be opened and verified through the WCH API.");
        }
        return match;
    }

    private static int CountPresentBootloaderDevnodes()
    {
        var count = EnumeratePresentUsbDeviceInstanceIds().Count(
            instanceId => instanceId.StartsWith(
                BootloaderUsbInstancePrefix, StringComparison.OrdinalIgnoreCase));
        if (count > 1)
        {
            throw new InvalidOperationException("Multiple CH552 ROM bootloader devnodes are present.");
        }
        return count;
    }

    private static IEnumerable<string> EnumeratePresentUsbDeviceInstanceIds()
    {
        var deviceInfoSet = SetupDiGetClassDevsForEnumerator(
            IntPtr.Zero, "USB", IntPtr.Zero, DigcfPresent | DigcfAllClasses);
        if (deviceInfoSet == new IntPtr(-1))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "USB device enumeration failed.");
        }

        try
        {
            for (uint index = 0; ; index++)
            {
                var deviceInfo = new SpDeviceInfoData
                {
                    Size = Marshal.SizeOf<SpDeviceInfoData>()
                };
                if (!SetupDiEnumDeviceInfo(deviceInfoSet, index, ref deviceInfo))
                {
                    var error = Marshal.GetLastWin32Error();
                    if (error == ErrorNoMoreItems)
                    {
                        yield break;
                    }
                    throw new Win32Exception(error, "SetupDiEnumDeviceInfo failed.");
                }
                yield return GetDeviceInstanceId(deviceInfo.DeviceInstance);
            }
        }
        finally
        {
            SetupDiDestroyDeviceInfoList(deviceInfoSet);
        }
    }

    private static void DeleteStaleTransitionMarker()
    {
        if (!File.Exists(TransitionMarkerPath))
        {
            return;
        }
        var age = DateTimeOffset.UtcNow - File.GetLastWriteTimeUtc(TransitionMarkerPath);
        if (age > TimeSpan.FromSeconds(10))
        {
            File.Delete(TransitionMarkerPath);
        }
        else
        {
            throw new InvalidOperationException("A recent bootloader transition is already pending.");
        }
    }

    private static DateTimeOffset ValidateRecentTransitionMarker()
    {
        if (!File.Exists(TransitionMarkerPath))
        {
            throw new InvalidOperationException(
                "EXIT requires a bootloader transition just initiated from this keyboard by ENTER.");
        }

        var created = DateTimeOffset.Parse(
            File.ReadAllText(TransitionMarkerPath),
            System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.RoundtripKind);
        var age = DateTimeOffset.UtcNow - created;
        if (age < TimeSpan.Zero || age > TimeSpan.FromSeconds(10))
        {
            throw new InvalidOperationException("The bootloader transition authorization has expired.");
        }
        return created;
    }

    private static void ConsumeRecentTransitionMarker(DateTimeOffset expectedCreated)
    {
        if (ValidateRecentTransitionMarker() != expectedCreated)
        {
            throw new InvalidOperationException("The bootloader transition authorization changed unexpectedly.");
        }
        File.Delete(TransitionMarkerPath);
    }

    private static void ValidateIspEndResponse(ReadOnlySpan<byte> response)
    {
        if (!response.SequenceEqual(IspEndResponse))
        {
            throw new InvalidDataException(
                $"Unexpected ISP_END response: {Convert.ToHexString(response)}");
        }
    }

    private static void RunSelfTest()
    {
        var command = HidProtocol.CreateEnterBootloader(0x13);
        var expectedCommand = Convert.FromHexString(
            "01010513434B424F4F544C4F41444552");
        if (!command.SequenceEqual(expectedCommand))
        {
            throw new InvalidOperationException("ENTER_BOOTLOADER vector check failed.");
        }
        ValidateIspEndResponse(IspEndResponse);
        try
        {
            ValidateIspEndResponse([0xA2, 0x00, 0x00, 0x00]);
            throw new InvalidOperationException("The malformed ISP_END response was accepted.");
        }
        catch (InvalidDataException)
        {
        }
        Console.WriteLine("Bootloader tool self-test passed.");
    }

    private static void PrintUsage()
    {
        Console.Error.WriteLine("Usage: CodexKeyboard.BootloaderTool <status|enter|exit|self-test>");
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

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    private struct UsbDeviceDescriptor
    {
        public byte Length;
        public byte DescriptorType;
        public ushort UsbSpecification;
        public byte DeviceClass;
        public byte DeviceSubclass;
        public byte DeviceProtocol;
        public byte MaxPacketSize0;
        public ushort VendorId;
        public ushort ProductId;
        public ushort DeviceRelease;
        public byte ManufacturerIndex;
        public byte ProductIndex;
        public byte SerialIndex;
        public byte ConfigurationCount;
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

    [DllImport(
        "setupapi.dll",
        EntryPoint = "SetupDiGetClassDevsW",
        CharSet = CharSet.Unicode,
        SetLastError = true)]
    private static extern IntPtr SetupDiGetClassDevsForEnumerator(
        IntPtr classGuid, string enumerator, IntPtr parent, uint flags);

    [DllImport("setupapi.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetupDiEnumDeviceInterfaces(
        IntPtr deviceInfoSet,
        IntPtr deviceInfoData,
        ref Guid interfaceClassGuid,
        uint memberIndex,
        ref SpDeviceInterfaceData deviceInterfaceData);

    [DllImport("setupapi.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetupDiEnumDeviceInfo(
        IntPtr deviceInfoSet, uint memberIndex, ref SpDeviceInfoData deviceInfoData);

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

    [DllImport("CH375DLL64.dll", CallingConvention = CallingConvention.Winapi)]
    private static extern IntPtr CH375OpenDevice(uint index);

    [DllImport("CH375DLL64.dll", CallingConvention = CallingConvention.Winapi)]
    private static extern void CH375CloseDevice(uint index);

    [DllImport("CH375DLL64.dll", CallingConvention = CallingConvention.Winapi)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CH375GetDeviceDescr(
        uint index, ref UsbDeviceDescriptor descriptor, ref uint length);

    [DllImport("CH375DLL64.dll", CallingConvention = CallingConvention.Winapi)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CH375SetExclusive(uint index, uint exclusive);

    [DllImport("CH375DLL64.dll", CallingConvention = CallingConvention.Winapi)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CH375SetTimeout(uint index, uint writeTimeout, uint readTimeout);

    [DllImport("CH375DLL64.dll", CallingConvention = CallingConvention.Winapi)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CH375WriteData(uint index, byte[] buffer, ref uint length);

    [DllImport("CH375DLL64.dll", CallingConvention = CallingConvention.Winapi)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CH375ReadData(uint index, byte[] buffer, ref uint length);
}
