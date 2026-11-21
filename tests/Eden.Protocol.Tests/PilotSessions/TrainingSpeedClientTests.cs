using System.Diagnostics;
using System.Globalization;
using System.Text;
using Eden.Protocol.PilotSessions;
using Eden.Protocol.Serialization;
using Eden.Protocol.Skills;

namespace Eden.Protocol.Tests.PilotSessions;

public sealed class TrainingSpeedClientTests
{
    [Fact]
    public async Task SignedAdapterScalesEstimatesAndKeepsSavedQueueProgressAcrossRateChanges()
    {
        var directory = TestDirectory.Create("eden-training-speed-client-");
        try
        {
            ProcessStartInfo start = new("python") { RedirectStandardOutput = true, RedirectStandardError = true };
            start.ArgumentList.Add(Path.Combine(AppContext.BaseDirectory, "portrait-client-tests", "training_speed_client_test.py"));
            foreach (decimal multiplier in new[] { 2.5m, .1m, 1m })
            {
                string path = Path.Combine(directory.FullName, multiplier.ToString(CultureInfo.InvariantCulture));
                var expression = ClientExpression.CreateWithPrelude(
                    "eden-training-speed-test",
                    LoginClient.TrainingPrelude(multiplier),
                    "Eden.Protocol.Skills.training_speed_client.py"
                );
                await File.WriteAllBytesAsync(path, BlueRead.Bytes(BlueMarshal.Decode(expression)));
                start.ArgumentList.Add(path);
                string signed = Encoding.ASCII.GetString(BlueRead.Bytes(BlueMarshal.Decode(LoginClient.For(new(), multiplier))));
                Assert.Contains($"eden_training_speed = {multiplier.ToString(CultureInfo.InvariantCulture)}", signed);
            }
            using var process = Process.Start(start)!;
            var output = process.StandardOutput.ReadToEndAsync();
            var error = process.StandardError.ReadToEndAsync();
            using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(30));
            await process.WaitForExitAsync(timeout.Token);
            Assert.True(process.ExitCode == 0, await output + await error);
            Assert.Contains("Training speed adapter assertions passed", await output);
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    [Fact]
    public void QueueWireCarriesSavedTrainingRatePeriodsWithDecimalPrecision()
    {
        SkillQueueRow row = new(0, new(new(3327, 1), 50, 250, 1000, 9000) { Periods = [new(1000, 9000, 50.625m, 37.5m)] });
        var decoded = Assert.IsType<BlueInstance>(BlueMarshal.Decode(BlueMarshal.Encode(row)));
        var properties = Assert.IsType<Dictionary<string, object?>>(decoded.State);
        Assert.Equal(9000L, properties["trainingEndTime"]);
        var periods = Assert.IsType<List<object?>>(properties["edenTrainingPeriods"]);
        var period = Assert.IsType<object?[]>(Assert.Single(periods));
        Assert.Equal(1000L, period[0]);
        Assert.Equal("50.625", BlueMarshal.Text(period[1]));
        Assert.Equal("37.5", BlueMarshal.Text(period[2]));
    }
}
