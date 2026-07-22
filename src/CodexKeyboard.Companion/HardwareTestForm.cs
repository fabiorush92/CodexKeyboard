using CodexKeyboard.Protocol;
using System.Drawing;
using DeviceButtonState = CodexKeyboard.Protocol.ButtonState;
using DeviceControl = CodexKeyboard.Protocol.Control;
using WinFormsControl = System.Windows.Forms.Control;

namespace CodexKeyboard.Companion;

internal sealed class HardwareTestForm : Form
{
    private const int MaximumDiagnosticRows = 500;

    private static readonly DeviceControl[] Buttons =
    [
        DeviceControl.Button1,
        DeviceControl.Button2,
        DeviceControl.Button3,
        DeviceControl.KnobButton,
    ];

    private readonly KeyboardService _service;
    private readonly Label _stateValue = ValueLabel();
    private readonly Label _modeValue = ValueLabel();
    private readonly Label _deviceValue = ValueLabel();
    private readonly Label _detailValue = ValueLabel();
    private readonly Label _operationValue = ValueLabel();
    private readonly Label _sequenceWarning = ValueLabel();
    private readonly Label _encoderDirection = ValueLabel();
    private readonly ListView _diagnosticList = CreateListView();
    private readonly Dictionary<DeviceControl, Label> _buttonIndicators = [];
    private readonly ComboBox _scene = DropDown();
    private readonly ComboBox _effect = DropDown();
    private readonly NumericUpDown _brightness = Number(0, 255, 255, 1);
    private readonly NumericUpDown _periodMs = Number(0, 60_000, 1_000, 10);
    private readonly Button[] _colorButtons = new Button[3];
    private readonly SemaphoreSlim _operationGate = new(1, 1);
    private byte _maximumBrightness = byte.MaxValue;
    private bool _hideRequestPending;

    public HardwareTestForm(KeyboardService service)
    {
        _service = service;
        Text = "CodexKeyboard hardware tests";
        StartPosition = FormStartPosition.CenterScreen;
        MinimumSize = new Size(800, 600);
        Size = new Size(880, 660);
        AutoScaleMode = AutoScaleMode.Dpi;

        var root = new TableLayoutPanel
        {
            ColumnCount = 1,
            Dock = DockStyle.Fill,
            Padding = new Padding(6),
            RowCount = 3,
        };
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        root.Controls.Add(BuildStatusHeader(), 0, 0);
        root.Controls.Add(BuildContent(), 0, 1);
        _operationValue.Text = "Ready.";
        _operationValue.Padding = new Padding(4);
        root.Controls.Add(_operationValue, 0, 2);
        Controls.Add(root);
    }

    public event EventHandler? HideRequested;

    public void ApplyStatus(ServiceStatus status)
    {
        _stateValue.Text = status.State.ToString();
        _stateValue.ForeColor = status.State switch
        {
            ServiceConnectionState.Connected => Color.DarkGreen,
            ServiceConnectionState.Connecting => Color.DarkOrange,
            _ => Color.DarkRed,
        };
        _modeValue.Text = status.Mode.ToString();
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
        if (observation.ShouldDispatch &&
            input.Control == DeviceControl.Encoder &&
            input.Kind == InputKind.Rotate)
        {
            ShowEncoderDirection(input.Value);
        }

        UpdateButtonIndicators(observation.Buttons);
        if (observation.SequenceGap || input.Flags.HasFlag(InputFlags.QueueOverflow))
        {
            var cause = observation.SequenceGap
                ? $"Sequence gap at 0x{input.Sequence:X2} ({observation.MissingCount} missing report(s))"
                : $"Queue overflow at 0x{input.Sequence:X2}";
            _sequenceWarning.Text = observation.WaitingForSequenceGap
                ? $"{cause}; buffered historical events are suppressed until their sequence gap arrives."
                : observation.WaitingForRelease
                    ? $"{cause}; events are suppressed until all buttons are released."
                    : $"{cause}; this event was suppressed and state was resynchronized.";
            _sequenceWarning.ForeColor = Color.DarkRed;
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

    private WinFormsControl BuildStatusHeader()
    {
        var group = new GroupBox
        {
            AutoSize = true,
            Dock = DockStyle.Top,
            Text = "Device status",
        };
        var layout = new TableLayoutPanel
        {
            AutoSize = true,
            ColumnCount = 4,
            Dock = DockStyle.Fill,
            Padding = new Padding(4),
            RowCount = 3,
        };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));

        AddField(layout, 0, "State", _stateValue);
        AddField(layout, 0, "Mode", _modeValue, 2);
        AddField(layout, 1, "Device", _deviceValue);
        layout.SetColumnSpan(_deviceValue, 3);
        AddField(layout, 2, "Detail", _detailValue);
        layout.SetColumnSpan(_detailValue, 3);
        group.Controls.Add(layout);
        return group;
    }

