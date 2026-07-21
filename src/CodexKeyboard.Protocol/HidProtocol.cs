using System.Buffers.Binary;

namespace CodexKeyboard.Protocol;

public enum ReportDirection
{
    HostToDevice,
    DeviceToHost,
}

public enum MessageType : byte
{
    GetInfo = 0x01,
    SetScene = 0x02,
    SetRgb = 0x03,
    Ping = 0x04,
    InputEvent = 0x81,
    DeviceInfo = 0x82,
    Ack = 0x83,
    Error = 0x84,
}

public enum Control : byte
{
    Button1 = 0x01,
    Button2 = 0x02,
    Button3 = 0x03,
    KnobButton = 0x04,
    Encoder = 0x05,
}

public enum InputKind : byte
{
    Press = 0x01,
    Release = 0x02,
    Rotate = 0x03,
}

[Flags]
public enum InputFlags : byte
{
    None = 0,
    QueueOverflow = 1 << 0,
}

[Flags]
public enum ButtonState : byte
{
    None = 0,
    Button1 = 1 << 0,
    Button2 = 1 << 1,
    Button3 = 1 << 2,
    KnobButton = 1 << 3,
}

public enum Scene : byte
{
    CompanionAbsent = 0x00,
    CompanionConnected = 0x01,
    CodexUnavailable = 0x02,
    EffortMedium = 0x03,
    EffortHigh = 0x04,
    EffortUltra = 0x05,
    ActionSucceeded = 0x06,
    ActionFailed = 0x07,
    TurnActive = 0x08,
    WaitingForApproval = 0x09,
    Completed = 0x0A,
}

public enum Effect : byte
{
    Solid = 0x00,
    Blink = 0x01,
    Breathe = 0x02,
}

[Flags]
public enum Capabilities : ushort
{
    None = 0,
    Buttons = 1 << 0,
    Encoder = 1 << 1,
    Scenes = 1 << 2,
    DirectRgb = 1 << 3,
    HeartbeatWatchdog = 1 << 4,
    QueueOverflowReporting = 1 << 5,
    RequiredV1 = Buttons | Encoder | Scenes | DirectRgb | HeartbeatWatchdog | QueueOverflowReporting,
}

public enum ProtocolErrorCode : byte
{
    UnsupportedVersion = 0x01,
    UnknownMessageType = 0x02,
    WrongDirection = 0x03,
    InvalidPayload = 0x04,
    UnsupportedValue = 0x05,
}

public readonly record struct DecodedReport(MessageType Type, byte Sequence, byte[] Payload);

public readonly record struct InputEvent(
    byte Sequence,
    Control Control,
    InputKind Kind,
    sbyte Value,
    InputFlags Flags,
    ButtonState Buttons);

public readonly record struct DeviceInfo(
    byte Sequence,
    byte FirmwareMajor,
    byte FirmwareMinor,
    byte FirmwarePatch,
    Capabilities Capabilities,
    byte ButtonCount,
    byte EncoderCount,
    byte LedCount,
    byte MaximumBrightness);

public readonly record struct CommandAck(byte Sequence, MessageType Command);

public readonly record struct CommandError(
    byte Sequence,
    byte FailedMessageType,
    ProtocolErrorCode Error,
    byte Detail);

public static class HidProtocol
{
    public const int ReportLength = 16;
    public const byte ReportId = 0x01;
    public const byte Version = 0x01;
    public const ushort MaximumEffectPeriod10Ms = 6000;
    public const int CommandTimeoutMs = 250;
    public const int PingIntervalMs = 1000;
    public const int HeartbeatTimeoutMs = 3000;

    public static byte NextSequence(byte sequence) => unchecked((byte)(sequence + 1));

    public static byte[] CreateGetInfo(byte sequence) => CreateEmptyCommand(MessageType.GetInfo, sequence);

    public static byte[] CreatePing(byte sequence) => CreateEmptyCommand(MessageType.Ping, sequence);

    public static byte[] CreateSetScene(
        byte sequence,
        Scene scene,
        Effect effect,
        byte brightness,
        ushort period10Ms,
        byte maximumBrightness)
    {
        if (brightness > maximumBrightness)
        {
            throw new ArgumentOutOfRangeException(nameof(brightness));
        }

        var report = CreateReport(MessageType.SetScene, sequence);
        report[4] = (byte)scene;
        report[5] = (byte)effect;
        report[6] = brightness;
        BinaryPrimitives.WriteUInt16LittleEndian(report.AsSpan(7, 2), period10Ms);
        Validate(report, ReportDirection.HostToDevice);
        return report;
    }

