using CodexKeyboard.Device;
using CodexKeyboard.Protocol;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;

namespace CodexKeyboard.BootloaderTool;

internal static class Program
{
    private const string BootloaderUsbInstancePrefix = "USB\\VID_4348&PID_55E0\\";

    private const ushort BootloaderVendorId = 0x4348;
    private const ushort BootloaderProductId = 0x55E0;
    private const ushort BootloaderRelease = 0x0250;
    private const byte BootloaderClass = 0xFF;
    private const byte BootloaderSubclass = 0x80;
    private const byte BootloaderProtocol = 0x55;

    private const uint DigcfPresent = 0x00000002;
    private const uint DigcfAllClasses = 0x00000004;
    private const int ErrorNoMoreItems = 259;
    private const uint CrSuccess = 0;
    private const int WchDeviceCapacity = 16;

    private static readonly byte[] IspEndRequest = [0xA2, 0x01, 0x00, 0x01];
    private static readonly byte[] IspEndResponse = [0xA2, 0x00, 0x02, 0x00, 0x00, 0x00];

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

        await using var device = await CodexKeyboardDevice.TryOpenAsync() ??
            throw new InvalidOperationException("The exact CodexKeyboard runtime device was not found.");
        var info = device.Info;
        if (info.FirmwareMajor != 1 || info.FirmwareMinor != 1 || info.FirmwarePatch != 0 ||
            !info.Capabilities.HasFlag(Capabilities.RemoteBootloader))
        {
            throw new InvalidOperationException("The connected firmware does not support guarded remote bootloader entry.");
        }

