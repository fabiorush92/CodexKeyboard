using CodexKeyboard.Protocol;
using System.Text.RegularExpressions;

namespace CodexKeyboard.Protocol.Check;

internal static class Program
{
    private static void Main()
    {
        CheckHostVectors();
        CheckDeviceVectors();
        CheckRejectedReports();
        CheckFirmwareContract();
        Assert(HidProtocol.NextSequence(0xFF) == 0x00, "Sequence wraparound failed.");

        Console.WriteLine(
            "CodexKeyboard HID v1 protocol check passed: 13 vectors, 10 rejection cases, and firmware contract parity.");
    }

    private static void CheckHostVectors()
    {
        var vectors = new (string Name, string Hex, Func<byte[]> Encode)[]
        {
            ("GET_INFO", "01 01 01 10 00 00 00 00 00 00 00 00 00 00 00 00",
                () => HidProtocol.CreateGetInfo(0x10)),
            ("SET_SCENE", "01 01 02 11 04 02 20 64 00 00 00 00 00 00 00 00",
                () => HidProtocol.CreateSetScene(0x11, Scene.EffortHigh, Effect.Breathe, 0x20, 100, 0x20)),
            ("SET_RGB", "01 01 03 12 20 00 00 00 20 00 00 00 20 00 00 00",
                () => HidProtocol.CreateSetRgb(0x12, new byte[] { 0x20, 0, 0, 0, 0x20, 0, 0, 0, 0x20 }, 0x20)),
            ("PING", "01 01 04 FF 00 00 00 00 00 00 00 00 00 00 00 00",
                () => HidProtocol.CreatePing(0xFF)),
            ("ENTER_BOOTLOADER", "01 01 05 13 43 4B 42 4F 4F 54 4C 4F 41 44 45 52",
                () => HidProtocol.CreateEnterBootloader(0x13)),
        };

        foreach (var vector in vectors)
        {
            var expected = Hex(vector.Hex);
            var actual = vector.Encode();
            Assert(actual.SequenceEqual(expected), $"{vector.Name} encoding differs from its vector.");
            var decoded = HidProtocol.Decode(actual, ReportDirection.HostToDevice);
            Assert(decoded.Sequence == actual[3], $"{vector.Name} sequence did not round-trip.");
        }
    }

    private static void CheckDeviceVectors()
    {
        var input = HidProtocol.DecodeInputEvent(Hex(
            "01 01 81 7F 05 03 FF 00 05 00 00 00 00 00 00 00"));
        Assert(input == new InputEvent(0x7F, Control.Encoder, InputKind.Rotate, -1, InputFlags.None,
            ButtonState.Button1 | ButtonState.Button3), "INPUT_EVENT decoding failed.");

        var info = HidProtocol.DecodeDeviceInfo(Hex(
            "01 01 82 10 01 01 00 7F 00 04 01 03 FF 00 00 00"));
        Assert(info == new DeviceInfo(0x10, 1, 1, 0, Capabilities.RequiredV1, 4, 1, 3, 0xFF),
            "DEVICE_INFO decoding failed.");

        var ack = HidProtocol.DecodeAck(Hex(
            "01 01 83 13 05 00 00 00 00 00 00 00 00 00 00 00"));
        Assert(ack == new CommandAck(0x13, MessageType.EnterBootloader), "ACK decoding failed.");

        var errors = new (string Hex, CommandError Expected)[]
        {
            ("01 01 84 20 01 01 01 00 00 00 00 00 00 00 00 00",
                new CommandError(0x20, 0x01, ProtocolErrorCode.UnsupportedVersion, 0x01)),
            ("01 01 84 21 7F 02 02 00 00 00 00 00 00 00 00 00",
                new CommandError(0x21, 0x7F, ProtocolErrorCode.UnknownMessageType, 0x02)),
            ("01 01 84 22 81 03 02 00 00 00 00 00 00 00 00 00",
                new CommandError(0x22, 0x81, ProtocolErrorCode.WrongDirection, 0x02)),
            ("01 01 84 23 01 04 04 00 00 00 00 00 00 00 00 00",
                new CommandError(0x23, 0x01, ProtocolErrorCode.InvalidPayload, 0x04)),
            ("01 01 84 24 03 05 04 00 00 00 00 00 00 00 00 00",
                new CommandError(0x24, 0x03, ProtocolErrorCode.UnsupportedValue, 0x04)),
        };

        foreach (var vector in errors)
        {
            Assert(HidProtocol.DecodeError(Hex(vector.Hex)) == vector.Expected, "ERROR decoding failed.");
        }
    }

