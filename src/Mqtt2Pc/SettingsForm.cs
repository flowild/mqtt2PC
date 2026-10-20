using Mqtt2Pc.Core;

namespace Mqtt2Pc;

internal sealed class SettingsForm : Form
{
    private readonly AppSettings _original;
    private readonly Func<AppSettings, string, Task> _saveAndConnect;
    private readonly IStartupRegistration? _startup;
    private bool _startupEnabled;
    private readonly CheckBox _startAtSignIn = new() { Text = "Start mqtt2PC when I sign in to Windows", AutoSize = true };
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
    private readonly Dictionary<PowerAction, CheckBox> _power = [];
    private readonly ListView _actions = new()
    {
        Dock = DockStyle.Fill, View = View.Details, CheckBoxes = true,
        FullRowSelect = true, MultiSelect = false, HideSelection = false
    };
    private bool _saving;

    public SettingsForm(AppSettings settings, string password, Func<AppSettings, string, Task> saveAndConnect,
        IStartupRegistration? startup = null, Action? openLogs = null)
    {
        _original = settings;
        _saveAndConnect = saveAndConnect;
        _startup = startup;
        Text = "mqtt2PC — Settings";
        Icon = SystemIcons.Application;
        AutoScaleMode = AutoScaleMode.Dpi;
        ClientSize = new Size(740, 680);
        MinimumSize = new Size(640, 540);
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
        var tabs = new TabControl { Dock = DockStyle.Fill };
        var connection = new TabPage("Connection");
        connection.Controls.Add(scroll);
        tabs.TabPages.Add(connection);
        tabs.TabPages.Add(CreateActionsPage(settings));
        tabs.TabPages.Add(CreateStartupPage(openLogs));
        Controls.Add(tabs);

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
        var footer = new TableLayoutPanel
        {
            Dock = DockStyle.Bottom, AutoSize = true, ColumnCount = 2, Padding = new Padding(20, 8, 20, 8)
        };
        AddWide(footer, _status);
        AddWide(footer, _audio);
        AddWide(footer, _error);
        var buttons = new FlowLayoutPanel
        {
            AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Dock = DockStyle.Bottom,
            FlowDirection = FlowDirection.RightToLeft, Padding = new Padding(0, 4, 0, 4), WrapContents = false
        };
        var close = new Button { Text = "Close", AutoSize = true };
        close.Click += (_, _) => Close();
        buttons.Controls.Add(close);
        buttons.Controls.Add(_save);
        AddWide(footer, buttons);
        buttons.Dock = DockStyle.Fill;
        Controls.Add(footer);
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
                Username = _username.Text, ComputerName = _name.Text.Trim(),
                Power = new PowerControls
                {
                    Lock = _power[PowerAction.Lock].Checked, Sleep = _power[PowerAction.Sleep].Checked,
                    Shutdown = _power[PowerAction.Shutdown].Checked, Restart = _power[PowerAction.Restart].Checked
                },
                Actions = _actions.Items.Cast<ListViewItem>().Select(item => ((ConfiguredAction)item.Tag!) with { Enabled = item.Checked }).ToArray()
            };
            settings.Validate();
            await _saveAndConnect(settings, _password.Text);
            if (_startup is not null && _startAtSignIn.Enabled && _startAtSignIn.Checked != _startupEnabled)
            {
                _startup.SetEnabled(_startAtSignIn.Checked);
                _startupEnabled = _startAtSignIn.Checked;
            }
        }
        catch (ArgumentException exception) { _error.Text = exception.Message; }
        catch (Exception exception)
        {
            _error.Text = $"Could not save, connect or update startup ({exception.GetType().Name}). Check settings, startup folder and Windows Credential Manager access.";
        }
        finally
        {
            _saving = false;
            _save.Enabled = true;
        }
    }

    private TabPage CreateStartupPage(Action? openLogs)
    {
        var page = new TabPage("Startup and diagnostics") { Padding = new Padding(20) };
        var layout = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoScroll = true, FlowDirection = FlowDirection.TopDown, WrapContents = false };
        page.Controls.Add(layout);
        _startAtSignIn.Enabled = _startup is not null;
        try { _startupEnabled = _startup?.IsEnabled ?? false; }
        catch (Exception exception)
        {
            _startAtSignIn.Enabled = false;
            _error.Text = $"Could not read startup registration ({exception.GetType().Name}).";
        }
        _startAtSignIn.Checked = _startupEnabled;
        layout.Controls.Add(_startAtSignIn);
        layout.Controls.Add(new Label
        {
            Text = "Changes apply with Save and connect. Install the app before enabling startup so it keeps a stable location. Startup runs after sign-in, in your Windows session.",
            AutoSize = true, MaximumSize = new Size(640, 0), Margin = new Padding(0, 12, 0, 20)
        });
        layout.Controls.Add(new Label
        {
            Text = "Diagnostic logs record connections, errors and action outcomes. Passwords, command payloads and program arguments are excluded.",
            AutoSize = true, MaximumSize = new Size(640, 0)
        });
        layout.Controls.Add(new Label { Text = AppPaths.LogDirectory, AutoSize = true, MaximumSize = new Size(640, 0), Margin = new Padding(0, 12, 0, 12) });
        var logs = new Button { Text = "Open log folder", AutoSize = true, Enabled = openLogs is not null };
        logs.Click += (_, _) => openLogs?.Invoke();
        layout.Controls.Add(logs);
        layout.Controls.Add(new Label { Text = "mqtt2PC 0.4.0", AutoSize = true, Margin = new Padding(0, 20, 0, 0) });
        return page;
    }

    private TabPage CreateActionsPage(AppSettings settings)
    {
        var page = new TabPage("Power and actions") { Padding = new Padding(16) };
        var body = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 4 };
        body.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        body.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        body.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        body.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        page.Controls.Add(body);
        var power = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, WrapContents = true };
        power.Controls.Add(new Label { Text = "Enable power controls:", AutoSize = true, Margin = new Padding(0, 5, 15, 5) });
        foreach (var action in Enum.GetValues<PowerAction>())
        {
            var toggle = new CheckBox { Text = action.ToString(), AutoSize = true, Checked = settings.Power.IsEnabled(action) };
            _power.Add(action, toggle);
            power.Controls.Add(toggle);
        }
        body.Controls.Add(power, 0, 0);
        body.Controls.Add(new Label
        {
            Text = "Configured actions — checked actions appear as Home Assistant buttons. Changes apply when you select Save and connect.",
            AutoSize = true, MaximumSize = new Size(660, 0), Margin = new Padding(0, 15, 0, 12)
        }, 0, 1);
        _actions.Columns.Add("Name", 190);
        _actions.Columns.Add("Action ID", 160);
        _actions.Columns.Add("Executable", 320);
        foreach (var action in settings.Actions) AddActionItem(action);
        body.Controls.Add(_actions, 0, 2);
        var buttons = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, Padding = new Padding(0, 8, 0, 0) };
        var add = new Button { Text = "Add…", AutoSize = true };
        var edit = new Button { Text = "Edit…", AutoSize = true };
        var remove = new Button { Text = "Remove", AutoSize = true };
        add.Click += (_, _) =>
        {
            if (_actions.Items.Count >= 32) { _error.Text = "You can configure at most 32 actions."; return; }
            using var dialog = new ActionEditorForm(null, id => !_actions.Items.Cast<ListViewItem>().Any(item => ((ConfiguredAction)item.Tag!).Id == id));
            if (dialog.ShowDialog(this) == DialogResult.OK) AddActionItem(dialog.Action);
        };
        void EditSelected()
        {
            if (_actions.SelectedItems.Count == 0) return;
            var item = _actions.SelectedItems[0];
            var action = ((ConfiguredAction)item.Tag!) with { Enabled = item.Checked };
            using var dialog = new ActionEditorForm(action, id => !_actions.Items.Cast<ListViewItem>().Any(other => other != item && ((ConfiguredAction)other.Tag!).Id == id));
            if (dialog.ShowDialog(this) != DialogResult.OK) return;
            item.Text = dialog.Action.Name;
            item.SubItems[1].Text = dialog.Action.Id;
            item.SubItems[2].Text = dialog.Action.Executable;
            item.Checked = dialog.Action.Enabled;
            item.Tag = dialog.Action;
        }
        edit.Click += (_, _) => EditSelected();
        _actions.DoubleClick += (_, _) => EditSelected();
        remove.Click += (_, _) =>
        {
            if (_actions.SelectedItems.Count != 0) _actions.Items.Remove(_actions.SelectedItems[0]);
        };
        buttons.Controls.AddRange([add, edit, remove]);
        body.Controls.Add(buttons, 0, 3);
        return page;
    }

    private void AddActionItem(ConfiguredAction action)
    {
        var item = new ListViewItem(new[] { action.Name, action.Id, action.Executable }) { Checked = action.Enabled, Tag = action };
        _actions.Items.Add(item);
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
