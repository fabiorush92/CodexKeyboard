using CodexKeyboard.Protocol;
using System.Threading.Channels;

namespace CodexKeyboard.Device;

public sealed class CodexKeyboardCommandException(CommandError error)
    : Exception($"Device error {error.Error} for message 0x{error.FailedMessageType:X2}, detail {error.Detail}.")
{
    public CommandError Error { get; } = error;
}

public sealed class CodexKeyboardDeviceBusyException()
    : InvalidOperationException("The exact CodexKeyboard runtime is already owned by another process.")
{
}

public sealed class CodexKeyboardDevice : IAsyncDisposable
{
    private readonly FileStream _readStream;
    private readonly FileStream _writeStream;
    private readonly NamedSemaphoreLease _ownership;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly SemaphoreSlim _commandGate = new(1, 1);
    private readonly Channel<InputEvent> _inputChannel = Channel.CreateUnbounded<InputEvent>(
        new UnboundedChannelOptions
        {
            SingleReader = false,
            SingleWriter = true,
            AllowSynchronousContinuations = false,
        });
    private readonly TaskCompletionSource _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _disposeCompletion =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly DeviceReportRouter _router;
    private readonly Task _readTask;
    private byte _nextCommandSequence;
    private int _bootloaderRequest;
    private int _bootloaderTransition;
    private int _stopped;
    private int _disposeStarted;

    private CodexKeyboardDevice(
        FileStream readStream,
        FileStream writeStream,
        NamedSemaphoreLease ownership)
    {
        _readStream = readStream;
        _writeStream = writeStream;
        _ownership = ownership;
        _router = new DeviceReportRouter(_inputChannel.Writer);
        _readTask = ReadLoopAsync();
    }

    public DeviceInfo Info { get; private set; }

    public ChannelReader<InputEvent> Inputs => _inputChannel.Reader;

    public Task Completion => _completion.Task;

    public static async Task<CodexKeyboardDevice?> TryOpenAsync(
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var transitionGate = NamedSemaphoreLease.TryAcquire(
            CodexKeyboardIdentity.TransitionVerificationName);
        if (transitionGate is null || IsTransitionAuthorizationActive())
        {
            return null;
        }
        return await TryOpenCoreAsync(cancellationToken).ConfigureAwait(false);
    }

    internal static Task<CodexKeyboardDevice?> TryOpenForTransitionVerificationAsync(
        CancellationToken cancellationToken = default) =>
        TryOpenCoreAsync(cancellationToken);

    private static async Task<CodexKeyboardDevice?> TryOpenCoreAsync(
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        NamedSemaphoreLease? ownership = NamedSemaphoreLease.TryAcquire(
            CodexKeyboardIdentity.OwnershipName);
        if (ownership is null)
        {
            throw new CodexKeyboardDeviceBusyException();
        }

        try
        {
            var handles = WindowsHid.OpenExactRuntime();
            if (handles is null)
            {
                return null;
            }

            FileStream? readStream = null;
            FileStream? writeStream = null;
            try
            {
                readStream = new FileStream(
                    handles.Read, FileAccess.Read, HidProtocol.ReportLength, true);
                writeStream = new FileStream(
                    handles.Write, FileAccess.Write, HidProtocol.ReportLength, true);
            }
            catch
            {
                readStream?.Dispose();
                writeStream?.Dispose();
                handles.Read.Dispose();
                handles.Write.Dispose();
                throw;
            }

            var device = new CodexKeyboardDevice(readStream, writeStream, ownership);
            ownership = null;
            try
            {
                device.Info = await device.GetInfoCoreAsync(cancellationToken).ConfigureAwait(false);
                return device;
            }
            catch
            {
                await device.DisposeAsync().ConfigureAwait(false);
                throw;
            }
        }
        finally
        {
            ownership?.Dispose();
        }
    }

    private static bool IsTransitionAuthorizationActive() =>
        IsTransitionAuthorizationActive(
            CodexKeyboardIdentity.TransitionMarkerPath,
            DateTimeOffset.UtcNow);

    internal static bool IsTransitionAuthorizationActive(
        string markerPath,
        DateTimeOffset now)
    {
        try
        {
            if (!File.Exists(markerPath))
            {
                return false;
            }
            var age = now - File.GetLastWriteTimeUtc(markerPath);
            return age < TimeSpan.Zero || age <= CodexKeyboardIdentity.TransitionAuthorizationLifetime;
        }
        catch (FileNotFoundException)
        {
            return false;
        }
    }

