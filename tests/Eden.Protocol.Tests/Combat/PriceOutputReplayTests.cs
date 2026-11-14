using System.Diagnostics;
using System.IO.Compression;
using System.Security.Cryptography;
using Eden.Infrastructure.Diagnostics;
using Eden.Protocol.Industry;
using Eden.Protocol.PilotSessions;
using Eden.Protocol.Serialization;
using Eden.Protocol.Transport;
using Eden.Server;
using Eden.World.Storage;
using Microsoft.Extensions.DependencyInjection;

namespace Eden.Protocol.Tests.Combat;

public sealed partial class ServerPerformanceReplayTests
{
    [Fact]
    public async Task ReplaySavedAveragePriceOutput()
    {
        string? source = Environment.GetEnvironmentVariable("EDEN_PERFORMANCE_SOURCE");
        if (source is null)
        {
            output.WriteLine("Set EDEN_PERFORMANCE_SOURCE to a frozen world and catalog to run the price output replay.");
            return;
        }
        string directory = CopySnapshot(source);
        try
        {
            CopyReplaySidecars(source, directory);
            WorldDatabase database = new(Path.Combine(directory, "world.sqlite"));
            WorldInitialization.Initialize(database);
            using var host = ServerServices.Build(database);
            var prices = host.GetRequiredService<EconomicPriceCache>();
            prices.Warm();
            var reply = prices.ReadReply();
            var request = IncomingMachoPacket.Read(
                Eden.Protocol.Tests.PilotSessions.PilotServicesTests.Request("config", "GetAverageMarketPrices", [])
            );
            var packet = request.CallSucceeded(reply);
            byte[] expected = BlueMarshal.Encode(packet);
            using Aes aes = Aes.Create();
            using MemoryStream stream = new();
            EncryptedPackets packets = new(stream, aes, default);
            List<string> reports = [];
            foreach (var level in new[] { CompressionLevel.Fastest, CompressionLevel.Optimal, CompressionLevel.SmallestSize })
            {
                using MemoryStream compressed = new();
                long started = Stopwatch.GetTimestamp();
                using (ZLibStream compressor = new(compressed, level, leaveOpen: true))
                {
                    compressor.Write(expected);
                }
                reports.Add(
                    $"compression={level} elapsed={Stopwatch.GetElapsedTime(started).TotalMilliseconds:F2} ms bytes={compressed.Length}"
                );
            }
            for (int sample = 0; sample < 10; sample++)
            {
                stream.SetLength(0);
                long started = Stopwatch.GetTimestamp();
                using (SessionPerformance.Begin("price output", _ => { }, force: true))
                using (new SessionLatency($"price output {sample}", reports.Add, reports.Add))
                {
                    await packets.WriteAsync(packet);
                }
                reports.Add($"sample={sample} output={Stopwatch.GetElapsedTime(started).TotalMilliseconds:F2} ms frame={stream.Length}");
                stream.Position = 0;
                byte[] ciphertext = await PacketStream.ReadAsync(stream, default, EncryptedPackets.MaximumOutgoingCiphertextBytes);
                byte[] compressed = aes.DecryptCbc(ciphertext, aes.IV);
                using ZLibStream inflater = new(new MemoryStream(compressed), CompressionMode.Decompress);
                using MemoryStream decoded = new();
                await inflater.CopyToAsync(decoded);
                Assert.Equal(expected, decoded.ToArray());
            }
            reports.Add($"inner={reply.EncodeReply().Length} envelope={expected.Length}");
            if (Environment.GetEnvironmentVariable("EDEN_PERFORMANCE_OUTPUT") is { } destination)
            {
                Directory.CreateDirectory(destination);
                File.WriteAllLines(Path.Combine(destination, "price-output.log"), reports);
            }
            foreach (string report in reports)
            {
                output.WriteLine(report);
            }
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}