    private static void CheckRejectedReports()
    {
        const string getInfo = "01 01 01 10 00 00 00 00 00 00 00 00 00 00 00 00";
        const string input = "01 01 81 7F 05 03 FF 00 05 00 00 00 00 00 00 00";

        Expect<FormatException>(() => HidProtocol.Decode(new byte[15], ReportDirection.HostToDevice));
        Expect<FormatException>(() => HidProtocol.Decode(Mutate(getInfo, 0, 0x02), ReportDirection.HostToDevice));
        Expect<FormatException>(() => HidProtocol.Decode(Mutate(getInfo, 1, 0x02), ReportDirection.HostToDevice));
        Expect<FormatException>(() => HidProtocol.Decode(Mutate(getInfo, 2, 0x7F), ReportDirection.HostToDevice));
        Expect<FormatException>(() => HidProtocol.Decode(Hex(input), ReportDirection.HostToDevice));
        Expect<FormatException>(() => HidProtocol.Decode(Mutate(getInfo, 4, 0x01), ReportDirection.HostToDevice));
        Expect<FormatException>(() => HidProtocol.Decode(
            Mutate("01 01 05 13 43 4B 42 4F 4F 54 4C 4F 41 44 45 52", 15, 0x00),
            ReportDirection.HostToDevice));

        var invalidButtonState = Hex(input);
        invalidButtonState[4] = (byte)Control.Button1;
        invalidButtonState[5] = (byte)InputKind.Press;
        invalidButtonState[6] = 0;
        invalidButtonState[8] = 0;
        Expect<FormatException>(() => HidProtocol.DecodeInputEvent(invalidButtonState));

        invalidButtonState[7] = (byte)InputFlags.QueueOverflow;
        var resynchronized = HidProtocol.DecodeInputEvent(invalidButtonState);
        Assert(resynchronized.Flags == InputFlags.QueueOverflow &&
               resynchronized.Buttons == ButtonState.None,
            "An overflow event must carry the current button state for resynchronization.");

        Expect<FormatException>(() =>
            HidProtocol.CreateSetScene(0, Scene.EffortHigh, Effect.Solid, 0x20, 1, 0x20));
        Expect<ArgumentOutOfRangeException>(() =>
            HidProtocol.CreateSetRgb(0, new byte[] { 0x21, 0, 0, 0, 0, 0, 0, 0, 0 }, 0x20));
    }