    public async Task<DeviceInfo> GetInfoAsync(CancellationToken cancellationToken = default)
    {
        var info = await GetInfoCoreAsync(cancellationToken).ConfigureAwait(false);
        Info = info;
        return info;
    }

    public Task PingAsync(CancellationToken cancellationToken = default) =>
        SendAckAsync(MessageType.Ping, HidProtocol.CreatePing, false, cancellationToken);

    public Task SetSceneAsync(
        Scene scene,
        Effect effect,
        byte brightness,
        ushort period10Ms,
        CancellationToken cancellationToken = default)
    {
        _ = HidProtocol.CreateSetScene(0, scene, effect, brightness, period10Ms, Info.MaximumBrightness);
        return SendAckAsync(
            MessageType.SetScene,
            sequence => HidProtocol.CreateSetScene(
                sequence, scene, effect, brightness, period10Ms, Info.MaximumBrightness),
            false,
            cancellationToken);
    }

    public Task SetRgbAsync(
        ReadOnlyMemory<byte> rgb,
        CancellationToken cancellationToken = default)
    {
        var components = rgb.ToArray();
        _ = HidProtocol.CreateSetRgb(0, components, Info.MaximumBrightness);
        return SendAckAsync(
            MessageType.SetRgb,
            sequence => HidProtocol.CreateSetRgb(sequence, components, Info.MaximumBrightness),
            false,
            cancellationToken);
    }

    public async Task EnterBootloaderAsync(CancellationToken cancellationToken = default)
    {
        if (Interlocked.CompareExchange(ref _bootloaderRequest, 1, 0) != 0)
        {
            throw new InvalidOperationException("A bootloader transition is already in progress.");
        }

        try
        {
            await SendAckAsync(
                MessageType.EnterBootloader,
                HidProtocol.CreateEnterBootloader,
                true,
                cancellationToken).ConfigureAwait(false);
            Stop(null);
        }
        catch (Exception exception)
        {
            if (Volatile.Read(ref _bootloaderTransition) != 0)
            {
                Stop(exception);
            }
            else
            {
                Interlocked.Exchange(ref _bootloaderRequest, 0);
            }
            throw;
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposeStarted, 1) != 0)
        {
            await _disposeCompletion.Task.ConfigureAwait(false);
            return;
        }

