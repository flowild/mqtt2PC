using Mqtt2Pc.Core;

namespace Mqtt2Pc;

internal sealed class SettingsForm : Form
{
    private readonly AppSettings _original;
    private readonly Func<AppSettings, string, Task> _saveAndConnect;
    private readonly TextBox _host = new() { MaxLength = 253 };
    private readonly NumericUpDown _port = new() { Minimum = 1, Maximum = 65535 };
    private readonly CheckBox _tls = new() { Text = "Use TLS (validate broker certificate)", AutoSize = true };
    private readonly TextBox _username = new() { MaxLength = 256 };
    private readonly TextBox _password = new() { UseSystemPasswordChar = true, MaxLength = 1024 };
    private readonly TextBox _name = new() { MaxLength = 80 };
    private readonly Label _status = new() { AutoSize = true, MaximumSize = new Size(490, 0) };
    private readonly Label _audio = new() { AutoSize = true, MaximumSize = new Size(490, 0) };
    private readonly Label _error = new() { AutoSize = true, ForeColor = Color.Firebrick, MaximumSize = new Size(490, 0) };
    private readonly Button _save = new() { Text = "Save and connect", AutoSize = true };
    private bool _saving;

    public SettingsForm(AppSettings settings, string password, Func<AppSettings, string, Task> saveAndConnect)
    {
        _original = settings;
        _saveAndConnect = saveAndConnect;
        Text = "mqtt2PC — Settings";
        Icon = SystemIcons.Application;
        AutoScaleMode = AutoScaleMode.Dpi;
        ClientSize = new Size(570, 680);
        MinimumSize = new Size(560, 540);
        StartPosition = FormStartPosition.CenterScreen;

        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Top, Padding = new Padding(20), ColumnCount = 2,
            AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, RowCount = 0
        };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 115));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        var scroll = new Panel { Dock = DockStyle.Fill, AutoScroll = true };
        scroll.Controls.Add(layout);
        Controls.Add(scroll);

        AddWide(layout, new Label { Text = "Connect this PC to your MQTT broker", AutoSize = true, Font = new Font(Font, FontStyle.Bold) });
        AddRow(layout, "Broker host", _host);
        AddRow(layout, "Port", _port);
        AddRow(layout, "Encryption", _tls);
        AddRow(layout, "Username", _username);
        AddRow(layout, "Password", _password);
        AddWide(layout, new Label { Text = "Password is stored in Windows Credential Manager. Leave blank for no password.", AutoSize = true, MaximumSize = new Size(490, 0) });
        AddRow(layout, "PC name", _name);
        AddRow(layout, "Device ID", new TextBox { Text = settings.DeviceId, ReadOnly = true });
        AddRow(layout, "Availability", new TextBox { Text = settings.AvailabilityTopic, ReadOnly = true });
        AddRow(layout, "Test command", new TextBox { Text = settings.TestCommandTopic, ReadOnly = true });
        AddRow(layout, "Results", new TextBox { Text = settings.ResultTopic, ReadOnly = true });
        AddWide(layout, new Label
        {
            Text = "Home Assistant automatically discovers volume, mute and media buttons through MQTT (default discovery prefix: homeassistant).",
            AutoSize = true, MaximumSize = new Size(490, 0)
        });
        AddWide(layout, _audio);

        var copy = new Button { Text = "Copy a fresh test payload", AutoSize = true };
        copy.Click += (_, _) =>
        {
            try
            {
                Clipboard.SetText(System.Text.Json.JsonSerializer.Serialize(new
                {
                    id = Guid.NewGuid().ToString("N"), message = "Hello from Home Assistant!"
                }));
                _error.Text = "Test payload copied. Publish it to the test command topic with Retain disabled.";
            }
            catch (System.Runtime.InteropServices.ExternalException)
            {
                _error.Text = "The clipboard is busy. Try again.";
            }
        };
        AddWide(layout, copy);
        AddWide(layout, _status);
        AddWide(layout, _error);
        var buttons = new FlowLayoutPanel
        {
            AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Dock = DockStyle.Bottom,
            FlowDirection = FlowDirection.RightToLeft, Padding = new Padding(20, 10, 20, 14), WrapContents = false
        };
        var close = new Button { Text = "Close", AutoSize = true };
        close.Click += (_, _) => Close();
        buttons.Controls.Add(close);
        buttons.Controls.Add(_save);
        Controls.Add(buttons);
        AcceptButton = _save;
        CancelButton = close;

        _host.Text = settings.BrokerHost;
        _port.Value = settings.BrokerPort;
        _tls.Checked = settings.UseTls;
        _username.Text = settings.Username;
        _password.Text = password;
        _name.Text = settings.ComputerName;
        _tls.CheckedChanged += (_, _) =>
        {
            if (_tls.Checked && _port.Value == 1883) _port.Value = 8883;
            else if (!_tls.Checked && _port.Value == 8883) _port.Value = 1883;
        };
        _save.Click += async (_, _) => await SaveAsync();
        FormClosing += (_, args) => args.Cancel = _saving;
    }

    public void SetStatus(ConnectionStatus status) => _status.Text = status.Message;

    public void SetAudioState(AudioState? state) => _audio.Text = state is null
        ? "Audio: unavailable or not connected"
        : $"Audio: {state.Volume}% — {(state.Muted ? "muted" : "unmuted")}";

    private async Task SaveAsync()
    {
        _saving = true;
        _save.Enabled = false;
        _error.Text = "";
        try
        {
            var settings = _original with
            {
                BrokerHost = _host.Text.Trim(), BrokerPort = (int)_port.Value, UseTls = _tls.Checked,
                Username = _username.Text, ComputerName = _name.Text.Trim()
            };
            settings.Validate();
            await _saveAndConnect(settings, _password.Text);
        }
        catch (ArgumentException exception) { _error.Text = exception.Message; }
        catch (Exception exception)
        {
            _error.Text = $"Could not save or connect ({exception.GetType().Name}). Check access to settings and Windows Credential Manager.";
        }
        finally
        {
            _saving = false;
            _save.Enabled = true;
        }
    }

    private static void AddRow(TableLayoutPanel layout, string title, Control control)
    {
        var row = layout.RowCount++;
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        var label = new Label { Text = title, AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(0, 8, 8, 8) };
        control.Dock = DockStyle.Fill;
        control.Margin = new Padding(0, 5, 0, 5);
        layout.Controls.Add(label, 0, row);
        layout.Controls.Add(control, 1, row);
    }

    private static void AddWide(TableLayoutPanel layout, Control control)
    {
        var row = layout.RowCount++;
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        control.Margin = new Padding(0, 7, 0, 7);
        layout.Controls.Add(control, 0, row);
        layout.SetColumnSpan(control, 2);
    }
}
