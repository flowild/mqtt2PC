using Mqtt2Pc.Core;
using Xunit;

namespace Mqtt2Pc.Tests;

public sealed class BatchActionTests
{
    [Fact]
    public void BatchStartInfoUsesWindowsInterpreterAndScriptWorkingDirectory()
    {
        var action = StageThreeTests.ExampleAction() with
        {
            Executable = "C:\\Folder with spaces\\run.BAT", Arguments = ["first argument", ""]
        };
        var info = WindowsPcActions.CreateActionStartInfo(action);
        Assert.Equal(Path.Combine(Environment.SystemDirectory, "cmd.exe"), info.FileName);
        Assert.Equal("C:\\Folder with spaces", info.WorkingDirectory);
        Assert.False(info.UseShellExecute);
        Assert.True(info.CreateNoWindow);
        Assert.StartsWith("/d /s /v:off /c \"", info.Arguments);
        Assert.Empty(info.ArgumentList);
        Assert.Equal(action.Executable, info.Environment["MQTT2PC_BATCH_FILE"]);
        Assert.Equal("first argument", info.Environment["MQTT2PC_BATCH_ARG_0"]);
        Assert.EndsWith("\"\"\"", info.Arguments);
    }

    [Fact]
    public void RejectsBatchCommandsBeyondWindowsCommandLengthLimit()
    {
        var action = StageThreeTests.ExampleAction() with
        {
            Executable = "C:\\scripts\\run.bat", Arguments = Enumerable.Repeat(new string('x', 2000), 4).ToArray()
        };
        Assert.Throws<ArgumentException>(action.Validate);
    }

    [Fact]
    public async Task MissingBatchFileIsReportedBeforeStartingTheInterpreter()
    {
        var action = StageThreeTests.ExampleAction() with
        {
            Executable = Path.Combine(Path.GetTempPath(), $"nonexistent-{Guid.NewGuid():N}.bat"), Arguments = []
        };
        await Assert.ThrowsAsync<FileNotFoundException>(() => new WindowsPcActions().LaunchAsync(action, CancellationToken.None));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExecutesBatchWithSpacesLiteralArgumentsAndOptionalWorkingDirectory(bool customWorkingDirectory)
    {
        var workspace = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
        var root = Path.Combine(workspace, "artifacts", "batch-tests", Guid.NewGuid().ToString("N"));
        var scriptDirectory = Path.Combine(root, "Scripts & %PATH% !keep! with spaces");
        var workingDirectory = customWorkingDirectory ? Path.Combine(root, "Working folder") : scriptDirectory;
        var scriptPath = Path.Combine(scriptDirectory, "test action.bat");
        var resultPath = Path.Combine(workingDirectory, "received.txt");
        Directory.CreateDirectory(scriptDirectory);
        Directory.CreateDirectory(workingDirectory);
        try
        {
            await File.WriteAllTextAsync(scriptPath,
                "@echo off\r\nsetlocal DisableDelayedExpansion\r\n" +
                "set \"MQTT2PC_TEST_ARG_1=[%~1]\"\r\n" +
                "set \"MQTT2PC_TEST_ARG_2=[%~2]\"\r\n" +
                "set \"MQTT2PC_TEST_ARG_3=[%~3]\"\r\n" +
                "set \"MQTT2PC_TEST_ARG_4=[%~4]\"\r\n" +
                "> \"received.txt\" set MQTT2PC_TEST_ARG_\r\n");
            var arguments = new[] { "first argument", "", "A&B (x) !keep! %PATH%", "C:\\trailing\\" };
            var action = StageThreeTests.ExampleAction() with
            {
                Executable = scriptPath, Arguments = arguments,
                WorkingDirectory = customWorkingDirectory ? workingDirectory : ""
            };
            Assert.True(await new WindowsPcActions().LaunchAsync(action, CancellationToken.None) > 0);
            string[]? received = null;
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
            while (!timeout.IsCancellationRequested)
            {
                try
                {
                    if (File.Exists(resultPath))
                    {
                        var lines = await File.ReadAllLinesAsync(resultPath, timeout.Token);
                        if (lines.Length == arguments.Length) { received = lines; break; }
                    }
                }
                catch (IOException) { }
                await Task.Delay(25, timeout.Token);
            }
            Assert.NotNull(received);
            Assert.Equal(arguments.Select((argument, index) => $"MQTT2PC_TEST_ARG_{index + 1}=[{argument}]"), received);
        }
        finally
        {
            if (File.Exists(resultPath)) File.Delete(resultPath);
            if (File.Exists(scriptPath)) File.Delete(scriptPath);
            if (customWorkingDirectory) Directory.Delete(workingDirectory);
            Directory.Delete(scriptDirectory);
            Directory.Delete(root);
        }
    }
}
