using CodexKeyboard.Protocol;
using System.Drawing;
using DeviceButtonState = CodexKeyboard.Protocol.ButtonState;
using DeviceControl = CodexKeyboard.Protocol.Control;

namespace CodexKeyboard.Companion;

internal sealed partial class HardwareTestForm : Form
{
    private const int MaximumDiagnosticRows = 500;

    private static readonly DeviceControl[] Buttons =
    [
        DeviceControl.Button1,
        DeviceControl.Button2,
        DeviceControl.Button3,
        DeviceControl.KnobButton,
    ];

    private readonly KeyboardService _service = null!;
    private readonly Dictionary<DeviceControl, Label> _buttonIndicators = [];
    private readonly Button[] _colorButtons = new Button[3];
    private readonly SemaphoreSlim _operationGate = new(1, 1);
    private byte _maximumBrightness = byte.MaxValue;
    private bool _hideRequestPending;

    public HardwareTestForm()
    {
        InitializeComponent();

        _buttonIndicators[DeviceControl.Button1] = _button1Indicator;
        _buttonIndicators[DeviceControl.Button2] = _button2Indicator;
        _buttonIndicators[DeviceControl.Button3] = _button3Indicator;
        _buttonIndicators[DeviceControl.KnobButton] = _knobButtonIndicator;
        _colorButtons[0] = _led1ColorButton;
        _colorButtons[1] = _led2ColorButton;
        _colorButtons[2] = _led3ColorButton;
        UpdatePeriodState();
    }

    public HardwareTestForm(KeyboardService service)
        : this()
    {
        _service = service;
    }

    public event EventHandler? HideRequested;

    public event EventHandler? ExitRequested;

    public void ApplyStatus(ServiceStatus status)
    {
        _stateValue.Text = status.State.ToString();
        _stateValue.ForeColor = status.State switch
        {
            ServiceConnectionState.Connected => Color.DarkGreen,
            ServiceConnectionState.Connecting => Color.DarkOrange,
            _ => Color.DarkRed,
        };
        _detailValue.Text = status.Detail;

        if (status.DeviceInfo is not { } info)
        {
            _deviceValue.Text = "No verified device information";
            return;
        }

        _maximumBrightness = info.MaximumBrightness;
        _brightness.Maximum = _maximumBrightness;
        if (_brightness.Value > _maximumBrightness)
        {
            _brightness.Value = _maximumBrightness;
        }

        for (var led = 0; led < 3; led++)
        {
            var color = _colorButtons[led].BackColor;
            SetLedColor(led, Color.FromArgb(
                Math.Min(color.R, _maximumBrightness),
                Math.Min(color.G, _maximumBrightness),
                Math.Min(color.B, _maximumBrightness)));
        }

        _deviceValue.Text =
            $"Firmware {info.FirmwareMajor}.{info.FirmwareMinor}.{info.FirmwarePatch}  |  " +
            $"Capabilities 0x{(ushort)info.Capabilities:X4}  |  Max RGB {_maximumBrightness}";
    }

    public void AddInput(InputObservation observation)
    {
        var input = observation.Event;
        var showEncoderDirection = observation.ShouldDispatch &&
                                   input.Control == DeviceControl.Encoder &&
                                   input.Kind == InputKind.Rotate;
        UpdateButtonIndicators(observation.Buttons, updateKnob: !showEncoderDirection);
        if (showEncoderDirection)
        {
            ShowEncoderDirection(input.Value);
        }
    }

    public void AddDiagnostic(DiagnosticEntry entry)
    {
        var row = new ListViewItem($"{entry.Timestamp:HH:mm:ss.fff}");
        row.SubItems.Add(entry.Level.ToString());
        row.SubItems.Add(entry.Message);
        AddBoundedRow(_diagnosticList, row, MaximumDiagnosticRows);
    }

    public void PrepareForShow()
    {
        _hideRequestPending = false;
        ResetInputView();
        WindowState = FormWindowState.Normal;
        ShowInTaskbar = true;
    }

