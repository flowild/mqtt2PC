using Xunit;

namespace Mqtt2Pc.Tests;

public sealed class StartupTests
{
    [Fact]
    public async Task StartupShortcutRoundTripsInAnIsolatedFolderAndDoesNotRemoveAnotherCopy()
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            var directory = Path.Combine(Path.GetTempPath(), $"mqtt2pc-startup-{Guid.NewGuid():N}");
            try
            {
                var executable = Path.Combine(AppContext.BaseDirectory, "mqtt2PC.exe");
                var registration = new StartupRegistration(executable, directory);
                Assert.False(registration.IsEnabled);
                registration.SetEnabled(true);
                Assert.True(registration.IsEnabled);
                Assert.Equal((Path.GetFullPath(executable), "--startup"), WindowsShortcut.Read(registration.ShortcutPath));
                registration.SetEnabled(true);
                Assert.Single(Directory.GetFiles(directory));
                var other = new StartupRegistration(Path.Combine(directory, "other-copy.exe"), directory);
                Assert.False(other.IsEnabled);
                other.SetEnabled(false);
                Assert.True(registration.IsEnabled);
                registration.SetEnabled(false);
                Assert.False(registration.IsEnabled);
                Assert.Empty(Directory.GetFiles(directory));
                completion.SetResult();
            }
            catch (Exception exception) { completion.SetException(exception); }
            finally
            {
                if (Directory.Exists(directory))
                {
                    foreach (var file in Directory.GetFiles(directory)) File.Delete(file);
                    Directory.Delete(directory);
                }
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.IsBackground = true;
        thread.Start();
        await completion.Task.WaitAsync(TimeSpan.FromSeconds(10));
    }
}
