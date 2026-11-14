using System.Diagnostics;
using System.Globalization;
using System.Text;
using Eden.Gameplay.Configuration;
using Eden.Protocol.PilotSessions;
using Eden.Protocol.Serialization;

namespace Eden.Protocol.Tests.Planetary;

public sealed class PlanetaryClientTests
{
    [Fact]
    public async Task SignedAdapterScalesFittingAndProductsWithoutCompoundingAndRestoresDefaults()
    {
        string directory = Path.Combine(Path.GetTempPath(), "eden-planetary-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            ProcessStartInfo start = new("python") { RedirectStandardOutput = true, RedirectStandardError = true };
            start.ArgumentList.Add(Path.Combine(AppContext.BaseDirectory, "planetary-client-tests", "planetary_client_test.py"));
            PlanetaryPolicy[] policies =
            [
                new(CpuMultiplier: 2, PowergridMultiplier: 3, FactoryOutputMultiplier: 2.5m),
                new(FactoryOutputMultiplier: .1m),
                new(),
            ];
            foreach (var policy in policies)
            {
                string path = Path.Combine(directory, start.ArgumentList.Count.ToString(CultureInfo.InvariantCulture));
                var expression = ClientExpression.CreateWithPrelude(
                    "eden-planetary-test",
                    LoginClient.PlanetaryPrelude(policy),
                    "Eden.Protocol.Planetary.planetary_client.py"
                );
                await File.WriteAllBytesAsync(path, BlueRead.Bytes(BlueMarshal.Decode(expression)));
                start.ArgumentList.Add(path);
            }
            using var process = Process.Start(start)!;
            Task<string> output = process.StandardOutput.ReadToEndAsync();
            Task<string> error = process.StandardError.ReadToEndAsync();
            using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(30));
            await process.WaitForExitAsync(timeout.Token);
            Assert.True(process.ExitCode == 0, await output + await error);
            Assert.Contains("Planetary adapter assertions passed", await output);
            string signed = Encoding.ASCII.GetString(BlueRead.Bytes(BlueMarshal.Decode(LoginClient.For(policies[0]))));
            Assert.Contains("install_planetary_tuning()", signed);
            Assert.Contains("2.5", signed);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}
