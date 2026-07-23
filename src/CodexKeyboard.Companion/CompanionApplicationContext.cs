using System.Drawing;

namespace CodexKeyboard.Companion;

internal sealed class CompanionApplicationContext : ApplicationContext
{
    private readonly KeyboardService _service = new();
    private readonly Control _dispatcher = new();
    private readonly ToolStripMenuItem _statusItem = new("Starting...") { Enabled = false };
    private readonly ToolStripMenuItem _openTestsItem = new("Open hardware tests");
    private readonly ToolStripMenuItem _reconnectItem = new("Reconnect");
    private readonly ToolStripMenuItem _exitItem = new("Exit");
    private readonly ContextMenuStrip _trayMenu = new();
    private readonly NotifyIcon _trayIcon;
    private readonly SemaphoreSlim _modeGate = new(1, 1);
    private HardwareTestForm? _testForm;
    private long _lastStatusRevision = -1;
    private bool _exiting;

    public CompanionApplicationContext(bool showTestsOnStartup)
    {
        _dispatcher.CreateControl();
        _ = _dispatcher.Handle;

        _trayMenu.Items.AddRange([
            _statusItem,
            new ToolStripSeparator(),
            _openTestsItem,
            _reconnectItem,
            new ToolStripSeparator(),
            _exitItem,
        ]);
        _trayIcon = new NotifyIcon
        {
            ContextMenuStrip = _trayMenu,
            Icon = SystemIcons.Application,
            Text = "CodexKeyboard — starting",
            Visible = true,
        };

        _openTestsItem.Click += async (_, _) => await ShowTestsAsync();
        _reconnectItem.Click += (_, _) => _service.Reconnect();
        _exitItem.Click += async (_, _) => await ExitAsync();
        _trayIcon.DoubleClick += async (_, _) => await ShowTestsAsync();

        _service.StatusChanged += OnStatusChanged;
        _service.InputReceived += OnInputReceived;
        _service.DiagnosticAdded += OnDiagnosticAdded;
        _service.Start();
        OnStatusChanged(_service.Status);

        if (showTestsOnStartup)
        {
            ShowTestsOnStartup();
        }
    }

    private void ShowTestsOnStartup()
    {
        EnsureTestForm();
        _testForm!.PrepareForShow();
        _testForm.Show();
        _ = EnterStartupTestModeAsync();
    }

    private async Task EnterStartupTestModeAsync()
    {
        await _modeGate.WaitAsync();
        try
        {
            if (!_exiting && _testForm?.Visible == true)
            {
                await _service.SetTestModeAsync(true);
            }
        }
        catch (Exception exception)
        {
            ShowOperationError("Could not enter hardware test mode.", exception);
        }
        finally
        {
            _modeGate.Release();
        }
    }

    private async Task ShowTestsAsync()
    {
        await _modeGate.WaitAsync();
        try
        {
            if (_exiting)
            {
                return;
            }

            if (_testForm?.Visible == true)
            {
                _testForm.Activate();
                return;
            }

            EnsureTestForm();
            await _service.SetTestModeAsync(true);
            _testForm!.PrepareForShow();
            _testForm.Show();
            _testForm.Activate();
        }
        catch (Exception exception)
        {
            ShowOperationError("Could not enter hardware test mode.", exception);
        }
        finally
        {
            _modeGate.Release();
        }
    }

    private async void OnHideTestsRequested(object? sender, EventArgs e)
    {
        await _modeGate.WaitAsync();
        try
        {
            if (_exiting || _testForm?.Visible != true)
            {
                return;
            }

            await _service.SetTestModeAsync(false);
        }
        catch (Exception exception)
        {
            ShowOperationError("Could not leave hardware test mode.", exception);
        }
        finally
        {
            if (!_exiting && _testForm?.Visible == true)
            {
                _testForm.HideToTray();
            }
            _modeGate.Release();
        }
    }