    public void HideToTray()
    {
        Hide();
        WindowState = FormWindowState.Normal;
        _hideRequestPending = false;
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (e.CloseReason == CloseReason.UserClosing)
        {
            e.Cancel = true;
            RequestHide();
        }
        base.OnFormClosing(e);
    }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        if (WindowState == FormWindowState.Minimized)
        {
            RequestHide();
        }
    }

    private async void RefreshDeviceInfoButton_Click(object? sender, EventArgs e) =>
        await RunOperationAsync("Refresh device info", () => _service.RefreshInfoAsync());

    private async void PingButton_Click(object? sender, EventArgs e) =>
        await RunOperationAsync("Ping", () => _service.PingAsync());

    private void ReconnectButton_Click(object? sender, EventArgs e) => _service.Reconnect();

    private void ExitApplicationButton_Click(object? sender, EventArgs e) =>
        ExitRequested?.Invoke(this, EventArgs.Empty);

    private void Effect_SelectedIndexChanged(object? sender, EventArgs e) => UpdatePeriodState();

    private async void SendSceneButton_Click(object? sender, EventArgs e) =>
        await RunOperationAsync("Send scene", SendSceneAsync);

    private void Led1ColorButton_Click(object? sender, EventArgs e) => ChooseColor(0);

    private void Led2ColorButton_Click(object? sender, EventArgs e) => ChooseColor(1);

    private void Led3ColorButton_Click(object? sender, EventArgs e) => ChooseColor(2);

    private async void SendRgbButton_Click(object? sender, EventArgs e) =>
        await RunOperationAsync("Send RGB", SendRgbAsync);

    private async void OffButton_Click(object? sender, EventArgs e)
    {
        await RunOperationAsync("Off", async () =>
        {
            SetAllRgb(0);
            await SendRgbAsync();
        });
    }

    private async void WhiteButton_Click(object? sender, EventArgs e)
    {
        await RunOperationAsync("White", async () =>
        {
            SetAllRgb(_maximumBrightness);
            await SendRgbAsync();
        });
    }

    private async void EnterBootloaderButton_Click(object? sender, EventArgs e) =>
        await ConfirmAndEnterBootloaderAsync();

    private async void WatchdogTestButton_Click(object? sender, EventArgs e) =>
        await RunOperationAsync("Watchdog test", () => _service.RunWatchdogTestAsync());

    private void ClearDiagnosticsButton_Click(object? sender, EventArgs e) => _diagnosticList.Items.Clear();

    private async Task SendSceneAsync()
    {
        if (_scene.SelectedItem is not Scene scene || _effect.SelectedItem is not Effect effect)
        {
            throw new InvalidOperationException("Select a scene and effect.");
        }

        var periodMs = decimal.ToInt32(_periodMs.Value);
        if (effect == Effect.Solid && periodMs != 0)
        {
            throw new InvalidOperationException("A solid scene requires a zero millisecond period.");
        }
        if (effect != Effect.Solid && (periodMs < 10 || periodMs % 10 != 0))
        {
            throw new InvalidOperationException("An animated scene requires a 10–60000 ms period in 10 ms steps.");
        }

        await _service.SetSceneAsync(
            scene,
            effect,
            decimal.ToByte(_brightness.Value),
            checked((ushort)(periodMs / 10)));
    }

    private async Task SendRgbAsync()
    {
        var values = new byte[9];
        for (var led = 0; led < 3; led++)
        {
            var color = _colorButtons[led].BackColor;
            values[led * 3] = color.R;
            values[(led * 3) + 1] = color.G;
            values[(led * 3) + 2] = color.B;
        }
        await _service.SetRgbAsync(values);
    }

    private async Task ConfirmAndEnterBootloaderAsync()
    {
        var result = MessageBox.Show(
            this,
            "This will disconnect the runtime HID device and stop all companion functions.\n\n" +
            "The companion has no WCH bootloader driver and cannot command ROM exit. Continue only if " +
            "you are ready to wait for the ROM timeout or power-cycle USB.",
            "Confirm ENTER_BOOTLOADER",
            MessageBoxButtons.YesNo,
            MessageBoxIcon.Warning,
            MessageBoxDefaultButton.Button2);
        if (result == DialogResult.Yes)
        {
            await RunOperationAsync("Entering bootloader", () => _service.EnterBootloaderAsync());
        }
    }

    private async Task RunOperationAsync(string name, Func<Task> operation)
    {
        if (!await _operationGate.WaitAsync(0))
        {
            return;
        }

        try
        {
            UseWaitCursor = true;
            _operationValue.ForeColor = SystemColors.ControlText;
            _operationValue.Text = $"{name}...";
            await operation();
            _operationValue.ForeColor = Color.DarkGreen;
            _operationValue.Text = $"{name}: completed.";
        }
        catch (Exception exception)
        {
            _operationValue.ForeColor = Color.DarkRed;
            _operationValue.Text = $"{name}: {exception.Message}";
        }
        finally
        {
            UseWaitCursor = false;
            _operationGate.Release();
        }
    }

    private void ChooseColor(int led)
    {
        using var picker = new ColorDialog
        {
            Color = _colorButtons[led].BackColor,
            FullOpen = true,
        };
        if (picker.ShowDialog(this) != DialogResult.OK)
        {
            return;
        }

        SetLedColor(led, Color.FromArgb(
            Math.Min(picker.Color.R, _maximumBrightness),
            Math.Min(picker.Color.G, _maximumBrightness),
            Math.Min(picker.Color.B, _maximumBrightness)));
    }

    private void SetLedColor(int led, Color color)
    {
        var button = _colorButtons[led];
        button.BackColor = color;
        var luminance = ((299 * color.R) + (587 * color.G) + (114 * color.B)) / 1000;
        button.ForeColor = luminance >= 128 ? Color.Black : Color.White;
    }

    private void SetAllRgb(byte value)
    {
        for (var led = 0; led < 3; led++)
        {
            SetLedColor(led, Color.FromArgb(value, value, value));
        }
    }

    private void UpdatePeriodState()
    {
        var solid = _effect.SelectedItem is Effect.Solid;
        _periodMs.Enabled = !solid;
        if (solid)
        {
            _periodMs.Value = 0;
        }
        else if (_periodMs.Value == 0)
        {
            _periodMs.Value = 1_000;
        }
    }

    private void UpdateButtonIndicators(DeviceButtonState state, bool updateKnob = true)
    {
        foreach (var control in Buttons)
        {
            if (!updateKnob && control == DeviceControl.KnobButton)
            {
                continue;
            }

            var pressed = state.HasFlag(ButtonFlag(control));
            var indicator = _buttonIndicators[control];
            var text = control == DeviceControl.KnobButton
                ? pressed ? "PRESS: DOWN" : "PRESS: UP"
                : pressed ? "DOWN" : "UP";
            if (indicator.Text != text)
            {
                indicator.Text = text;
            }

            if (control == DeviceControl.KnobButton)
            {
                continue;
            }

            indicator.BackColor = pressed ? Color.LightGreen : Color.Gainsboro;
        }
    }

    private void ShowEncoderDirection(sbyte value)
    {
        var text = value > 0 ? "CLOCKWISE  →" : "←  COUNTERCLOCKWISE";
        if (_knobButtonIndicator.Text != text)
        {
            _knobButtonIndicator.Text = text;
        }
    }

    private void ResetInputView()
    {
        UpdateButtonIndicators(DeviceButtonState.None);
    }

    private void RequestHide()
    {
        if (_hideRequestPending)
        {
            return;
        }
        _hideRequestPending = true;
        HideRequested?.Invoke(this, EventArgs.Empty);
    }

    private static void AddBoundedRow(ListView list, ListViewItem row, int maximumRows)
    {
        list.BeginUpdate();
        try
        {
            list.Items.Add(row);
            while (list.Items.Count > maximumRows)
            {
                list.Items.RemoveAt(0);
            }
            row.EnsureVisible();
        }
        finally
        {
            list.EndUpdate();
        }
    }

    private static DeviceButtonState ButtonFlag(DeviceControl control) => control switch
    {
        DeviceControl.Button1 => DeviceButtonState.Button1,
        DeviceControl.Button2 => DeviceButtonState.Button2,
        DeviceControl.Button3 => DeviceButtonState.Button3,
        DeviceControl.KnobButton => DeviceButtonState.KnobButton,
        _ => DeviceButtonState.None,
    };

}
