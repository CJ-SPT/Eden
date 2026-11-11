using System.Diagnostics;
using Eden.Protocol.PilotSessions;
using Eden.Protocol.Serialization;

namespace Eden.Protocol.Tests.PilotSessions;

public sealed class FreePointClientTests
{
    [Fact]
    public Task FreePointStartupResolvesTheBuiltinManagerWithoutClientFiles() =>
        CheckAdapter("free_points_builtin_test.py", ["Built-in free-point startup assertions passed"]);

    [PinnedPortraitClientFact]
    public Task NativeFreePointPreviewAndNeocomStartupUseBuiltinServices() =>
        CheckAdapter(
            "free_points_client_test.py",
            ["Native free-point preview assertions passed", "Native Neocom skill queue startup assertions passed"],
            Environment.GetEnvironmentVariable("EDEN_TEST_CLIENT_ROOT")
        );

    private static async Task CheckAdapter(string script, IReadOnlyList<string> expectedOutput, string? clientRoot = null)
    {
        string path = Path.Combine(Path.GetTempPath(), "eden-free-points-" + Guid.NewGuid().ToString("N"));
        try
        {
            var expression = ClientExpression.Create("eden-free-points-test", "Eden.Protocol.Skills.free_points_client.py");
            await File.WriteAllBytesAsync(path, BlueRead.Bytes(BlueMarshal.Decode(expression)));
            ProcessStartInfo start = new("python") { RedirectStandardOutput = true, RedirectStandardError = true };
            start.ArgumentList.Add(Path.Combine(AppContext.BaseDirectory, "portrait-client-tests", script));
            start.ArgumentList.Add(path);
            if (clientRoot is not null)
            {
                start.ArgumentList.Add(clientRoot);
            }
            using var process = Process.Start(start)!;
            Task<string> output = process.StandardOutput.ReadToEndAsync();
            Task<string> error = process.StandardError.ReadToEndAsync();
            using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(30));
            await process.WaitForExitAsync(timeout.Token);
            Assert.True(process.ExitCode == 0, await output + await error);
            foreach (string expected in expectedOutput)
            {
                Assert.Contains(expected, await output);
            }
        }
        finally
        {
            File.Delete(path);
        }
    }
}
