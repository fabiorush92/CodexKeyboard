using CodexKeyboard.Device;
using CodexKeyboard.Protocol;
using System.Threading.Channels;

namespace CodexKeyboard.Device.Check;

internal static class Program
{
    private static async Task Main()
    {
        await CheckInterleavedInputAsync();
        await CheckBootloaderAckReadTerminationAsync();
        CheckTransitionMarkerSuppression();
        await CheckTransitionLeaseAsync();
        CheckWrongTerminalResponse();
        await CheckPendingFailureObservationAsync();
        CheckInputSequenceTracking();
        CheckCleanupContinuation();
        Console.WriteLine(
            "CodexKeyboard device check passed: report routing, bootloader ACK handoff, observed pending failures, input resynchronization, and cleanup continuation.");
    }

    private static async Task CheckInterleavedInputAsync()
    {
        var inputs = Channel.CreateUnbounded<InputEvent>();
        var router = new DeviceReportRouter(inputs.Writer);
        var pending = router.BeginCommand(0x07, MessageType.Ping, MessageType.Ack);

        Assert(!router.Route(Hex("01 01 81 FE 05 03 01 00 00 00 00 00 00 00 00 00")),
            "An input report requested read-pump termination.");
        Assert(!pending.Task.IsCompleted, "An input report consumed the pending terminal response.");
        Assert(inputs.Reader.TryRead(out var input), "The interleaved input report was not delivered.");
        Assert(input.Control == Control.Encoder && input.Value == 1, "The routed input report changed.");

        var ack = Hex("01 01 83 07 04 00 00 00 00 00 00 00 00 00 00 00");
        Assert(!router.Route(ack), "A regular ACK requested read-pump termination.");
        Assert(!router.Cancel(pending, new TimeoutException("Expected test race.")),
            "A timeout canceled a terminal response that the router had already accepted.");
        Assert((await pending.Task).SequenceEqual(ack), "The matching ACK did not complete the command.");
    }

    private static async Task CheckBootloaderAckReadTerminationAsync()
    {
        var inputs = Channel.CreateUnbounded<InputEvent>();
        var router = new DeviceReportRouter(inputs.Writer);
        var pending = router.BeginCommand(0x2A, MessageType.EnterBootloader, MessageType.Ack);
        var ack = Hex("01 01 83 2A 05 00 00 00 00 00 00 00 00 00 00 00");

        Assert(router.Route(ack),
            "An accepted ENTER_BOOTLOADER ACK did not stop the read pump before ROM reset.");
        Assert((await pending.Task).SequenceEqual(ack),
            "The ENTER_BOOTLOADER ACK did not complete its pending command.");

        var errorPending = router.BeginCommand(0x2B, MessageType.EnterBootloader, MessageType.Ack);
        var error = Hex("01 01 84 2B 05 04 04 00 00 00 00 00 00 00 00 00");
        Assert(!router.Route(error),
            "A rejected ENTER_BOOTLOADER command stopped the read pump before an ACK.");
        Assert((await errorPending.Task).SequenceEqual(error),
            "The ENTER_BOOTLOADER error did not complete its pending command.");
    }

    private static async Task CheckTransitionLeaseAsync()
    {
        using var lease = CodexKeyboardTransitionVerificationLease.Acquire();
        await using var device = await CodexKeyboardDevice.TryOpenAsync();
        Assert(device is null, "Normal runtime discovery ignored the transition verification lease.");
    }

    private static void CheckTransitionMarkerSuppression()
    {
        var markerPath = Path.Combine(
            Path.GetTempPath(),
            $"CodexKeyboard.Device.Check-{Guid.NewGuid():N}.transition");
        var now = new DateTimeOffset(2026, 7, 22, 12, 0, 0, TimeSpan.Zero);

        try
        {
            Assert(!CodexKeyboardDevice.IsTransitionAuthorizationActive(markerPath, now),
                "A missing transition marker suppressed runtime discovery.");

            File.WriteAllText(markerPath, "device-check");
            File.SetLastWriteTimeUtc(markerPath, (now - TimeSpan.FromSeconds(1)).UtcDateTime);
            Assert(CodexKeyboardDevice.IsTransitionAuthorizationActive(markerPath, now),
                "An active transition marker did not suppress runtime discovery.");

            File.SetLastWriteTimeUtc(
                markerPath,
                (now - CodexKeyboardIdentity.TransitionAuthorizationLifetime - TimeSpan.FromSeconds(1)).UtcDateTime);
            Assert(!CodexKeyboardDevice.IsTransitionAuthorizationActive(markerPath, now),
                "A stale transition marker still suppressed runtime discovery.");
        }
        finally
        {
            File.Delete(markerPath);
        }
    }

    private static void CheckWrongTerminalResponse()
    {
        var inputs = Channel.CreateUnbounded<InputEvent>();
        var router = new DeviceReportRouter(inputs.Writer);
        var pending = router.BeginCommand(0x08, MessageType.SetScene, MessageType.Ack);
        try
        {
            router.Route(Hex("01 01 83 08 04 00 00 00 00 00 00 00 00 00 00 00"));
            throw new InvalidOperationException("A wrong ACK command was accepted.");
        }
        catch (InvalidDataException)
        {
            router.Fail(new InvalidDataException("Expected test failure."));
            _ = pending.Task.Exception;
        }
    }