    private static void CheckFirmwareContract()
    {
        var root = FindRepositoryRoot();
        var protocol = File.ReadAllText(Path.Combine(
            root, "firmware", "CodexKeyboard", "src", "protocol.h"));
        var led = File.ReadAllText(Path.Combine(
            root, "firmware", "CodexKeyboard", "src", "led.h"));
        var descriptor = File.ReadAllText(Path.Combine(
            root, "firmware", "CodexKeyboard", "src", "usb", "USBconstant.c"));
        var usbHandler = File.ReadAllText(Path.Combine(
            root, "firmware", "CodexKeyboard", "src", "usb", "USBhandler.c"));
        var usbHid = File.ReadAllText(Path.Combine(
            root, "firmware", "CodexKeyboard", "src", "usb", "USBHID.c"));
        var protocolImplementation = File.ReadAllText(Path.Combine(
            root, "firmware", "CodexKeyboard", "src", "protocol.cpp"));

        RequireToken(protocol, $"#define CK_REPORT_LENGTH {HidProtocol.ReportLength}");
        RequireToken(protocol, $"#define CK_REPORT_ID 0x{HidProtocol.ReportId:X2}");
        RequireToken(protocol, $"#define CK_PROTOCOL_VERSION 0x{HidProtocol.Version:X2}");
        RequireToken(protocol, $"#define CK_COMMAND_TIMEOUT_MS {HidProtocol.CommandTimeoutMs}");
        RequireToken(protocol, $"#define CK_PING_INTERVAL_MS {HidProtocol.PingIntervalMs}");
        RequireToken(protocol, $"#define CK_HEARTBEAT_TIMEOUT_MS {HidProtocol.HeartbeatTimeoutMs}");
        RequireToken(protocol, $"#define CK_MAX_EFFECT_PERIOD_10MS {HidProtocol.MaximumEffectPeriod10Ms}");
        RequireToken(protocol, "#define CK_FIRMWARE_VERSION_MAJOR 1");
        RequireToken(protocol, "#define CK_FIRMWARE_VERSION_MINOR 1");
        RequireToken(protocol, "#define CK_FIRMWARE_VERSION_PATCH 0");
        RequireToken(protocol, $"#define CK_CAPABILITIES 0x{(ushort)Capabilities.RequiredV1:X4}");
        RequireToken(protocol, "#define CK_BOOTLOADER_ARM_TOKEN \"CKBOOTLOADER\"");
        RequireToken(protocol, $"#define CK_BOOTLOADER_ARM_TOKEN_LENGTH {HidProtocol.BootloaderArmTokenLength}");

        var enumTokens = new (string FirmwareName, byte Value)[]
        {
            ("CK_MSG_GET_INFO", (byte)MessageType.GetInfo),
            ("CK_MSG_SET_SCENE", (byte)MessageType.SetScene),
            ("CK_MSG_SET_RGB", (byte)MessageType.SetRgb),
            ("CK_MSG_PING", (byte)MessageType.Ping),
            ("CK_MSG_ENTER_BOOTLOADER", (byte)MessageType.EnterBootloader),
            ("CK_MSG_INPUT_EVENT", (byte)MessageType.InputEvent),
            ("CK_MSG_DEVICE_INFO", (byte)MessageType.DeviceInfo),
            ("CK_MSG_ACK", (byte)MessageType.Ack),
            ("CK_MSG_ERROR", (byte)MessageType.Error),
            ("CK_CONTROL_BUTTON_1", (byte)Control.Button1),
            ("CK_CONTROL_BUTTON_2", (byte)Control.Button2),
            ("CK_CONTROL_BUTTON_3", (byte)Control.Button3),
            ("CK_CONTROL_KNOB_BUTTON", (byte)Control.KnobButton),
            ("CK_CONTROL_ENCODER", (byte)Control.Encoder),
            ("CK_INPUT_PRESS", (byte)InputKind.Press),
            ("CK_INPUT_RELEASE", (byte)InputKind.Release),
            ("CK_INPUT_ROTATE", (byte)InputKind.Rotate),
            ("CK_SCENE_COMPANION_ABSENT", (byte)Scene.CompanionAbsent),
            ("CK_SCENE_COMPANION_CONNECTED", (byte)Scene.CompanionConnected),
            ("CK_SCENE_CODEX_UNAVAILABLE", (byte)Scene.CodexUnavailable),
            ("CK_SCENE_EFFORT_MEDIUM", (byte)Scene.EffortMedium),
            ("CK_SCENE_EFFORT_HIGH", (byte)Scene.EffortHigh),
            ("CK_SCENE_EFFORT_ULTRA", (byte)Scene.EffortUltra),
            ("CK_SCENE_ACTION_SUCCEEDED", (byte)Scene.ActionSucceeded),
            ("CK_SCENE_ACTION_FAILED", (byte)Scene.ActionFailed),
            ("CK_SCENE_TURN_ACTIVE", (byte)Scene.TurnActive),
            ("CK_SCENE_WAITING_FOR_APPROVAL", (byte)Scene.WaitingForApproval),
            ("CK_SCENE_COMPLETED", (byte)Scene.Completed),
            ("CK_EFFECT_SOLID", (byte)Effect.Solid),
            ("CK_EFFECT_BLINK", (byte)Effect.Blink),
            ("CK_EFFECT_BREATHE", (byte)Effect.Breathe),
            ("CK_ERROR_UNSUPPORTED_VERSION", (byte)ProtocolErrorCode.UnsupportedVersion),
            ("CK_ERROR_UNKNOWN_MESSAGE_TYPE", (byte)ProtocolErrorCode.UnknownMessageType),
            ("CK_ERROR_WRONG_DIRECTION", (byte)ProtocolErrorCode.WrongDirection),
            ("CK_ERROR_INVALID_PAYLOAD", (byte)ProtocolErrorCode.InvalidPayload),
            ("CK_ERROR_UNSUPPORTED_VALUE", (byte)ProtocolErrorCode.UnsupportedValue),
        };
        foreach (var token in enumTokens)
        {
            RequireToken(protocol, $"{token.FirmwareName} = 0x{token.Value:X2}");
        }

        RequireToken(led, "#define LED_MAX_COMPONENT 255");
        RequireToken(descriptor, ".VendorID = 0x1209");
        RequireToken(descriptor, ".ProductID = 0xC55D");
        RequireToken(descriptor, ".ReleaseNumber = VERSION_BCD(1, 1, 0)");
        RequireToken(descriptor, "0x06, 0x00, 0xFF");
        RequireToken(descriptor, "0x85, 0x01");
        RequireToken(descriptor, "0x95, 0x0F");
        RequireToken(usbHandler,
            "case USB_SET_CONFIGURATION: " +
            "if (UsbSetupBuf->bRequestType != 0x00 || UsbSetupBuf->wValueH || " +
            "UsbSetupBuf->wValueL > 1 || UsbSetupBuf->wIndexH || " +
            "UsbSetupBuf->wIndexL || SetupLen) { len = 0xFF; break; } " +
            "UsbConfig = UsbSetupBuf->wValueL; USB_transport_reset(); break;");
        RequireToken(usbHandler,
            "if (UsbSetupBuf->bRequestType != 0x02 || " +
            "UsbSetupBuf->wValueH || UsbSetupBuf->wValueL || " +
            "UsbSetupBuf->wIndexH || SetupLen || UsbConfig != 1 || " +
            "(UsbSetupBuf->wIndexL != 0x81 && UsbSetupBuf->wIndexL != 0x01)) " +
            "{ len = 0xFF; break; } USB_clear_endpoint_halt(UsbSetupBuf->wIndexL);");
        RequireToken(usbHandler,
            "if (UsbSetupBuf->bRequestType != 0x02 || " +
            "UsbSetupBuf->wValueH || UsbSetupBuf->wValueL || " +
            "UsbSetupBuf->wIndexH || SetupLen || UsbConfig != 1 || " +
            "(UsbSetupBuf->wIndexL != 0x81 && UsbSetupBuf->wIndexL != 0x01)) " +
            "{ len = 0xFF; break; } USB_set_endpoint_halt(UsbSetupBuf->wIndexL);");
        RequireToken(usbHandler,
            "case USB_GET_STATUS: if (UsbSetupBuf->wValueH || " +
            "UsbSetupBuf->wValueL || SetupLen != 2) { len = 0xFF; break; }");
        RequireToken(usbHandler,
            "if (endpoint == 0x81 || endpoint == 0x01) { if (UsbConfig != 1) " +
            "{ len = 0xFF; break; } " +
            "Ep0Buffer[0] = USB_endpoint_halted((uint8_t)endpoint);");
        RequireToken(usbHandler,
            "UEP1_CTRL = bUEP_AUTO_TOG | UEP_T_RES_NAK | UEP_R_RES_NAK;");
        RequireToken(usbHid,
            "uint8_t USB_take_transport_reset(void) { uint8_t pending; " +
            "uint8_t interrupts_enabled = EA; EA = 0; pending = usb_reset_pending_s; " +
            "usb_reset_pending_s = 0; EA = interrupts_enabled; return pending; }");
        RequireToken(usbHid,
            "uint8_t USB_claim_report_completion(void) { uint8_t ready; " +
            "uint8_t interrupts_enabled = EA; EA = 0; ready = usb_in_complete_s && " +
            "!usb_reset_pending_s && !usb_in_halted_s && !UIF_BUS_RST && !UIF_TRANSFER;");
        RequireToken(usbHid,
            "if (UsbConfig && !usb_reset_pending_s && !usb_in_halted_s && " +
            "!usb_in_busy_s)");
        RequireToken(usbHid,
            "if (!usb_reset_pending_s && !usb_out_halted_s && usb_out_pending_s)");
        RequireToken(usbHid,
            "void USB_EP1_IN(void) { if (usb_in_halted_s) { return; }");
        RequireToken(usbHid,
            "void USB_EP1_OUT(void) { if (!usb_out_halted_s && U_TOG_OK)");
        RequireToken(usbHid,
            "void USB_set_endpoint_halt(uint8_t endpoint_address) { " +
            "reset_transfer_state();");
        RequireToken(usbHid,
            "static void update_endpoint_responses(void) { uint8_t control = " +
            "UEP1_CTRL & (bUEP_T_TOG | bUEP_R_TOG);");
        RequireToken(usbHid,
            "uint8_t USB_endpoint_halted(uint8_t endpoint_address) { " +
            "return endpoint_address == 0x81 ? usb_in_halted_s : usb_out_halted_s; }");
        RequireToken(usbHid,
            "void USB_clear_endpoint_halt(uint8_t endpoint_address) { " +
            "reset_transfer_state();");
        RequireToken(usbHid,
            "void USB_cancel_pending_in(void) { uint8_t interrupts_enabled = EA; " +
            "EA = 0; usb_in_busy_s = 0; usb_in_complete_s = 0; UEP1_T_LEN = 0;");
        RequireToken(usbHid,
            "void USB_transport_reset(void) { reset_transfer_state(); " +
            "usb_in_halted_s = 0; usb_out_halted_s = 0; " +
            "UEP1_CTRL = bUEP_AUTO_TOG | UEP_T_RES_NAK | " +
            "(UsbConfig ? UEP_R_RES_ACK : UEP_R_RES_NAK); }");
        RequireToken(protocolImplementation,
            "if (USB_take_transport_reset()) { close_session(); } if (!UsbConfig)");
        RequireToken(protocolImplementation,
            "static void close_session(void) { USB_cancel_pending_in();");
        RequireToken(protocolImplementation,
            "if (USB_claim_report_completion()) { led_show_bootloader(); BOOT_now(); }");
    }