    private void EnsureTestForm()
    {
        if (_testForm is not null)
        {
            return;
        }

        _testForm = new HardwareTestForm(_service);
        _testForm.HideRequested += OnHideTestsRequested;
        _testForm.ExitRequested += OnExitRequested;
        _testForm.ApplyStatus(_service.Status);
        foreach (var entry in _service.Diagnostics)
        {
            _testForm.AddDiagnostic(entry);
        }
    }

    private async void OnExitRequested(object? sender, EventArgs e) => await ExitAsync();

    private void OnStatusChanged(ServiceStatus status) => Dispatch(() =>
    {
        if (status.Revision < _lastStatusRevision)
        {
            return;
        }
        _lastStatusRevision = status.Revision;

        var text = $"{status.State} — {status.Detail}";
        _statusItem.Text = text;
        _trayIcon.Text = text.Length <= 63 ? text : text[..63];
        _testForm?.ApplyStatus(status);
    });

    private void OnInputReceived(InputObservation observation) => Dispatch(() =>
    {
        if (_testForm?.Visible == true)
        {
            _testForm.AddInput(observation);
        }
    });

    private void OnDiagnosticAdded(DiagnosticEntry entry) =>
        Dispatch(() => _testForm?.AddDiagnostic(entry));

    internal async void HandleFatalException(Exception exception)
    {
        try
        {
            await ExitAsync("CodexKeyboard stopped after an unexpected UI error.", exception);
        }
        catch
        {
            ExitThread();
        }
    }

    private void Dispatch(Action action)
    {
        if (_exiting || _dispatcher.IsDisposed)
        {
            return;
        }

        if (_dispatcher.InvokeRequired)
        {
            try
            {
                _dispatcher.BeginInvoke((Action)(() => ExecuteDispatched(action)));
            }
            catch (InvalidOperationException) when (_exiting || _dispatcher.IsDisposed)
            {
            }
            return;
        }

        ExecuteDispatched(action);
    }

    private void ExecuteDispatched(Action action)
    {
        if (_exiting)
        {
            return;
        }

        try
        {
            action();
        }
        catch (Exception exception)
        {
            HandleFatalException(exception);
        }
    }

    private async Task ExitAsync(string? terminalMessage = null, Exception? terminalException = null)
    {
        if (_exiting)
        {
            return;
        }

        _exiting = true;
        var modeGateEntered = false;
        Exception? shutdownFailure = null;
        try
        {
            _openTestsItem.Enabled = false;
            _reconnectItem.Enabled = false;
            _exitItem.Enabled = false;
            _trayIcon.Visible = false;

            await _modeGate.WaitAsync();
            modeGateEntered = true;
            try
            {
                await _service.DisposeAsync();
            }
            catch (Exception exception)
            {
                shutdownFailure = exception;
            }

            if (terminalException is not null)
            {
                ShowOperationError(terminalMessage!, terminalException);
            }
            else if (shutdownFailure is not null)
            {
                ShowOperationError("CodexKeyboard could not shut down cleanly.", shutdownFailure);
            }

            DisposeIgnoringErrors(_testForm);
            DisposeIgnoringErrors(_trayIcon);
            DisposeIgnoringErrors(_trayMenu);
            DisposeIgnoringErrors(_dispatcher);
        }
        catch (Exception exception)
        {
            try
            {
                ShowOperationError(
                    terminalMessage ?? "CodexKeyboard could not shut down cleanly.",
                    terminalException ?? exception);
            }
            catch
            {
            }
        }
        finally
        {
            if (modeGateEntered)
            {
                try
                {
                    _modeGate.Release();
                }
                catch
                {
                }
            }
            ExitThread();
        }
    }

    private static void ShowOperationError(string message, Exception exception) =>
        MessageBox.Show(
            $"{message}{Environment.NewLine}{Environment.NewLine}{exception.Message}",
            "CodexKeyboard",
            MessageBoxButtons.OK,
            MessageBoxIcon.Error);

    private static void DisposeIgnoringErrors(IDisposable? resource)
    {
        try
        {
            resource?.Dispose();
        }
        catch
        {
        }
    }
}
