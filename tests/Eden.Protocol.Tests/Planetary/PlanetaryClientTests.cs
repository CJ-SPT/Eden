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
    public async Task CustomsRefreshPreservesPendingTransfersAndPreventsReentrantConfirmation()
    {
        string path = Path.Combine(Path.GetTempPath(), "eden-customs-" + Guid.NewGuid().ToString("N"));
        try
        {
            var expression = ClientExpression.CreateWithPrelude(
                "eden-customs-test",
                "",
                "Eden.Protocol.Planetary.customs_transfer_client.py"
            );
            await File.WriteAllBytesAsync(path, BlueRead.Bytes(BlueMarshal.Decode(expression)));
            ProcessStartInfo start = new("python") { RedirectStandardOutput = true, RedirectStandardError = true };
            start.ArgumentList.Add(Path.Combine(AppContext.BaseDirectory, "planetary-client-tests", "customs_transfer_client_test.py"));
            start.ArgumentList.Add(path);
            using var process = Process.Start(start)!;
            Task<string> output = process.StandardOutput.ReadToEndAsync();
            Task<string> error = process.StandardError.ReadToEndAsync();
            using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(30));
            await process.WaitForExitAsync(timeout.Token);
            Assert.True(process.ExitCode == 0, await output + await error);
            Assert.Contains("Customs transfer assertions passed", await output);
            foreach (var policy in new PlanetaryPolicy[] { new(), new(StorageCapacityMultiplier: 100) })
            {
                string signed = Encoding.ASCII.GetString(BlueRead.Bytes(BlueMarshal.Decode(LoginClient.For(policy))));
                Assert.Contains("install_planetary_transfer_refresh()", signed);
            }
        }
        finally
        {
            File.Delete(path);
        }
    }

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
                new(CpuMultiplier: 2, PowergridMultiplier: 3, FactoryOutputMultiplier: 2.5m, MaximumPlanets: 8),
                new(FactoryOutputMultiplier: .1m, MaximumPlanets: 1),
                new(),
                new(MaximumPlanets: 12),
                new(CpuMultiplier: 100, PowergridMultiplier: 100, FactoryOutputMultiplier: 100, MaximumPlanets: 100),
                new(ExtractionYieldMultiplier: 100),
                new(StorageCapacityMultiplier: 10, FactorySpeedMultiplier: 10),
                new(StorageCapacityMultiplier: .1m, FactorySpeedMultiplier: 100),
                new(StorageCapacityMultiplier: 100, FactorySpeedMultiplier: .1m),
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
