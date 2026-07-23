using CodexKeyboard.Device;
using CodexKeyboard.Protocol;
using System.Diagnostics;

namespace CodexKeyboard.Companion;

internal enum ServiceConnectionState
{
    Stopped,
    Connecting,
    Disconnected,
    Connected,
}

internal enum ServiceMode
{
    Companion,
    HardwareTest,
}

internal enum DiagnosticLevel
{
    Information,
    Warning,
    Error,
}

internal readonly record struct ServiceStatus(
    ServiceConnectionState State,
    ServiceMode Mode,
    DeviceInfo? DeviceInfo,
    string Detail,
    long Revision);

internal readonly record struct InputObservation(
    InputEvent Event,
    bool SequenceGap,
    byte MissingCount,
    bool ShouldDispatch,
    bool WaitingForSequenceGap,
    bool WaitingForRelease,
    CodexKeyboard.Protocol.ButtonState Buttons);

internal readonly record struct DiagnosticEntry(
    DateTimeOffset Timestamp,
    DiagnosticLevel Level,
    string Message);

internal sealed class KeyboardService : IAsyncDisposable
{
    private const int MaximumDiagnostics = 500;
    private const byte CompanionConnectedBrightness = 8;
    private static readonly TimeSpan ReconnectDelay = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan WatchdogObservationMargin = TimeSpan.FromMilliseconds(750);

    private readonly object _sync = new();
    private readonly object _inputSync = new();
    private readonly CancellationTokenSource _lifetime = new();
    private readonly SemaphoreSlim _operationGate = new(1, 1);
    private readonly SemaphoreSlim _wakeSignal = new(0, 1);
    private readonly TaskCompletionSource _disposeCompletion =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly InputSequenceTracker _inputTracker = new();
    private readonly List<DiagnosticEntry> _diagnostics = [];
    private Task _runTask = Task.CompletedTask;
    private CodexKeyboardDevice? _device;
    private ServiceStatus _status = new(
        ServiceConnectionState.Stopped,
        ServiceMode.Companion,
        null,
        "Not started",
        0);
    private string? _lastConnectionFailure;
    private long _statusRevision;
    private bool _started;
    private bool _disposed;

    public event Action<ServiceStatus>? StatusChanged;

    public event Action<InputObservation>? InputReceived;

    public event Action<DiagnosticEntry>? DiagnosticAdded;

    public ServiceStatus Status
    {
        get
        {
            lock (_sync)
            {
                return _status;
            }
        }
    }

    public IReadOnlyList<DiagnosticEntry> Diagnostics
    {
        get
        {
            lock (_sync)
            {
                return _diagnostics.ToArray();
            }
        }
    }

    public void Start()
    {
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_started)
            {
                return;
            }