    private static async Task CheckPendingFailureObservationAsync()
    {
        var router = new DeviceReportRouter(Channel.CreateUnbounded<InputEvent>().Writer);
        var canceled = router.BeginCommand(0x31, MessageType.Ping, MessageType.Ack);
        var cancelError = new TimeoutException("Expected canceled pending command.");
        Assert(router.Cancel(canceled, cancelError), "The pending command was not canceled.");
        await AssertPendingFailureAsync(canceled.Task, cancelError);

        var failed = router.BeginCommand(0x32, MessageType.Ping, MessageType.Ack);
        var failure = new IOException("Expected failed pending command.");
        router.Fail(failure);
        await AssertPendingFailureAsync(failed.Task, failure);
    }

    private static async Task AssertPendingFailureAsync(
        Task<byte[]> task,
        Exception expected)
    {
        try
        {
            await task;
            throw new InvalidOperationException("A faulted pending command completed successfully.");
        }
        catch (Exception exception) when (ReferenceEquals(exception, expected))
        {
        }
    }

    private static void CheckInputSequenceTracking()
    {
        var tracker = new InputSequenceTracker();
        Assert(tracker.Observe(Rotate(0xFE)).ShouldDispatch, "The first event did not establish a baseline.");
        Assert(tracker.Observe(Rotate(0xFF)).ShouldDispatch, "A consecutive event was suppressed.");
        var wrapped = tracker.Observe(Rotate(0x00));
        Assert(!wrapped.SequenceGap && wrapped.ShouldDispatch, "Sequence wraparound was treated as a gap.");

        var gap = tracker.Observe(new InputEvent(
            0x02,
            Control.Button1,
            InputKind.Press,
            0,
            InputFlags.None,
            ButtonState.Button1));
        Assert(gap.SequenceGap && gap.MissingCount == 1 && !gap.ShouldDispatch,
            "A sequence gap did not suppress the uncertain event.");
        Assert(tracker.Buttons == ButtonState.Button1 && tracker.WaitingForRelease,
            "A sequence gap did not adopt the supplied button mask.");

        var release = tracker.Observe(new InputEvent(
            0x03,
            Control.Button1,
            InputKind.Release,
            0,
            InputFlags.None,
            ButtonState.None));
        Assert(!release.ShouldDispatch && !tracker.WaitingForRelease,
            "The neutral recovery event was dispatched or failed to end resynchronization.");
        Assert(tracker.Observe(Rotate(0x04)).ShouldDispatch,
            "Input did not resume after all buttons were released.");

        var overflow = tracker.Observe(new InputEvent(
            0x05,
            Control.Encoder,
            InputKind.Rotate,
            1,
            InputFlags.QueueOverflow,
            ButtonState.Button2));
        Assert(overflow.Overflow && !overflow.ShouldDispatch && overflow.WaitingForSequenceGap &&
               tracker.Buttons == ButtonState.Button2,
            "Queue overflow did not resynchronize from the current button mask.");

        tracker.Reset();
        Assert(tracker.Observe(Rotate(0x00)).ShouldDispatch, "The overflow regression baseline failed.");
        var neutralOverflow = tracker.Observe(new InputEvent(
            0x01,
            Control.Button1,
            InputKind.Press,
            0,
            InputFlags.QueueOverflow,
            ButtonState.None));
        Assert(neutralOverflow.WaitingForSequenceGap && tracker.Buttons == ButtonState.None,
            "A neutral overflow did not begin stale-queue draining.");

        var stale = tracker.Observe(new InputEvent(
            0x02,
            Control.Button2,
            InputKind.Press,
            0,
            InputFlags.None,
            ButtonState.Button2));
        Assert(!stale.ShouldDispatch && stale.WaitingForSequenceGap && tracker.Buttons == ButtonState.None,
            "A buffered pre-overflow event escaped or replaced the authoritative button mask.");

        var postOverflowGap = tracker.Observe(Rotate(0x04));
        Assert(postOverflowGap.SequenceGap && !postOverflowGap.ShouldDispatch &&
               !postOverflowGap.WaitingForSequenceGap,
            "The first post-overflow gap did not complete stale-queue draining.");
        Assert(tracker.Observe(Rotate(0x05)).ShouldDispatch,
            "Input did not resume after the post-overflow gap.");
    }

    private static void CheckCleanupContinuation()
    {
        var actionsRun = 0;
        var originalError = new InvalidDataException("Expected device failure.");
        var cleanupError = new IOException("Expected cleanup failure.");
        var error = CodexKeyboardDevice.RunCleanupActions(
            originalError,
            () =>
            {
                actionsRun++;
                throw cleanupError;
            },
            () => actionsRun++);

        Assert(actionsRun == 2, "A cleanup failure skipped a later cleanup action.");
        Assert(error is AggregateException aggregate &&
               aggregate.InnerExceptions.SequenceEqual([originalError, cleanupError]),
            "The device and cleanup failures were not both preserved.");
    }

    private static InputEvent Rotate(byte sequence) =>
        new(sequence, Control.Encoder, InputKind.Rotate, 1, InputFlags.None, ButtonState.None);

    private static byte[] Hex(string value) => Convert.FromHexString(value.Replace(" ", string.Empty));

    private static void Assert(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }
}
