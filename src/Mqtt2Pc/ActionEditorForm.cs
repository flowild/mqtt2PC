using Mqtt2Pc.Core;

namespace Mqtt2Pc;

internal sealed class ActionEditorForm : Form
{
    private readonly ConfiguredAction _original;
    private readonly TextBox _id = new() { MaxLength = 48 };
    private readonly TextBox _name = new() { MaxLength = 80 };
    private readonly TextBox _executable = new() { MaxLength = 1024 };
    private readonly TextBox _arguments = new() { Multiline = true, AcceptsReturn = true, WordWrap = false, ScrollBars = ScrollBars.Both, Height = 110 };
    private readonly TextBox _directory = new() { MaxLength = 1024 };
    private readonly CheckBox _enabled = new() { Text = "Expose this action in Home Assistant", AutoSize = true };
    private readonly Label _error = new() { AutoSize = true, ForeColor = Color.Firebrick, MaximumSize = new Size(550, 0) };

    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    internal ConfiguredAction Action { get; private set; }

    public ActionEditorForm(ConfiguredAction? action, Func<string, bool> idAvailable)
    {
        _original = action ?? new ConfiguredAction();
        Action = _original;
        Text = action is null ? "Add action" : "Edit action";
        Icon = SystemIcons.Application;
        AutoScaleMode = AutoScaleMode.Dpi;
        ClientSize = new Size(640, 530);
        MinimumSize = new Size(620, 540);
        StartPosition = FormStartPosition.CenterParent;
        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Top, Padding = new Padding(18), ColumnCount = 3,
            AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink
        };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 115));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 80));
        var scroll = new Panel { Dock = DockStyle.Fill, AutoScroll = true };
        scroll.Controls.Add(layout);
        Controls.Add(scroll);
        void Row(string title, Control control, Button? browse = null)
        {
            var row = layout.RowCount++;
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            layout.Controls.Add(new Label { Text = title, AutoSize = true, Anchor = AnchorStyles.Left }, 0, row);
            control.Dock = DockStyle.Fill;
            control.Margin = new Padding(3, 6, 3, 6);
            layout.Controls.Add(control, 1, row);
            if (browse is null) layout.SetColumnSpan(control, 2);
            else { browse.AutoSize = true; layout.Controls.Add(browse, 2, row); }
        }
        void Wide(Control control)
        {
            var row = layout.RowCount++;
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            layout.Controls.Add(control, 0, row);
            layout.SetColumnSpan(control, 3);
            control.Margin = new Padding(0, 8, 0, 8);
        }
        Row("Name", _name);
        Row("Action ID", _id);
        var file = new Button { Text = "Browse…" };
        file.Click += (_, _) =>
        {
            using var dialog = new OpenFileDialog { Filter = "Programs and batch files (*.exe;*.com;*.bat)|*.exe;*.com;*.bat", CheckFileExists = true };
            if (dialog.ShowDialog(this) == DialogResult.OK) _executable.Text = dialog.FileName;
        };
        Row("Executable", _executable, file);
        Row("Arguments", _arguments);
        Wide(new Label
        {
            Text = "Enter one argument per line, without surrounding quotes. Batch (.bat) files run directly. For other scripts, choose the interpreter and pass the script path as an argument.",
            AutoSize = true, MaximumSize = new Size(570, 0)
        });
        var folder = new Button { Text = "Browse…" };
        folder.Click += (_, _) =>
        {
            using var dialog = new FolderBrowserDialog();
            if (dialog.ShowDialog(this) == DialogResult.OK) _directory.Text = dialog.SelectedPath;
        };
        Row("Working folder", _directory, folder);
        Wide(new Label { Text = "Leave the working folder empty to use the executable's folder.", AutoSize = true });
        Wide(_enabled);
        var footer = new TableLayoutPanel { Dock = DockStyle.Bottom, AutoSize = true, Padding = new Padding(18, 4, 18, 12) };
        footer.Controls.Add(_error, 0, 0);
        var buttons = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, FlowDirection = FlowDirection.RightToLeft };
        var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, AutoSize = true };
        var save = new Button { Text = "Save action", AutoSize = true };
        save.Click += (_, _) =>
        {
            try
            {
                var edited = _original with
                {
                    Id = _id.Text.Trim(), Name = _name.Text.Trim(), Executable = _executable.Text.Trim(),
                    Arguments = _arguments.Text.Length == 0 ? [] : _arguments.Lines,
                    WorkingDirectory = _directory.Text.Trim(), Enabled = _enabled.Checked
                };
                edited.Validate();
                if (!idAvailable(edited.Id)) throw new ArgumentException("An action with this ID already exists.");
                Action = edited;
                DialogResult = DialogResult.OK;
                Close();
            }
            catch (ArgumentException exception) { _error.Text = exception.Message; }
        };
        buttons.Controls.Add(cancel);
        buttons.Controls.Add(save);
        footer.Controls.Add(buttons, 0, 1);
        Controls.Add(footer);
        AcceptButton = save;
        CancelButton = cancel;
        _id.Text = _original.Id;
        _id.ReadOnly = action is not null;
        _name.Text = _original.Name;
        _executable.Text = _original.Executable;
        _arguments.Lines = _original.Arguments;
        _directory.Text = _original.WorkingDirectory;
        _enabled.Checked = _original.Enabled;
    }
}