    public static byte[] CreateSetRgb(
        byte sequence,
        ReadOnlySpan<byte> rgb,
        byte maximumBrightness)
    {
        if (rgb.Length != 9)
        {
            throw new ArgumentException("Exactly nine RGB components are required.", nameof(rgb));
        }

        for (var i = 0; i < rgb.Length; i++)
        {
            if (rgb[i] > maximumBrightness)
            {
                throw new ArgumentOutOfRangeException(nameof(rgb), $"RGB component {i} exceeds the device maximum.");
            }
        }

        var report = CreateReport(MessageType.SetRgb, sequence);
        rgb.CopyTo(report.AsSpan(4, 9));
        Validate(report, ReportDirection.HostToDevice);
        return report;
    }

    public static DecodedReport Decode(ReadOnlySpan<byte> bytes, ReportDirection direction)
    {
        Validate(bytes, direction);
        return new DecodedReport((MessageType)bytes[2], bytes[3], bytes[4..].ToArray());
    }

    public static InputEvent DecodeInputEvent(ReadOnlySpan<byte> bytes)
    {
        var report = DecodeExpected(bytes, MessageType.InputEvent);
        return new InputEvent(
            report.Sequence,
            (Control)report.Payload[0],
            (InputKind)report.Payload[1],
            unchecked((sbyte)report.Payload[2]),
            (InputFlags)report.Payload[3],
            (ButtonState)report.Payload[4]);
    }

    public static DeviceInfo DecodeDeviceInfo(ReadOnlySpan<byte> bytes)
    {
        var report = DecodeExpected(bytes, MessageType.DeviceInfo);
        return new DeviceInfo(
            report.Sequence,
            report.Payload[0],
            report.Payload[1],
            report.Payload[2],
            (Capabilities)BinaryPrimitives.ReadUInt16LittleEndian(report.Payload.AsSpan(3, 2)),
            report.Payload[5],
            report.Payload[6],
            report.Payload[7],
            report.Payload[8]);
    }

    public static CommandAck DecodeAck(ReadOnlySpan<byte> bytes)
    {
        var report = DecodeExpected(bytes, MessageType.Ack);
        return new CommandAck(report.Sequence, (MessageType)report.Payload[0]);
    }

    public static CommandError DecodeError(ReadOnlySpan<byte> bytes)
    {
        var report = DecodeExpected(bytes, MessageType.Error);
        return new CommandError(
            report.Sequence,
            report.Payload[0],
            (ProtocolErrorCode)report.Payload[1],
            report.Payload[2]);
    }

    private static byte[] CreateEmptyCommand(MessageType type, byte sequence)
    {
        var report = CreateReport(type, sequence);
        Validate(report, ReportDirection.HostToDevice);
        return report;
    }

    private static byte[] CreateReport(MessageType type, byte sequence)
    {
        var report = new byte[ReportLength];
        report[0] = ReportId;
        report[1] = Version;
        report[2] = (byte)type;
        report[3] = sequence;
        return report;
    }

    private static DecodedReport DecodeExpected(ReadOnlySpan<byte> bytes, MessageType expected)
    {
        var report = Decode(bytes, ReportDirection.DeviceToHost);
        if (report.Type != expected)
        {
            throw new FormatException($"Expected {expected}, received {report.Type}.");
        }

        return report;
    }

    private static void Validate(ReadOnlySpan<byte> report, ReportDirection direction)
    {
        if (report.Length != ReportLength)
        {
            throw new FormatException($"A HID report must contain exactly {ReportLength} bytes.");
        }

        if (report[0] != ReportId)
        {
            throw new FormatException("Unexpected HID report ID.");
        }

        if (report[1] != Version)
        {
            throw new FormatException("Unsupported protocol version.");
        }

        var type = (MessageType)report[2];
        if (!Enum.IsDefined(type))
        {
            throw new FormatException("Unknown message type.");
        }

        var isHostMessage = IsHostMessage(type);
        if ((direction == ReportDirection.HostToDevice) != isHostMessage)
        {
            throw new FormatException("Message type is not valid in this direction.");
        }

        switch (type)
        {
            case MessageType.GetInfo:
            case MessageType.Ping:
                RequireZero(report, 4);
                break;
            case MessageType.SetScene:
                ValidateSetScene(report);
                break;
            case MessageType.SetRgb:
                RequireZero(report, 13);
                break;
            case MessageType.InputEvent:
                ValidateInputEvent(report);
                break;
            case MessageType.DeviceInfo:
                ValidateDeviceInfo(report);
                break;
            case MessageType.Ack:
                ValidateAck(report);
                break;
            case MessageType.Error:
                ValidateError(report);
                break;
        }
    }