    private static string FindRepositoryRoot()
    {
        for (DirectoryInfo? directory = new(Directory.GetCurrentDirectory());
             directory is not null;
             directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "README.md")) &&
                File.Exists(Path.Combine(directory.FullName, "firmware", "CodexKeyboard", "src", "protocol.h")))
            {
                return directory.FullName;
            }
        }

        throw new DirectoryNotFoundException("Could not locate the CodexKeyboard repository root.");
    }

    private static void RequireToken(string source, string expected)
    {
        var normalizedSource = Regex.Replace(source, @"\s+", " ");
        var normalizedExpected = Regex.Replace(expected, @"\s+", " ");
        Assert(normalizedSource.Contains(normalizedExpected, StringComparison.Ordinal),
            $"Firmware contract token is missing or differs: {expected}");
    }

    private static byte[] Hex(string value) => Convert.FromHexString(value.Replace(" ", string.Empty));

    private static byte[] Mutate(string value, int index, byte replacement)
    {
        var bytes = Hex(value);
        bytes[index] = replacement;
        return bytes;
    }

    private static void Expect<TException>(Action action)
        where TException : Exception
    {
        try
        {
            action();
        }
        catch (TException)
        {
            return;
        }

        throw new InvalidOperationException($"Expected {typeof(TException).Name}.");
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }
}
