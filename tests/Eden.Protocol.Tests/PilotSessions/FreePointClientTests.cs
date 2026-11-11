using System.Diagnostics;
using Eden.Protocol.PilotSessions;
using Eden.Protocol.Serialization;

namespace Eden.Protocol.Tests.PilotSessions;

public sealed class FreePointClientTests
{
    [PinnedPortraitClientFact]
    public async Task NativeFreePointPreviewUsesCurrentProgressAndPassiveRewardsStaySilent()
    {
        string path = Path.Combine(Path.GetTempPath(), "eden-free-points-" + Guid.NewGuid().ToString("N"));
        try
        {
            var expression = ClientExpression.Create("eden-free-points-test", "Eden.Protocol.Skills.free_points_client.py");
            await File.WriteAllBytesAsync(path, BlueRead.Bytes(BlueMarshal.Decode(expression)));
            ProcessStartInfo start = new("python") { RedirectStandardOutput = true, RedirectStandardError = true };
            start.ArgumentList.Add(Path.Combine(AppContext.BaseDirectory, "portrait-client-tests", "free_points_client_test.py"));
            start.ArgumentList.Add(path);
            start.ArgumentList.Add(Environment.GetEnvironmentVariable("EDEN_TEST_CLIENT_ROOT")!);
            using var process = Process.Start(start)!;
            Task<string> output = process.StandardOutput.ReadToEndAsync();
            Task<string> error = process.StandardError.ReadToEndAsync();
            using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(30));
            await process.WaitForExitAsync(timeout.Token);
            Assert.True(process.ExitCode == 0, await output + await error);
            Assert.Contains("Native free-point preview assertions passed", await output);
        }
        finally
        {
            File.Delete(path);
        }
    }
}