            _started = true;
            _runTask = Task.Run(() => RunAsync(_lifetime.Token));
        }
    }

    public void Reconnect()
    {
        lock (_sync)
        {
            if (_disposed)
            {
                return;
            }
        }

        AddDiagnostic(DiagnosticLevel.Information, "A device reconnect was requested.");
        WakeConnectionLoop();
    }

    public async Task SetTestModeAsync(bool enabled)
    {
        await _operationGate.WaitAsync(_lifetime.Token).ConfigureAwait(false);
        try
        {
            var requestedMode = enabled ? ServiceMode.HardwareTest : ServiceMode.Companion;
            ServiceStatus status;
            lock (_sync)
            {
                if (_status.Mode == requestedMode)
                {
                    return;
                }

                _status = _status with
                {
                    Mode = requestedMode,
                    Revision = ++_statusRevision,
                };
                status = _status;
            }

            lock (_inputSync)
            {
                _inputTracker.Reset();
            }

            PublishStatus(status);
            AddDiagnostic(
                DiagnosticLevel.Information,
                enabled ? "Hardware test mode enabled." : "Companion mode restored.");

            if (!enabled && TryGetDevice() is { } device)
            {
                await device.SetSceneAsync(
                    Scene.CompanionConnected,
                    Effect.Solid,
                    CompanionConnectedBrightness,
                    0,
                    _lifetime.Token).ConfigureAwait(false);
                AddDiagnostic(
                    DiagnosticLevel.Information,
                    "The companion-connected LED scene was restored.");
            }
        }
        finally
        {
            _operationGate.Release();
        }
    }

    public async Task RefreshInfoAsync()
    {
        await ExecuteCommandAsync(
            "GET_INFO",
            async (device, cancellationToken) =>
            {
                var info = await device.GetInfoAsync(cancellationToken).ConfigureAwait(false);
                UpdateConnectedStatus(info, "Exact runtime identity and DEVICE_INFO verified");
            }).ConfigureAwait(false);
    }

    public Task PingAsync() => ExecuteCommandAsync(
        "PING",
        (device, cancellationToken) => device.PingAsync(cancellationToken));

    public Task SetSceneAsync(Scene scene, Effect effect, byte brightness, ushort period10Ms) =>
        ExecuteCommandAsync(
            $"SET_SCENE {scene}/{effect}, brightness {brightness}, period {period10Ms * 10} ms",
            (device, cancellationToken) =>
                device.SetSceneAsync(scene, effect, brightness, period10Ms, cancellationToken));

    public Task SetRgbAsync(byte[] rgb)
    {
        ArgumentNullException.ThrowIfNull(rgb);
        var copy = rgb.ToArray();
        return ExecuteCommandAsync(
            $"SET_RGB {Convert.ToHexString(copy)}",
            (device, cancellationToken) => device.SetRgbAsync(copy, cancellationToken));
    }

    public async Task RunWatchdogTestAsync()
    {
        await _operationGate.WaitAsync(_lifetime.Token).ConfigureAwait(false);
        try
        {
            var device = GetRequiredDevice();
            var quietInterval = TimeSpan.FromMilliseconds(HidProtocol.HeartbeatTimeoutMs) +
                                WatchdogObservationMargin;
            AddDiagnostic(
                DiagnosticLevel.Information,
                $"Heartbeat watchdog test started; all host commands are paused for {quietInterval.TotalSeconds:0.00} seconds.");

            await Task.Delay(quietInterval, _lifetime.Token).ConfigureAwait(false);

            var stopwatch = Stopwatch.StartNew();
            var info = await device.GetInfoAsync(_lifetime.Token).ConfigureAwait(false);
            stopwatch.Stop();
            lock (_inputSync)
            {
                _inputTracker.Reset();
            }
            UpdateConnectedStatus(info, "Watchdog recovery verified by GET_INFO");
            AddDiagnostic(
                DiagnosticLevel.Information,
                $"Watchdog interval elapsed and GET_INFO restored the session in {stopwatch.ElapsedMilliseconds} ms. " +
                "Confirm that the companion-absent LED scene appeared before recovery.");
        }
        catch (Exception exception)
        {
            AddDiagnostic(DiagnosticLevel.Error, $"Heartbeat watchdog test failed: {exception.Message}");
            throw;
        }
        finally
        {
            _operationGate.Release();
        }
    }

    public async Task EnterBootloaderAsync()
    {
        await _operationGate.WaitAsync(_lifetime.Token).ConfigureAwait(false);
        try
        {
            var device = GetRequiredDevice();
            AddDiagnostic(
                DiagnosticLevel.Warning,
                "Guarded ENTER_BOOTLOADER requested; the runtime HID device will disconnect.");
            await device.EnterBootloaderAsync(_lifetime.Token).ConfigureAwait(false);
            SetStatus(
                ServiceConnectionState.Disconnected,
                null,
                "Runtime entered the CH552 ROM bootloader; waiting for its return");
        }
        catch (Exception exception)
        {
            AddDiagnostic(DiagnosticLevel.Error, $"ENTER_BOOTLOADER failed: {exception.Message}");
            throw;
        }
        finally
        {
            _operationGate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        Task runTask;
        bool ownsDispose;
        lock (_sync)
        {
            ownsDispose = !_disposed;
            if (ownsDispose)
            {
                _disposed = true;
                runTask = _runTask;
            }
            else
            {
                runTask = Task.CompletedTask;
            }
        }

        if (!ownsDispose)
        {
            await _disposeCompletion.Task.ConfigureAwait(false);
            return;
        }

        Exception? disposeError = RunCleanupActions(
            null,
            _lifetime.Cancel,
            WakeConnectionLoop);
        try
        {
            await runTask.ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            disposeError = CombineErrors(disposeError, exception);
        }
        finally
        {
            disposeError = RunCleanupActions(
                disposeError,
                () => SetStatus(ServiceConnectionState.Stopped, null, "Stopped"),
                _operationGate.Dispose,
                _wakeSignal.Dispose,
                _lifetime.Dispose);
            if (disposeError is null)
            {
                _disposeCompletion.TrySetResult();
            }
            else
            {
                _disposeCompletion.TrySetException(disposeError);
            }
        }
        await _disposeCompletion.Task.ConfigureAwait(false);
    }

    private async Task RunAsync(CancellationToken cancellationToken)
    {
        AddDiagnostic(DiagnosticLevel.Information, "CodexKeyboard companion started.");
        while (!cancellationToken.IsCancellationRequested)
        {
            SetStatus(ServiceConnectionState.Connecting, null, "Searching for the exact runtime HID device");

            CodexKeyboardDevice? device = null;
            try
            {
                device = await CodexKeyboardDevice.TryOpenAsync(cancellationToken).ConfigureAwait(false);
                if (device is null)
                {
                    const string detail = "Exact CodexKeyboard runtime not found";
                    SetStatus(ServiceConnectionState.Disconnected, null, detail);
                    ReportConnectionFailureOnce(detail);
                    await WaitForWakeOrDelayAsync(ReconnectDelay, cancellationToken).ConfigureAwait(false);
                    continue;
                }

                lock (_sync)
                {
                    _device = device;
                    _lastConnectionFailure = null;
                }
                lock (_inputSync)
                {
                    _inputTracker.Reset();
                }

                UpdateConnectedStatus(device.Info, "Exact runtime identity and DEVICE_INFO verified");
                AddDiagnostic(
                    DiagnosticLevel.Information,
                    $"Connected to firmware {device.Info.FirmwareMajor}.{device.Info.FirmwareMinor}.{device.Info.FirmwarePatch}.");

                await RunConnectedSessionAsync(device, cancellationToken).ConfigureAwait(false);
                SetDisconnectedIfStillConnected("Runtime session closed; reconnecting");
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                SetStatus(ServiceConnectionState.Disconnected, null, exception.Message);
                ReportConnectionFailureOnce(exception.Message);
            }
            finally
            {
                if (device is not null)
                {
                    try
                    {
                        await DetachAndDisposeAsync(device).ConfigureAwait(false);
                    }
                    catch (Exception exception)
                    {
                        var detail = $"Runtime cleanup failed; reconnect stopped: {exception.Message}";
                        SetStatus(ServiceConnectionState.Disconnected, null, detail);
                        AddDiagnostic(DiagnosticLevel.Error, detail);
                        throw;
                    }
                }
            }

            if (!cancellationToken.IsCancellationRequested)
            {
                await WaitForWakeOrDelayAsync(ReconnectDelay, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private async Task RunConnectedSessionAsync(
        CodexKeyboardDevice device,
        CancellationToken cancellationToken)
    {
        using var session = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var inputTask = ReadInputsAsync(device, session.Token);
        var heartbeatTask = SendHeartbeatsAsync(device, session.Token);
        var reconnectTask = _wakeSignal.WaitAsync(session.Token);
        Exception? failure = null;

        var completed = await Task.WhenAny(device.Completion, inputTask, heartbeatTask, reconnectTask)
            .ConfigureAwait(false);
        if (completed != reconnectTask)
        {
            try
            {
                await completed.ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                failure = exception;
            }
        }

        session.Cancel();
        await ObserveCanceledTaskAsync(inputTask).ConfigureAwait(false);
        await ObserveCanceledTaskAsync(heartbeatTask).ConfigureAwait(false);
        await ObserveCanceledTaskAsync(reconnectTask).ConfigureAwait(false);
        if (device.Completion.IsCompleted)
        {
            try
            {
                await device.Completion.ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                failure ??= exception;
            }
        }

        if (failure is not null)
        {
            throw new IOException("The runtime HID session failed.", failure);
        }
    }

    private async Task ReadInputsAsync(
        CodexKeyboardDevice device,
        CancellationToken cancellationToken)
    {
        await foreach (var input in device.Inputs.ReadAllAsync(cancellationToken).ConfigureAwait(false))
        {
            InputSequenceUpdate update;
            lock (_inputSync)
            {
                update = _inputTracker.Observe(input);
            }

            if (update.SequenceGap || update.Overflow)
            {
                var reason = update.Overflow
                    ? "the firmware input queue overflowed"
                    : $"{update.MissingCount} input report(s) were missing";
                var recovery = update.WaitingForSequenceGap
                    ? "Buffered historical reports remain suppressed until their sequence gap arrives."
                    : update.WaitingForRelease
                        ? "Actions remain suppressed until all buttons are released."
                        : "The supplied button mask is now authoritative.";
                AddDiagnostic(
                    DiagnosticLevel.Warning,
                    $"Input sequence 0x{input.Sequence:X2} was resynchronized because {reason}. {recovery}");
            }

            InputReceived?.Invoke(new InputObservation(
                update.Input,
                update.SequenceGap,
                update.MissingCount,
                update.ShouldDispatch,
                update.WaitingForSequenceGap,
                update.WaitingForRelease,
                update.Buttons));
        }
    }

    private async Task SendHeartbeatsAsync(
        CodexKeyboardDevice device,
        CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(HidProtocol.PingIntervalMs));
        while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
        {
            await _operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                if (!ReferenceEquals(device, TryGetDevice()))
                {
                    return;
                }
                await device.PingAsync(cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                _operationGate.Release();
            }
        }
    }

    private async Task ExecuteCommandAsync(
        string description,
        Func<CodexKeyboardDevice, CancellationToken, Task> command)
    {
        await _operationGate.WaitAsync(_lifetime.Token).ConfigureAwait(false);
        var stopwatch = Stopwatch.StartNew();
        try
        {
            await command(GetRequiredDevice(), _lifetime.Token).ConfigureAwait(false);
            stopwatch.Stop();
            AddDiagnostic(
                DiagnosticLevel.Information,
                $"{description} acknowledged in {stopwatch.ElapsedMilliseconds} ms.");
        }
        catch (Exception exception)
        {
            AddDiagnostic(DiagnosticLevel.Error, $"{description} failed: {exception.Message}");
            throw;
        }
        finally
        {
            _operationGate.Release();
        }
    }

    private async Task DetachAndDisposeAsync(CodexKeyboardDevice device)
    {
        await _operationGate.WaitAsync().ConfigureAwait(false);
        try
        {
            lock (_sync)
            {
                if (ReferenceEquals(_device, device))
                {
                    _device = null;
                }
            }
            await device.DisposeAsync().ConfigureAwait(false);
        }
        finally
        {
            _operationGate.Release();
        }
    }

    private CodexKeyboardDevice GetRequiredDevice() => TryGetDevice() ??
        throw new InvalidOperationException("The verified CodexKeyboard runtime is not connected.");

    private CodexKeyboardDevice? TryGetDevice()
    {
        lock (_sync)
        {
            return _device;
        }
    }

    private void UpdateConnectedStatus(DeviceInfo info, string detail) =>
        SetStatus(ServiceConnectionState.Connected, info, detail);

    private void SetDisconnectedIfStillConnected(string detail)
    {
        ServiceStatus? status = null;
        lock (_sync)
        {
            if (_status.State == ServiceConnectionState.Connected)
            {
                _status = new ServiceStatus(
                    ServiceConnectionState.Disconnected,
                    _status.Mode,
                    null,
                    detail,
                    ++_statusRevision);
                status = _status;
            }
        }
        if (status is { } disconnected)
        {
            PublishStatus(disconnected);
        }
    }

    private void SetStatus(
        ServiceConnectionState state,
        DeviceInfo? deviceInfo,
        string detail)
    {
        ServiceStatus status;
        lock (_sync)
        {
            _status = new ServiceStatus(
                state,
                _status.Mode,
                deviceInfo,
                detail,
                ++_statusRevision);
            status = _status;
        }
        PublishStatus(status);
    }

    private void PublishStatus(ServiceStatus status) => StatusChanged?.Invoke(status);

    private void AddDiagnostic(DiagnosticLevel level, string message)
    {
        var entry = new DiagnosticEntry(DateTimeOffset.Now, level, message);
        lock (_sync)
        {
            _diagnostics.Add(entry);
            if (_diagnostics.Count > MaximumDiagnostics)
            {
                _diagnostics.RemoveAt(0);
            }
        }
        DiagnosticAdded?.Invoke(entry);
    }

    private void ReportConnectionFailureOnce(string detail)
    {
        lock (_sync)
        {
            if (string.Equals(_lastConnectionFailure, detail, StringComparison.Ordinal))
            {
                return;
            }
            _lastConnectionFailure = detail;
        }
        AddDiagnostic(DiagnosticLevel.Warning, detail);
    }

    private void WakeConnectionLoop()
    {
        try
        {
            _wakeSignal.Release();
        }
        catch (SemaphoreFullException)
        {
        }
        catch (ObjectDisposedException)
        {
        }
    }

    private async Task WaitForWakeOrDelayAsync(
        TimeSpan delay,
        CancellationToken cancellationToken)
    {
        using var wait = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var signalTask = _wakeSignal.WaitAsync(wait.Token);
        var delayTask = Task.Delay(delay, wait.Token);
        await Task.WhenAny(signalTask, delayTask).ConfigureAwait(false);
        wait.Cancel();
        await ObserveCanceledTaskAsync(signalTask).ConfigureAwait(false);
        await ObserveCanceledTaskAsync(delayTask).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
    }

    private static async Task ObserveCanceledTaskAsync(Task task)
    {
        try
        {
            await task.ConfigureAwait(false);
        }
        catch
        {
        }
    }

    private static Exception? RunCleanupActions(Exception? error, params Action[] actions)
    {
        foreach (var action in actions)
        {
            try
            {
                action();
            }
            catch (Exception exception)
            {
                error = CombineErrors(error, exception);
            }
        }
        return error;
    }

    private static Exception CombineErrors(Exception? error, Exception next) =>
        error is null ? next : new AggregateException(error, next);
}