    private static bool IsHostMessage(MessageType type) =>
        type is MessageType.GetInfo or MessageType.SetScene or MessageType.SetRgb or MessageType.Ping;

    private static void ValidateSetScene(ReadOnlySpan<byte> report)
    {
        if (!Enum.IsDefined((Scene)report[4]))
        {
            throw new FormatException("Unknown scene.");
        }

        var effect = (Effect)report[5];
        if (!Enum.IsDefined(effect))
        {
            throw new FormatException("Unknown effect.");
        }

        var period = BinaryPrimitives.ReadUInt16LittleEndian(report[7..9]);
        if ((effect == Effect.Solid && period != 0) ||
            (effect != Effect.Solid && (period == 0 || period > MaximumEffectPeriod10Ms)))
        {
            throw new FormatException("Invalid effect period.");
        }

        RequireZero(report, 9);
    }

    private static void ValidateInputEvent(ReadOnlySpan<byte> report)
    {
        var control = (Control)report[4];
        var kind = (InputKind)report[5];
        var value = unchecked((sbyte)report[6]);

        if (!Enum.IsDefined(control) || !Enum.IsDefined(kind))
        {
            throw new FormatException("Unknown input control or kind.");
        }

        if ((report[7] & ~(byte)InputFlags.QueueOverflow) != 0 || (report[8] & 0xF0) != 0)
        {
            throw new FormatException("Unknown input flags or button-state bits.");
        }

        if (kind == InputKind.Rotate)
        {
            if (control != Control.Encoder || (value != -1 && value != 1))
            {
                throw new FormatException("A rotation must target the encoder with value -1 or +1.");
            }
        }
        else
        {
            if (control == Control.Encoder || value != 0)
            {
                throw new FormatException("A press or release must target a button with value zero.");
            }

            var overflow = (report[7] & (byte)InputFlags.QueueOverflow) != 0;
            var controlBit = 1 << ((byte)control - 1);
            var isPressed = (report[8] & controlBit) != 0;
            if (!overflow && (kind == InputKind.Press) != isPressed)
            {
                throw new FormatException("Button state does not match the input event.");
            }
        }

        RequireZero(report, 9);
    }

    private static void ValidateDeviceInfo(ReadOnlySpan<byte> report)
    {
        var capabilities = (Capabilities)BinaryPrimitives.ReadUInt16LittleEndian(report[7..9]);
        if (capabilities != Capabilities.RequiredV1)
        {
            throw new FormatException("The device does not advertise the complete v1 capability set.");
        }

        if (report[9] != 4 || report[10] != 1 || report[11] != 3 || report[12] == 0)
        {
            throw new FormatException("Unexpected CodexKeyboard hardware capabilities.");
        }

        RequireZero(report, 13);
    }

    private static void ValidateAck(ReadOnlySpan<byte> report)
    {
        var acknowledgedType = (MessageType)report[4];
        if (!Enum.IsDefined(acknowledgedType) || !IsHostMessage(acknowledgedType))
        {
            throw new FormatException("ACK does not identify a host command.");
        }

        RequireZero(report, 5);
    }

    private static void ValidateError(ReadOnlySpan<byte> report)
    {
        if (!Enum.IsDefined((ProtocolErrorCode)report[5]))
        {
            throw new FormatException("Unknown protocol error code.");
        }

        RequireZero(report, 7);
    }

    private static void RequireZero(ReadOnlySpan<byte> report, int start)
    {
        for (var i = start; i < report.Length; i++)
        {
            if (report[i] != 0)
            {
                throw new FormatException($"Reserved byte {i} must be zero.");
            }
        }
    }
}
