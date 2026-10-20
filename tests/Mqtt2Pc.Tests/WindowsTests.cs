using Mqtt2Pc.Core;
using Xunit;

namespace Mqtt2Pc.Tests;

public sealed class WindowsTests
{
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
