using CodexKeyboard.Protocol;

namespace CodexKeyboard.Device;

public readonly record struct InputSequenceUpdate(
    InputEvent Input,
    bool SequenceGap,
    byte MissingCount,
    bool Overflow,
    bool ShouldDispatch,
    bool WaitingForSequenceGap,
    bool WaitingForRelease,
    ButtonState Buttons);

public sealed class InputSequenceTracker
{
    private bool _hasSequence;
    private byte _lastSequence;

    public ButtonState Buttons { get; private set; }

    public bool WaitingForRelease { get; private set; }

    public bool WaitingForSequenceGap { get; private set; }

    public InputSequenceUpdate Observe(InputEvent input)
    {
        var expected = HidProtocol.NextSequence(_lastSequence);
        var sequenceGap = _hasSequence && input.Sequence != expected;
        var missingCount = sequenceGap ? unchecked((byte)(input.Sequence - expected)) : (byte)0;
        var overflow = input.Flags.HasFlag(InputFlags.QueueOverflow);
        _hasSequence = true;
        _lastSequence = input.Sequence;

        var suppress = sequenceGap || overflow || WaitingForSequenceGap || WaitingForRelease;
        if (overflow)
        {
            Buttons = input.Buttons;
            WaitingForSequenceGap = true;
            WaitingForRelease = Buttons != ButtonState.None;
        }
        else if (WaitingForSequenceGap)
        {
            if (sequenceGap)
            {
                Buttons = input.Buttons;
                WaitingForSequenceGap = false;
                WaitingForRelease = Buttons != ButtonState.None;
            }
        }
        else
        {
            Buttons = input.Buttons;
            if (sequenceGap)
            {
                WaitingForRelease = Buttons != ButtonState.None;
            }
            else if (WaitingForRelease && Buttons == ButtonState.None)
            {
                WaitingForRelease = false;
            }
        }

        return new InputSequenceUpdate(
            input,
            sequenceGap,
            missingCount,
            overflow,
            !suppress,
            WaitingForSequenceGap,
            WaitingForRelease,
            Buttons);
    }

    public void Reset()
    {
        _hasSequence = false;
        _lastSequence = 0;
        Buttons = ButtonState.None;
        WaitingForSequenceGap = false;
        WaitingForRelease = false;
    }
}
