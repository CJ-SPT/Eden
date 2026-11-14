using System.Diagnostics;
using System.Text;
using Eden.Protocol.PilotSessions;
using Eden.Protocol.Serialization;

namespace Eden.Protocol.Tests.PilotSessions;

public sealed class CorporationClientDiagnosticsTests
{
    [Fact]
    public async Task DiagnosticPreservesResultsAndExceptionsAndBoundsLocalOutput()
    {
        string directory = Path.Combine(Path.GetTempPath(), "eden-corp-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            string expressionPath = Path.Combine(directory, "expression.marshal");
            byte[] expression = ClientExpression.Create(
                "eden-corporation-test",
                "Eden.Protocol.Corporations.corporation_diagnostics_client.py"
            );
            await File.WriteAllBytesAsync(expressionPath, BlueRead.Bytes(BlueMarshal.Decode(expression)));
            ProcessStartInfo start = new("python") { RedirectStandardOutput = true, RedirectStandardError = true };
            start.ArgumentList.Add(
                Path.Combine(AppContext.BaseDirectory, "corporation-client-tests", "corporation_diagnostics_client_test.py")
            );
            start.ArgumentList.Add(expressionPath);
            start.ArgumentList.Add(directory);
            using var process = Process.Start(start)!;
            Task<string> output = process.StandardOutput.ReadToEndAsync();
            Task<string> error = process.StandardError.ReadToEndAsync();
            using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(30));
            await process.WaitForExitAsync(timeout.Token);
            Assert.True(process.ExitCode == 0, await output + await error);
            Assert.Contains("Corporation diagnostic assertions passed", await output);
            string login = Encoding.ASCII.GetString(BlueRead.Bytes(BlueMarshal.Decode(LoginClient.Expression)));
            Assert.Contains("install_corporation_diagnostics()", login);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}