    private WinFormsControl BuildContent()
    {
        var layout = new TableLayoutPanel
        {
            ColumnCount = 1,
            Dock = DockStyle.Fill,
            RowCount = 3,
        };
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.Controls.Add(BuildInputsGroup(), 0, 0);
        layout.Controls.Add(BuildDeviceOutputGroup(), 0, 1);
        layout.Controls.Add(BuildDiagnosticsGroup(), 0, 2);
        return layout;
    }

    private WinFormsControl BuildInputsGroup()
    {
        var group = new GroupBox { AutoSize = true, Dock = DockStyle.Top, Text = "Inputs" };
        var layout = new TableLayoutPanel
        {
            AutoSize = true,
            ColumnCount = 5,
            Dock = DockStyle.Fill,
            Padding = new Padding(4),
            RowCount = 2,
        };
        for (var column = 0; column < 4; column++)
        {
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 15));
            var control = Buttons[column];
            var panel = new TableLayoutPanel
            {
                AutoSize = true,
                ColumnCount = 1,
                Dock = DockStyle.Fill,
                Margin = new Padding(2),
            };
            panel.Controls.Add(new Label
            {
                AutoSize = true,
                Font = new Font(Font, FontStyle.Bold),
                Text = ButtonName(control),
            });
            var indicator = new Label
            {
                AutoSize = false,
                BackColor = Color.Gainsboro,
                BorderStyle = BorderStyle.FixedSingle,
                Height = 24,
                Text = "UP",
                TextAlign = ContentAlignment.MiddleCenter,
                Width = 95,
            };
            panel.Controls.Add(indicator);
            _buttonIndicators[control] = indicator;
            layout.Controls.Add(panel, column, 0);
        }
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 40));

        var encoder = new TableLayoutPanel
        {
            AutoSize = true,
            ColumnCount = 1,
            Dock = DockStyle.Fill,
            Margin = new Padding(2),
        };
        encoder.Controls.Add(new Label
        {
            AutoSize = true,
            Font = new Font(Font, FontStyle.Bold),
            Text = "Encoder",
        });
        _encoderDirection.AutoSize = false;
        _encoderDirection.BackColor = Color.Gainsboro;
        _encoderDirection.BorderStyle = BorderStyle.FixedSingle;
        _encoderDirection.Height = 24;
        _encoderDirection.TextAlign = ContentAlignment.MiddleCenter;
        _encoderDirection.Width = 210;
        encoder.Controls.Add(_encoderDirection);
        layout.Controls.Add(encoder, 4, 0);

        _sequenceWarning.Text = "No sequence gaps or queue overflows observed.";
        _sequenceWarning.Padding = new Padding(2);
        layout.Controls.Add(_sequenceWarning, 0, 1);
        layout.SetColumnSpan(_sequenceWarning, 5);
        group.Controls.Add(layout);
        return group;
    }

    private WinFormsControl BuildDeviceOutputGroup()
    {
        var group = new GroupBox { AutoSize = true, Dock = DockStyle.Top, Text = "Device and LED output" };
        var layout = new TableLayoutPanel
        {
            AutoSize = true,
            ColumnCount = 1,
            Dock = DockStyle.Fill,
            Padding = new Padding(4),
            RowCount = 4,
        };

        var deviceCommands = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Top };
        deviceCommands.Controls.Add(CommandButton("Refresh device info", () => _service.RefreshInfoAsync()));
        deviceCommands.Controls.Add(CommandButton("Ping", () => _service.PingAsync()));
        var reconnect = new Button { AutoSize = true, Text = "Reconnect" };
        reconnect.Click += (_, _) => _service.Reconnect();
        deviceCommands.Controls.Add(reconnect);
        layout.Controls.Add(deviceCommands, 0, 0);

        var scene = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Top, WrapContents = true };
        _scene.Items.AddRange(Enum.GetValues<Scene>().Select(value => (object)value).ToArray());
        _scene.SelectedItem = Scene.CompanionConnected;
        _effect.Items.AddRange(Enum.GetValues<Effect>().Select(value => (object)value).ToArray());
        _effect.SelectedItem = Effect.Solid;
        _effect.SelectedIndexChanged += (_, _) => UpdatePeriodState();
        scene.Controls.Add(FieldLabel("Scene"));
        scene.Controls.Add(_scene);
        scene.Controls.Add(FieldLabel("Effect"));
        scene.Controls.Add(_effect);
        scene.Controls.Add(FieldLabel("Brightness"));
        scene.Controls.Add(_brightness);
        scene.Controls.Add(FieldLabel("Period (ms)"));
        scene.Controls.Add(_periodMs);
        scene.Controls.Add(CommandButton("Send scene", SendSceneAsync));
        layout.Controls.Add(scene, 0, 1);

        var colors = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Top, WrapContents = true };
        colors.Controls.Add(FieldLabel("Direct colors"));
        for (var led = 0; led < 3; led++)
        {
            var selectedLed = led;
            var button = new Button
            {
                AutoSize = false,
                BackColor = Color.Black,
                ForeColor = Color.White,
                Height = 26,
                Text = $"LED {led + 1} color...",
                UseVisualStyleBackColor = false,
                Width = 110,
            };
            button.Click += (_, _) => ChooseColor(selectedLed);
            _colorButtons[led] = button;
            colors.Controls.Add(button);
        }
        colors.Controls.Add(CommandButton("Send RGB", SendRgbAsync));
        colors.Controls.Add(CommandButton("Off", async () =>
        {
            SetAllRgb(0);
            await SendRgbAsync();
        }));
        colors.Controls.Add(CommandButton("White", async () =>
        {
            SetAllRgb(_maximumBrightness);
            await SendRgbAsync();
        }));
        layout.Controls.Add(colors, 0, 2);

        var watchdog = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Top, WrapContents = true };
        watchdog.Controls.Add(FieldLabel("Heartbeat watchdog"));
        watchdog.Controls.Add(new Label
        {
            AutoSize = true,
            Margin = new Padding(3, 8, 10, 3),
            Text = "Pause heartbeat and verify the companion-absent fallback.",
        });
        watchdog.Controls.Add(CommandButton("Run watchdog test", () => _service.RunWatchdogTestAsync()));
        layout.Controls.Add(watchdog, 0, 3);
        group.Controls.Add(layout);
        UpdatePeriodState();
        return group;
    }

    private WinFormsControl BuildDiagnosticsGroup()
    {
        var group = new GroupBox { Dock = DockStyle.Fill, Text = "Diagnostics" };
        var layout = new TableLayoutPanel
        {
            ColumnCount = 1,
            Dock = DockStyle.Fill,
            Padding = new Padding(4),
            RowCount = 3,
        };
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.Controls.Add(new Label
        {
            AutoSize = true,
            ForeColor = Color.DarkRed,
            MaximumSize = new Size(740, 0),
            Padding = new Padding(2),
            Text = "Safety: pressing all four controls enters ROM immediately. ENTER_BOOTLOADER disconnects " +
                   "runtime HID; recovery depends on the ROM timeout or a USB power cycle.",
        }, 0, 0);

        var commands = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Top };
        var enterBootloader = new Button { AutoSize = true, Text = "Enter bootloader..." };
        enterBootloader.Click += async (_, _) => await ConfirmAndEnterBootloaderAsync();
        commands.Controls.Add(enterBootloader);
        var clear = new Button { AutoSize = true, Text = "Clear diagnostic view" };
        clear.Click += (_, _) => _diagnosticList.Items.Clear();
        commands.Controls.Add(clear);
        layout.Controls.Add(commands, 0, 1);

        _diagnosticList.Columns.Add("Time", 105);
        _diagnosticList.Columns.Add("Level", 90);
        _diagnosticList.Columns.Add("Message", 580);
        layout.Controls.Add(_diagnosticList, 0, 2);
        group.Controls.Add(layout);
        return group;
    }

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

    private Button CommandButton(string text, Func<Task> operation)
    {
        var button = new Button { AutoSize = true, Text = text };
        button.Click += async (_, _) => await RunOperationAsync(text, operation);
        return button;
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

    private void UpdateButtonIndicators(DeviceButtonState state)
    {
        foreach (var control in Buttons)
        {
            var pressed = state.HasFlag(ButtonFlag(control));
            var indicator = _buttonIndicators[control];
            indicator.Text = pressed ? "DOWN" : "UP";
            indicator.BackColor = pressed ? Color.LightGreen : Color.Gainsboro;
        }
    }

    private void ShowEncoderDirection(sbyte value)
    {
        _encoderDirection.Text = value > 0 ? "CLOCKWISE  →" : "←  COUNTERCLOCKWISE";
        _encoderDirection.BackColor = value > 0 ? Color.LightGreen : Color.LightSkyBlue;
    }

    private void ResetInputView()
    {
        UpdateButtonIndicators(DeviceButtonState.None);
        _encoderDirection.Text = "Waiting for rotation";
        _encoderDirection.BackColor = Color.Gainsboro;
        _sequenceWarning.ForeColor = SystemColors.ControlText;
        _sequenceWarning.Text = "No sequence gaps or queue overflows observed.";
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

    private static void AddField(
        TableLayoutPanel layout,
        int row,
        string name,
        Label value,
        int column = 0)
    {
        layout.Controls.Add(new Label
        {
            Anchor = AnchorStyles.Left,
            AutoSize = true,
            Font = new Font(SystemFonts.MessageBoxFont!, FontStyle.Bold),
            Margin = new Padding(3),
            Text = $"{name}:",
            TextAlign = ContentAlignment.MiddleLeft,
        }, column, row);
        value.Anchor = AnchorStyles.Left;
        layout.Controls.Add(value, column + 1, row);
    }

    private static Label FieldLabel(string text) => new()
    {
        AutoSize = true,
        Font = new Font(SystemFonts.MessageBoxFont!, FontStyle.Bold),
        Margin = new Padding(3, 8, 3, 3),
        Text = text,
    };

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

    private static Label ValueLabel() => new()
    {
        AutoEllipsis = true,
        AutoSize = true,
        Anchor = AnchorStyles.Left,
        Margin = new Padding(3),
        TextAlign = ContentAlignment.MiddleLeft,
    };

    private static ComboBox DropDown() => new()
    {
        DropDownStyle = ComboBoxStyle.DropDownList,
        Width = 155,
    };

    private static NumericUpDown Number(decimal minimum, decimal maximum, decimal value, decimal increment) => new()
    {
        Increment = increment,
        Maximum = maximum,
        Minimum = minimum,
        Value = value,
        Width = 90,
    };

    private static ListView CreateListView() => new()
    {
        Dock = DockStyle.Fill,
        FullRowSelect = true,
        GridLines = true,
        HideSelection = false,
        View = View.Details,
    };

    private static DeviceButtonState ButtonFlag(DeviceControl control) => control switch
    {
        DeviceControl.Button1 => DeviceButtonState.Button1,
        DeviceControl.Button2 => DeviceButtonState.Button2,
        DeviceControl.Button3 => DeviceButtonState.Button3,
        DeviceControl.KnobButton => DeviceButtonState.KnobButton,
        _ => DeviceButtonState.None,
    };

    private static string ButtonName(DeviceControl control) => control switch
    {
        DeviceControl.Button1 => "Button 1",
        DeviceControl.Button2 => "Button 2",
        DeviceControl.Button3 => "Button 3",
        DeviceControl.KnobButton => "Knob press",
        _ => control.ToString(),
    };
}
