namespace Dito.Linux.Tests;

public sealed class DesktopInstanceTests
{
    [Fact]
    public async Task SecondLaunchActivatesTheInstanceForTheSameDisplay()
    {
        var runtime = Directory.CreateTempSubdirectory("dito-test-");
        try
        {
            using (var first = Assert.IsType<DesktopInstance>(await DesktopInstance.OpenAsync(runtime.FullName, ":42.0")))
            {
                var shown = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                first.ReceiveActivations(() => shown.TrySetResult(), () =>
                    shown.TrySetException(new IOException("Activation listener failed.")));

                Assert.Null(await DesktopInstance.OpenAsync(runtime.FullName, ":42.1"));
                await shown.Task.WaitAsync(TimeSpan.FromSeconds(3));
                using var otherDisplay = Assert.IsType<DesktopInstance>(await DesktopInstance.OpenAsync(runtime.FullName, ":43"));
            }

            using var restarted = Assert.IsType<DesktopInstance>(await DesktopInstance.OpenAsync(runtime.FullName, ":42"));
        }
        finally
        {
            runtime.Delete(recursive: true);
        }
    }
}