        try
        {
            Stop(null);
            try
            {
                await _readTask.ConfigureAwait(false);
            }
            catch
            {
            }
            if (_completion.Task.IsFaulted)
            {
                _ = _completion.Task.Exception;
            }
        }
        finally
        {
            _disposeCompletion.TrySetResult();
        }
    }

    private async Task<DeviceInfo> GetInfoCoreAsync(CancellationToken cancellationToken)
    {
        var report = await SendCommandAsync(
            MessageType.GetInfo,
            MessageType.DeviceInfo,
            HidProtocol.CreateGetInfo,
            false,
            cancellationToken).ConfigureAwait(false);
        var info = HidProtocol.DecodeDeviceInfo(report);
        try
        {
            CodexKeyboardIdentity.Validate(info);
        }
        catch (Exception exception)
        {
            Stop(exception);
            throw;
        }
        return info;
    }

    private async Task SendAckAsync(
        MessageType commandType,
        Func<byte, byte[]> createCommand,
        bool allowBootloaderTransition,
        CancellationToken cancellationToken)
    {
        var report = await SendCommandAsync(
            commandType,
            MessageType.Ack,
            createCommand,
            allowBootloaderTransition,
            cancellationToken).ConfigureAwait(false);
        var ack = HidProtocol.DecodeAck(report);
        if (ack.Command != commandType)
        {
            var exception = new InvalidDataException("The ACK identifies the wrong host command.");
            Stop(exception);
            throw exception;
        }
    }

    private async Task<byte[]> SendCommandAsync(
        MessageType commandType,
        MessageType expectedType,
        Func<byte, byte[]> createCommand,
        bool allowBootloaderTransition,
        CancellationToken cancellationToken)
    {
        ThrowIfUnavailable(allowBootloaderTransition);
        await _commandGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        PendingCommand? pending = null;
        try
        {
            ThrowIfUnavailable(allowBootloaderTransition);
            if (allowBootloaderTransition)
            {
                Volatile.Write(ref _bootloaderTransition, 1);
            }
            var sequence = _nextCommandSequence;
            _nextCommandSequence = HidProtocol.NextSequence(_nextCommandSequence);
            var command = createCommand(sequence);
            pending = _router.BeginCommand(sequence, commandType, expectedType);

            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken, _lifetime.Token);
            deadline.CancelAfter(HidProtocol.CommandTimeoutMs);
            try
            {
                await _writeStream.WriteAsync(command, deadline.Token).ConfigureAwait(false);
                await _writeStream.FlushAsync(deadline.Token).ConfigureAwait(false);
                var response = await pending.Task.WaitAsync(deadline.Token).ConfigureAwait(false);

                var decoded = HidProtocol.Decode(response, ReportDirection.DeviceToHost);
                if (decoded.Type == MessageType.Error)
                {
                    throw new CodexKeyboardCommandException(HidProtocol.DecodeError(response));
                }
                return response;
            }
            catch (CodexKeyboardCommandException)
            {
                throw;
            }
            catch (OperationCanceledException exception) when (!_lifetime.IsCancellationRequested)
            {
                Exception failure = cancellationToken.IsCancellationRequested
                    ? new OperationCanceledException(
                        "The HID command was canceled after dispatch; the session state is ambiguous.",
                        exception,
                        cancellationToken)
                    : new TimeoutException(
                        $"The HID command did not complete within {HidProtocol.CommandTimeoutMs} ms.",
                        exception);
                if (_router.Cancel(pending, failure))
                {
                    pending = null;
                    Stop(failure);
                    throw failure;
                }

                var acceptedResponse = pending.Task.GetAwaiter().GetResult();
                var accepted = HidProtocol.Decode(acceptedResponse, ReportDirection.DeviceToHost);
                if (accepted.Type == MessageType.Error)
                {
                    throw new CodexKeyboardCommandException(HidProtocol.DecodeError(acceptedResponse));
                }
                return acceptedResponse;
            }
            catch (Exception exception)
            {
                Stop(exception);
                throw;
            }
        }
        finally
        {
            if (pending is not null)
            {
                _router.Cancel(pending, new ObjectDisposedException(nameof(CodexKeyboardDevice)));
            }
            _commandGate.Release();
        }
    }

    private async Task ReadLoopAsync()
    {
        try
        {
            while (true)
            {
                var report = await ReadReportAsync(_lifetime.Token).ConfigureAwait(false);
                if (_router.Route(report))
                {
                    return;
                }
            }
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
        }
        catch (ObjectDisposedException) when (Volatile.Read(ref _stopped) != 0)
        {
        }
        catch (Exception exception)
        {
            Stop(exception);
        }
    }

    private async Task<byte[]> ReadReportAsync(CancellationToken cancellationToken)
    {
        var report = new byte[HidProtocol.ReportLength];
        var offset = 0;
        while (offset < report.Length)
        {
            var read = await _readStream.ReadAsync(report.AsMemory(offset), cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                throw new EndOfStreamException("The HID device closed before returning a complete report.");
            }
            offset += read;
        }
        return report;
    }

    private void ThrowIfUnavailable(bool allowBootloaderTransition)
    {
        if (Volatile.Read(ref _stopped) != 0)
        {
            throw new ObjectDisposedException(nameof(CodexKeyboardDevice));
        }
        if (!allowBootloaderTransition &&
            (Volatile.Read(ref _bootloaderRequest) != 0 ||
             Volatile.Read(ref _bootloaderTransition) != 0))
        {
            throw new InvalidOperationException("The device is entering its bootloader.");
        }
    }

    private void Stop(Exception? error)
    {
        if (Interlocked.Exchange(ref _stopped, 1) != 0)
        {
            return;
        }

        var pendingError = error ?? new ObjectDisposedException(nameof(CodexKeyboardDevice));
        _router.Fail(pendingError);
        _inputChannel.Writer.TryComplete(error);
        _lifetime.Cancel();
        _readStream.Dispose();
        _writeStream.Dispose();
        _ownership.Dispose();

        if (error is null)
        {
            _completion.TrySetResult();
        }
        else
        {
            _completion.TrySetException(error);
        }
    }

}

public sealed class CodexKeyboardTransitionVerificationLease : IDisposable
{
    private readonly NamedSemaphoreLease _lease;

    private CodexKeyboardTransitionVerificationLease(NamedSemaphoreLease lease)
    {
        _lease = lease;
    }

