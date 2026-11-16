using System.Diagnostics;
using Eden.Protocol.PilotSessions;
using Eden.Protocol.Serialization;

namespace Eden.Protocol.Tests.PilotSessions;

public sealed class CloneLicenseClientTests
{
    [Fact]
    public async Task NativeOmegaLicenseUsesAuthenticatedCloneGradeWithoutGrantingOtherLicenses()
    {
        string path = Path.Combine(Path.GetTempPath(), "eden-clone-license-" + Guid.NewGuid().ToString("N"));
        try
        {
            byte[] expression = ClientExpression.Create("eden-clone-license-test", "Eden.Protocol.Skills.clone_license_client.py");
            await File.WriteAllBytesAsync(path, BlueRead.Bytes(BlueMarshal.Decode(expression)));
            ProcessStartInfo start = new("python") { RedirectStandardOutput = true, RedirectStandardError = true };
            start.ArgumentList.Add(Path.Combine(AppContext.BaseDirectory, "clone-license-client-tests", "clone_license_client_test.py"));
            start.ArgumentList.Add(path);
            using var process = Process.Start(start)!;
            Task<string> output = process.StandardOutput.ReadToEndAsync();
            Task<string> error = process.StandardError.ReadToEndAsync();
            using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(30));
            await process.WaitForExitAsync(timeout.Token);
            Assert.True(process.ExitCode == 0, await output + await error);
            Assert.Contains("Saved clone license assertions passed", await output);
        }
        finally
        {
            File.Delete(path);
        }
    }
}
