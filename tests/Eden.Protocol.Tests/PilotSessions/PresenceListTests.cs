using System.Diagnostics;
using Eden.Protocol.PilotSessions;
using Eden.Protocol.Serialization;

namespace Eden.Protocol.Tests.PilotSessions;

public sealed class PresenceListTests
{
    [PinnedPortraitClientFact]
    public async Task NativePresenceRebuildsPreserveConcurrentArrivalsDeparturesAndRowIndexes()
    {
        string path = Path.Combine(Path.GetTempPath(), "eden-presence-expression-" + Guid.NewGuid().ToString("N"));
        try
        {
            var expression = ClientExpression.Create("eden-presence-test", "Eden.Protocol.Docking.presence_lists_client.py");
            await File.WriteAllBytesAsync(path, BlueRead.Bytes(BlueMarshal.Decode(expression)));
            ProcessStartInfo start = new("python") { RedirectStandardOutput = true, RedirectStandardError = true };
            start.ArgumentList.Add(Path.Combine(AppContext.BaseDirectory, "portrait-client-tests", "presence_lists_client_test.py"));
            start.ArgumentList.Add(path);
            start.ArgumentList.Add(Environment.GetEnvironmentVariable("EDEN_TEST_CLIENT_ROOT")!);
            using var process = Process.Start(start)!;
            Task<string> output = process.StandardOutput.ReadToEndAsync();
            Task<string> error = process.StandardError.ReadToEndAsync();
            using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(30));
            await process.WaitForExitAsync(timeout.Token);
            Assert.True(process.ExitCode == 0, await output + await error);
            Assert.Contains("Native presence list assertions passed", await output);
        }
        finally
        {
            File.Delete(path);
        }
    }
}
