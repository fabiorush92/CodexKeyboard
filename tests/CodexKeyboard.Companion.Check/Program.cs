using CodexKeyboard.Companion;
using CodexKeyboard.Protocol;
using DeviceButtonState = CodexKeyboard.Protocol.ButtonState;
using DeviceControl = CodexKeyboard.Protocol.Control;

namespace CodexKeyboard.Companion.Check;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        var service = new KeyboardService();
        using (var form = new HardwareTestForm(service))
        {
            form.AddInput(new InputObservation(
                new InputEvent(
                    0x02,
                    DeviceControl.Button1,
                    InputKind.Press,
                    0,
                    InputFlags.None,
                    DeviceButtonState.Button1),
                SequenceGap: true,
                MissingCount: 1,
                ShouldDispatch: false,
                WaitingForSequenceGap: false,
                WaitingForRelease: true,
                Buttons: DeviceButtonState.Button1));

            form.AddInput(new InputObservation(
                new InputEvent(
                    0x03,
                    DeviceControl.Encoder,
                    InputKind.Rotate,
                    1,
                    InputFlags.QueueOverflow,
                    DeviceButtonState.None),
                SequenceGap: false,
                MissingCount: 0,
                ShouldDispatch: false,
                WaitingForSequenceGap: true,
                WaitingForRelease: false,
                Buttons: DeviceButtonState.None));

            Assert(
                form.Controls.Find("_watchdogTestButton", true).Length == 1,
                "The watchdog recovery command is missing from the hardware-test window.");
        }

        service.DisposeAsync().AsTask().GetAwaiter().GetResult();
        service.DisposeAsync().AsTask().GetAwaiter().GetResult();
        Assert(
            service.Status.State == ServiceConnectionState.Stopped,
            "Companion shutdown did not reach the stopped state.");

        Console.WriteLine(
            "CodexKeyboard companion check passed: input recovery remains non-throwing, watchdog recovery is exposed, and shutdown is idempotent.");
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }
}