        await device.EnterBootloaderAsync();
        await WaitForBootloaderAsync(TimeSpan.FromSeconds(3));
        await using var unexpectedRuntime = await CodexKeyboardDevice.TryOpenAsync();
        if (unexpectedRuntime is not null)
        {
            throw new InvalidOperationException("The runtime remained present after the bootloader transition.");
        }
        File.WriteAllText(CodexKeyboardIdentity.TransitionMarkerPath, DateTimeOffset.UtcNow.ToString("O"));
        Console.WriteLine("BOOTLOADER_READY: 4348:55E0, CH552 ROM 2.50");
    }

    private static async Task ShowRuntimeStatusAsync()
    {
        await using var device = await CodexKeyboardDevice.TryOpenAsync() ??
            throw new InvalidOperationException("The exact CodexKeyboard runtime device was not found.");
        var info = device.Info;
        Console.WriteLine(
            $"RUNTIME_READY: firmware {info.FirmwareMajor}.{info.FirmwareMinor}.{info.FirmwarePatch}, " +
            $"capabilities 0x{(ushort)info.Capabilities:X4}, serial {CodexKeyboardIdentity.Serial}");
    }

    private static async Task ExitBootloaderAsync()
    {
        var transitionCreated = ValidateRecentTransitionMarker();
        using var verificationLease = CodexKeyboardTransitionVerificationLease.Acquire();
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
                await using var device =
                    await CodexKeyboardDevice.TryOpenForTransitionVerificationAsync();
                if (device is not null)
                {
                    return;
                }
            }
            catch (Exception exception) when (
                exception is IOException or TimeoutException or Win32Exception or CodexKeyboardDeviceBusyException)
            {
                // The runtime can enumerate before its HID endpoints are ready; retry within the deadline.
            }
            await Task.Delay(50);
        }
        throw new TimeoutException("The exact CodexKeyboard runtime did not return after ISP_END.");
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

    private static void DeleteStaleTransitionMarker(
        string? markerPath = null,
        DateTimeOffset? currentTime = null)
    {
        markerPath ??= CodexKeyboardIdentity.TransitionMarkerPath;
        if (!File.Exists(markerPath))
        {
            return;
        }
        var age = (currentTime ?? DateTimeOffset.UtcNow) - File.GetLastWriteTimeUtc(markerPath);
        if (age > CodexKeyboardIdentity.TransitionAuthorizationLifetime)
        {
            File.Delete(markerPath);
        }
        else
        {
            throw new InvalidOperationException("A recent bootloader transition is already pending.");
        }
    }

    private static DateTimeOffset ValidateRecentTransitionMarker(
        string? markerPath = null,
        DateTimeOffset? currentTime = null)
    {
        markerPath ??= CodexKeyboardIdentity.TransitionMarkerPath;
        if (!File.Exists(markerPath))
        {
            throw new InvalidOperationException(
                "EXIT requires a bootloader transition just initiated from this keyboard by ENTER.");
        }

        var created = DateTimeOffset.Parse(
            File.ReadAllText(markerPath),
            System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.RoundtripKind);
        var age = (currentTime ?? DateTimeOffset.UtcNow) - created;
        if (age < TimeSpan.Zero || age > CodexKeyboardIdentity.TransitionAuthorizationLifetime)
        {
            throw new InvalidOperationException("The bootloader transition authorization has expired.");
        }
        return created;
    }

    private static void ConsumeRecentTransitionMarker(
        DateTimeOffset expectedCreated,
        string? markerPath = null,
        DateTimeOffset? currentTime = null)
    {
        markerPath ??= CodexKeyboardIdentity.TransitionMarkerPath;
        if (ValidateRecentTransitionMarker(markerPath, currentTime) != expectedCreated)
        {
            throw new InvalidOperationException("The bootloader transition authorization changed unexpectedly.");
        }
        File.Delete(markerPath);
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

        CheckTransitionMarker();
        Console.WriteLine("Bootloader tool self-test passed.");
    }

    private static void CheckTransitionMarker()
    {
        var markerPath = Path.Combine(
            Path.GetTempPath(), $"CodexKeyboard-BootloaderTool-{Guid.NewGuid():N}.self-test");
        var currentTime = new DateTimeOffset(2026, 7, 22, 12, 0, 0, TimeSpan.Zero);
        var activeTime = currentTime - TimeSpan.FromSeconds(1);
        var staleTime = currentTime - CodexKeyboardIdentity.TransitionAuthorizationLifetime -
                        TimeSpan.FromSeconds(1);

        try
        {
            WriteTransitionMarker(markerPath, activeTime);
            ExpectInvalidOperation(
                () => DeleteStaleTransitionMarker(markerPath, currentTime),
                "An active transition marker was deleted.");
            if (ValidateRecentTransitionMarker(markerPath, currentTime) != activeTime)
            {
                throw new InvalidOperationException("An active transition marker changed during validation.");
            }
            ConsumeRecentTransitionMarker(activeTime, markerPath, currentTime);
            if (File.Exists(markerPath))
            {
                throw new InvalidOperationException("A consumed transition marker remained on disk.");
            }

            WriteTransitionMarker(markerPath, staleTime);
            ExpectInvalidOperation(
                () => ValidateRecentTransitionMarker(markerPath, currentTime),
                "A stale transition marker was accepted.");
            DeleteStaleTransitionMarker(markerPath, currentTime);
            if (File.Exists(markerPath))
            {
                throw new InvalidOperationException("A stale transition marker was not deleted.");
            }
        }
        finally
        {
            File.Delete(markerPath);
        }
    }

    private static void WriteTransitionMarker(string markerPath, DateTimeOffset created)
    {
        File.WriteAllText(markerPath, created.ToString("O"));
        File.SetLastWriteTimeUtc(markerPath, created.UtcDateTime);
    }

    private static void ExpectInvalidOperation(Action action, string failureMessage)
    {
        try
        {
            action();
        }
        catch (InvalidOperationException)
        {
            return;
        }
        throw new InvalidOperationException(failureMessage);
    }

    private static void PrintUsage()
    {
        Console.Error.WriteLine("Usage: CodexKeyboard.BootloaderTool <status|enter|exit|self-test>");
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SpDeviceInfoData
    {
        public int Size;
        public Guid ClassGuid;
        public uint DeviceInstance;
        public IntPtr Reserved;
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

    [DllImport(
        "setupapi.dll",
        EntryPoint = "SetupDiGetClassDevsW",
        CharSet = CharSet.Unicode,
        SetLastError = true)]
    private static extern IntPtr SetupDiGetClassDevsForEnumerator(
        IntPtr classGuid, string enumerator, IntPtr parent, uint flags);

    [DllImport("setupapi.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetupDiEnumDeviceInfo(
        IntPtr deviceInfoSet, uint memberIndex, ref SpDeviceInfoData deviceInfoData);

    [DllImport("setupapi.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetupDiDestroyDeviceInfoList(IntPtr deviceInfoSet);

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
