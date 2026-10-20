using Mqtt2Pc.Core;
using System.Runtime.InteropServices;
using Xunit;
using Xunit.Abstractions;

namespace Mqtt2Pc.Tests;

public sealed class WindowsTests(ITestOutputHelper output)
{
    [Theory]
    [InlineData(PowerAction.Shutdown, "/s")]
    [InlineData(PowerAction.Restart, "/r")]
    public void PowerProcessSpecificationsNeverForceCloseApplications(PowerAction action, string flag)
    {
        var info = WindowsPcActions.CreateShutdownStartInfo(action);
        Assert.Equal(Path.Combine(Environment.SystemDirectory, "shutdown.exe"), info.FileName);
        Assert.Equal(new[] { flag, "/t", "0" }, info.ArgumentList);
        Assert.False(info.UseShellExecute);
        Assert.True(info.CreateNoWindow);
    }

    [Fact]
    public void ConfiguredLaunchPreservesEachArgumentWithoutShellParsing()
    {
        var arguments = new[] { "C:\\Folder with spaces\\note.txt", "literal \"quotes\"", "& not-a-shell-command", "" };
        var info = WindowsPcActions.CreateActionStartInfo(StageThreeTests.ExampleAction() with { Arguments = arguments });
        Assert.False(info.UseShellExecute);
        Assert.True(info.CreateNoWindow);
        Assert.Equal(arguments, info.ArgumentList);
        Assert.Equal(Environment.SystemDirectory, info.WorkingDirectory);
    }

    [Fact]
    public async Task NativeLauncherCanStartAHarmlessProcessAndReportsMissingFiles()
    {
        var action = StageThreeTests.ExampleAction() with
        {
            Executable = Path.Combine(Environment.SystemDirectory, "cmd.exe"), Arguments = ["/d", "/c", "exit", "0"]
        };
        var executor = new WindowsPcActions();
        var pid = await executor.LaunchAsync(action, CancellationToken.None);
        Assert.True(pid > 0);
        await Assert.ThrowsAsync<FileNotFoundException>(() => executor.LaunchAsync(action with
        {
            Executable = Path.Combine(Path.GetTempPath(), $"nonexistent-{Guid.NewGuid():N}.exe")
        }, CancellationToken.None));
    }

    [Theory]
    [InlineData(MediaAction.PlayPause, 0xB3)]
    [InlineData(MediaAction.Next, 0xB0)]
    [InlineData(MediaAction.Previous, 0xB1)]
    public void MediaInputHasNativeLayoutAndPairedKeyEvents(MediaAction action, int key)
    {
        Assert.Equal(IntPtr.Size == 8 ? 40 : 28, Marshal.SizeOf<WindowsMedia.Input>());
        var inputs = WindowsMedia.CreateInputs(action);
        Assert.Equal(2, inputs.Length);
        Assert.All(inputs, input =>
        {
            Assert.Equal(1u, input.Type);
            Assert.Equal((ushort)key, input.Data.Keyboard.VirtualKey);
        });
        Assert.Equal(1u, inputs[0].Data.Keyboard.Flags);
        Assert.Equal(3u, inputs[1].Data.Keyboard.Flags);
    }

    [Fact]
    public async Task ReadsNativeAudioStateWithoutChangingVolumeOrPlayback()
    {
        var completion = new TaskCompletionSource<AudioState?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try { completion.SetResult(WindowsAudio.GetState()); }
            catch (COMException exception) when (exception.HResult == unchecked((int)0x80070490))
            {
                // A headless Windows test machine may have no default output endpoint.
                completion.SetResult(null);
            }
            catch (Exception exception) { completion.SetException(exception); }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.IsBackground = true;
        thread.Start();
        var state = await completion.Task.WaitAsync(TimeSpan.FromSeconds(10));
        if (state is null) output.WriteLine("Windows has no default playback endpoint; device-unavailable handling is covered by broker tests.");
        else
        {
            Assert.InRange(state.Volume, 0, 100);
            output.WriteLine($"Read native Windows audio: volume {state.Volume}%, muted {state.Muted}.");
        }
    }