    public static CodexKeyboardTransitionVerificationLease Acquire()
    {
        var lease = NamedSemaphoreLease.TryAcquire(CodexKeyboardIdentity.TransitionVerificationName) ??
            throw new InvalidOperationException("Another bootloader transition verification is already active.");
        return new CodexKeyboardTransitionVerificationLease(lease);
    }

    public void Dispose() => _lease.Dispose();
}

internal sealed class NamedSemaphoreLease : IDisposable
{
    private readonly Semaphore _semaphore;
    private int _disposed;

    private NamedSemaphoreLease(Semaphore semaphore)
    {
        _semaphore = semaphore;
    }

    internal static NamedSemaphoreLease? TryAcquire(string name)
    {
        var semaphore = new Semaphore(1, 1, name);
        try
        {
            if (!semaphore.WaitOne(0))
            {
                semaphore.Dispose();
                return null;
            }
            return new NamedSemaphoreLease(semaphore);
        }
        catch
        {
            semaphore.Dispose();
            throw;
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }
        _semaphore.Release();
        _semaphore.Dispose();
    }
}

internal sealed class PendingCommand
{
    internal PendingCommand(byte sequence, MessageType commandType, MessageType expectedType)
    {
        Sequence = sequence;
        CommandType = commandType;
        ExpectedType = expectedType;
    }

    internal byte Sequence { get; }

    internal MessageType CommandType { get; }

    internal MessageType ExpectedType { get; }

    internal TaskCompletionSource<byte[]> Completion { get; } =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    internal Task<byte[]> Task => Completion.Task;
}

internal sealed class DeviceReportRouter
{
    private readonly object _sync = new();
    private readonly ChannelWriter<InputEvent> _inputs;
    private PendingCommand? _pending;

    internal DeviceReportRouter(ChannelWriter<InputEvent> inputs)
    {
        _inputs = inputs;
    }

    internal PendingCommand BeginCommand(
        byte sequence,
        MessageType commandType,
        MessageType expectedType)
    {
        lock (_sync)
        {
            if (_pending is not null)
            {
                throw new InvalidOperationException("Only one HID command may be outstanding.");
            }
            _pending = new PendingCommand(sequence, commandType, expectedType);
            return _pending;
        }
    }

    internal bool Route(byte[] report)
    {
        var decoded = HidProtocol.Decode(report, ReportDirection.DeviceToHost);
        if (decoded.Type == MessageType.InputEvent)
        {
            if (!_inputs.TryWrite(HidProtocol.DecodeInputEvent(report)))
            {
                throw new InvalidOperationException("The input report channel is closed.");
            }
            return false;
        }

        PendingCommand pending;
        bool bootloaderAcknowledged;
        lock (_sync)
        {
            pending = _pending ??
                throw new InvalidDataException("The device returned a terminal response with no pending command.");
            if (decoded.Sequence != pending.Sequence)
            {
                throw new InvalidDataException("A terminal response used an unexpected sequence number.");
            }
            if (decoded.Type == MessageType.Error)
            {
                var error = HidProtocol.DecodeError(report);
                if (error.FailedMessageType != (byte)pending.CommandType)
                {
                    throw new InvalidDataException("The device error identifies the wrong host command.");
                }
            }
            else
            {
                if (decoded.Type != pending.ExpectedType)
                {
                    throw new InvalidDataException(
                        $"Expected {pending.ExpectedType}, received {decoded.Type}.");
                }
                if (decoded.Type == MessageType.Ack &&
                    HidProtocol.DecodeAck(report).Command != pending.CommandType)
                {
                    throw new InvalidDataException("The ACK identifies the wrong host command.");
                }
            }
            bootloaderAcknowledged = decoded.Type == MessageType.Ack &&
                                     pending.CommandType == MessageType.EnterBootloader;
            _pending = null;
            pending.Completion.TrySetResult(report);
        }
        return bootloaderAcknowledged;
    }

    internal bool Cancel(PendingCommand pending, Exception error)
    {
        lock (_sync)
        {
            if (!ReferenceEquals(_pending, pending))
            {
                return false;
            }
            _pending = null;
            pending.Completion.TrySetException(error);
            return true;
        }
    }

    internal void Fail(Exception error)
    {
        PendingCommand? pending;
        lock (_sync)
        {
            pending = _pending;
            _pending = null;
            pending?.Completion.TrySetException(error);
        }
    }
}
