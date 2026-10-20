using Mqtt2Pc.Core;

namespace Mqtt2Pc;

internal sealed class TrayApplicationContext : ApplicationContext
{
    private readonly Control _dispatcher = new();
    private readonly NotifyIcon _tray;
    private readonly ContextMenuStrip _menu = new();
    private readonly ToolStripMenuItem _statusItem = new("Disconnected") { Enabled = false };
    private readonly ToolStripMenuItem _reconnectItem = new("Reconnect");
    private readonly SettingsStore _store = new(Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "mqtt2PC", "settings.json"));
    private readonly SemaphoreSlim _transition = new(1, 1);
    private AppSettings _settings = new();
    private ConnectionStatus _status = new(false, "Enter your MQTT broker details to connect.");
    private SettingsForm? _form;
    private CancellationTokenSource? _agentCancellation;
    private Task? _agentTask;
    private bool _exiting;

    public TrayApplicationContext()
    {
        _ = _dispatcher.Handle;
        _menu.Items.Add(_statusItem);
        _menu.Items.Add(new ToolStripSeparator());
        _menu.Items.Add("Settings…", null, (_, _) => ShowSettings());
        _menu.Items.Add(_reconnectItem);
        _menu.Items.Add(new ToolStripSeparator());
        _menu.Items.Add("Exit", null, async (_, _) => await ShutdownAsync());
        _reconnectItem.Click += async (_, _) =>
        {
            if (string.IsNullOrWhiteSpace(_settings.BrokerHost)) { ShowSettings(); return; }
            try { await RestartAsync(_settings, CredentialStore.Read(_settings.DeviceId)); }
            catch (Exception exception) { ShowError("Could not reconnect", exception); }
        };
        _tray = new NotifyIcon
        {
            Icon = SystemIcons.Application, Text = "mqtt2PC — Disconnected", ContextMenuStrip = _menu, Visible = true
        };
        _tray.DoubleClick += (_, _) => ShowSettings();

        // Start once the Windows message loop can dispatch UI work.
        _dispatcher.BeginInvoke(async () =>
        {
            try
            {
                var saved = _store.Load();
                if (saved is null) ShowSettings();
                else
                {
                    _settings = saved;
                    await RestartAsync(saved, CredentialStore.Read(saved.DeviceId));
                }
            }
            catch (Exception exception)
            {
                ShowError("Could not load settings or credentials. Existing settings have been kept", exception);
                ShowSettings();
            }
        });
    }

    private void ShowSettings()
    {
        if (_exiting) return;
        if (_form is not null) { _form.Activate(); return; }
        string password;
        try { password = CredentialStore.Read(_settings.DeviceId); }
        catch (Exception exception)
        {
            ShowError("Could not read the saved broker password", exception);
            return;
        }
        _form = new SettingsForm(_settings, password, async (settings, secret) =>
        {
            CredentialStore.Write(settings.DeviceId, settings.Username, secret);
            _store.Save(settings);
            _settings = settings;
            await RestartAsync(settings, secret);
        });
        _form.SetStatus(_status);
        _form.FormClosed += (_, _) => _form = null;
        _form.Show();
    }

    private async Task RestartAsync(AppSettings settings, string password)
    {
        await _transition.WaitAsync();
        try
        {
            if (_exiting) return;
            await StopAgentAsync();
            _agentCancellation = new CancellationTokenSource();
            var token = _agentCancellation.Token;
            var agent = new MqttAgent(settings, password, ShowNotificationAsync);
            agent.StatusChanged += status => Post(() => UpdateStatus(status));
            _agentTask = Task.Run(() => agent.RunAsync(token));
        }
        finally { _transition.Release(); }
    }

    private async Task StopAgentAsync()
    {
        if (_agentCancellation is null) return;
        _agentCancellation.Cancel();
        try { if (_agentTask is not null) await _agentTask; }
        finally
        {
            _agentCancellation.Dispose();
            _agentCancellation = null;
            _agentTask = null;
        }
    }

    private async Task ShowNotificationAsync(string message, CancellationToken token)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var registration = token.Register(() => completion.TrySetCanceled(token));
        Post(() =>
        {
            if (token.IsCancellationRequested || _exiting) return;
            try
            {
                _tray.BalloonTipTitle = "mqtt2PC — Connection test";
                _tray.BalloonTipText = message;
                _tray.BalloonTipIcon = ToolTipIcon.Info;
                _tray.ShowBalloonTip(4000);
                completion.TrySetResult();
            }
            catch (Exception exception) { completion.TrySetException(exception); }
        });
        await completion.Task.ConfigureAwait(false);
    }

    private void UpdateStatus(ConnectionStatus status)
    {
        _status = status;
        _statusItem.Text = status.IsConnected ? "Connected" : "Disconnected";
        _tray.Text = status.IsConnected ? "mqtt2PC — Connected" : "mqtt2PC — Disconnected";
        _form?.SetStatus(status);
    }

    private async Task ShutdownAsync()
    {
        if (_exiting) return;
        _exiting = true;
        _menu.Enabled = false;
        if (_form is not null) _form.Enabled = false;
        await _transition.WaitAsync();
        try { await StopAgentAsync(); }
        finally
        {
            _transition.Release();
            _form?.Close();
            _tray.Visible = false;
            ExitThread();
        }
    }

    private void Post(Action action)
    {
        if (_exiting || _dispatcher.IsDisposed) return;
        try { _dispatcher.BeginInvoke(action); }
        catch (InvalidOperationException) when (_exiting || _dispatcher.IsDisposed) { }
    }

    private static void ShowError(string title, Exception exception) =>
        MessageBox.Show($"{title}.\n\nError: {exception.GetType().Name}", "mqtt2PC", MessageBoxButtons.OK, MessageBoxIcon.Error);

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _agentCancellation?.Cancel();
            _form?.Dispose();
            _tray.Dispose();
            _menu.Dispose();
            _dispatcher.Dispose();
        }
        base.Dispose(disposing);
    }
}