    [Fact]
    public void CredentialManagerRoundTripsAndRemovesOnlyTheTestCredential()
    {
        var id = $"integration-test-{Guid.NewGuid():N}";
        try
        {
            Assert.Equal("", CredentialStore.Read(id));
            CredentialStore.Write(id, "test-user", "test-password-ä-🔑");
            Assert.Equal("test-password-ä-🔑", CredentialStore.Read(id));
            CredentialStore.Write(id, "test-user", "updated-test-password");
            Assert.Equal("updated-test-password", CredentialStore.Read(id));
        }
        finally { CredentialStore.Write(id, "test-user", ""); }
        Assert.Equal("", CredentialStore.Read(id));
    }

    [Fact]
    public async Task SettingsScreenRendersWithoutConnectingOrSaving()
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                using var form = new SettingsForm(new AppSettings
                {
                    BrokerHost = "mqtt.home.lan", Username = "desktop-pc", DeviceId = "desktop-demo",
                    ComputerName = "Desktop PC"
                }, "", (_, _) => throw new InvalidOperationException("A render must not save settings."));
                form.SetStatus(new ConnectionStatus(false, "Disconnected — enter your broker details and click Save and connect."));
                form.SetAudioState(new AudioState(35, false));
                // A form must be visible to WinForms for its child controls to render.
                // Zero opacity and no taskbar entry keep this test off the user's desktop.
                form.Opacity = 0;
                form.ShowInTaskbar = false;
                form.Show();
                form.PerformLayout();
                var saveButton = FindControls(form).OfType<Button>().Single(button => button.Text == "Save and connect");
                Assert.True(saveButton.Visible);
                var saveLocation = form.PointToClient(saveButton.PointToScreen(Point.Empty));
                Assert.True(saveLocation.Y + saveButton.Height <= form.ClientSize.Height);
                Assert.True(saveLocation.X + saveButton.Width <= form.ClientSize.Width);
                using var bitmap = new Bitmap(form.Width, form.Height);
                form.DrawToBitmap(bitmap, new Rectangle(Point.Empty, bitmap.Size));
                var projectDirectory = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
                var artifactDirectory = Path.Combine(projectDirectory, "artifacts", "ui");
                Directory.CreateDirectory(artifactDirectory);
                bitmap.Save(Path.Combine(artifactDirectory, "settings-preview.png"));
                var tabs = FindControls(form).OfType<TabControl>().Single();
                tabs.SelectedIndex = 1;
                form.PerformLayout();
                form.DrawToBitmap(bitmap, new Rectangle(Point.Empty, bitmap.Size));
                bitmap.Save(Path.Combine(artifactDirectory, "power-actions-preview.png"));
                Assert.Equal(4, FindControls(tabs.TabPages[1]).OfType<CheckBox>().Count());

                using var editor = new ActionEditorForm(StageThreeTests.ExampleAction(), _ => true)
                {
                    Opacity = 0, ShowInTaskbar = false
                };
                editor.Show();
                editor.PerformLayout();
                using var editorBitmap = new Bitmap(editor.Width, editor.Height);
                editor.DrawToBitmap(editorBitmap, new Rectangle(Point.Empty, editorBitmap.Size));
                editorBitmap.Save(Path.Combine(artifactDirectory, "action-editor-preview.png"));
                var saveAction = FindControls(editor).OfType<Button>().Single(button => button.Text == "Save action");
                var editorSave = editor.PointToClient(saveAction.PointToScreen(Point.Empty));
                Assert.True(editorSave.Y + saveAction.Height <= editor.ClientSize.Height);
                saveAction.PerformClick();
                Assert.Equal(DialogResult.OK, editor.DialogResult);
                Assert.Equivalent(StageThreeTests.ExampleAction(), editor.Action, strict: true);
                Assert.True(form.Controls.Count > 0);
                completion.SetResult();
            }
            catch (Exception exception) { completion.SetException(exception); }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.IsBackground = true;
        thread.Start();
        await completion.Task.WaitAsync(TimeSpan.FromSeconds(10));
    }

    private static IEnumerable<Control> FindControls(Control parent)
    {
        foreach (Control child in parent.Controls)
        {
            yield return child;
            foreach (var descendant in FindControls(child)) yield return descendant;
        }
    }
}
