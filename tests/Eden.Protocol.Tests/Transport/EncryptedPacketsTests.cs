using System.IO.Compression;
using System.Security.Cryptography;
using Eden.Gameplay.Market;
using Eden.Infrastructure.Diagnostics;
using Eden.Protocol.Industry;
using Eden.Protocol.Market;
using Eden.Protocol.PilotSessions;
using Eden.Protocol.Serialization;
using Eden.Protocol.Transport;

namespace Eden.Protocol.Tests.Transport;

public sealed class EncryptedPacketsTests
{
    [Fact]
    public async Task LargeAveragePriceRepliesPreserveTheirEnvelopeAndUseOneCompactFrameWrite()
    {
        var prices = Enumerable.Range(1, 20_000).Select(id => new MarketValuation(id, id * 1.23m, id * 0.45m)).ToArray();
        EconomicPricesReply reply = new(prices);
        var request = IncomingMachoPacket.Read(
            Eden.Protocol.Tests.PilotSessions.PilotServicesTests.Request("config", "GetAverageMarketPrices", [])
        );
        var packet = request.CallSucceeded(reply);
        byte[] expected = BlueMarshal.Encode(packet);
        Assert.True(expected.Length > EncryptedPackets.CompactCompressionThreshold);
        using MemoryStream fast = new();
        using (ZLibStream compressor = new(fast, CompressionLevel.Fastest, leaveOpen: true))
        {
            compressor.Write(expected);
        }
        using Aes aes = Aes.Create();
        using CountingStream stream = new();
        List<string> reports = [];
        using (SessionPerformance.Begin("price output", _ => { }, force: true))
        using (new SessionLatency("price output", reports.Add, reports.Add))
        {
            await new EncryptedPackets(stream, aes, default).WriteAsync(packet);
        }
        Assert.Equal(1, stream.Writes);
        Assert.True(stream.Length < fast.Length);
        string report = Assert.Single(reports);
        foreach (string stage in new[] { "OutputEncode", "OutputCompress", "OutputEncrypt", "OutputWrite" })
        {
            Assert.Contains(stage + "=", report);
        }
        stream.Position = 0;
        byte[] ciphertext = await PacketStream.ReadAsync(stream, default, EncryptedPackets.MaximumOutgoingCiphertextBytes);
        using ZLibStream inflater = new(new MemoryStream(aes.DecryptCbc(ciphertext, aes.IV)), CompressionMode.Decompress);
        using MemoryStream decoded = new();
        await inflater.CopyToAsync(decoded);
        Assert.Equal(expected, decoded.ToArray());
        var envelope = IncomingMachoPacket.Read(BlueMarshal.Decode(decoded.ToArray()));
        Assert.Equal(reply.EncodeReply(), BlueRead.Bytes(BlueRead.Tuple(envelope.Fields[4], 1)[0]));
    }

    private sealed class CountingStream : MemoryStream
    {
        internal int Writes { get; private set; }

        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            Writes++;
            return base.WriteAsync(buffer, cancellationToken);
        }
    }

    [Fact]
    public async Task IncompressibleRepliesKeepTheirOriginalPayloadAcrossRepeatedPoolReuse()
    {
        byte[] data = new byte[2 * PacketStream.MaxSessionBytes];
        new Random(42).NextBytes(data);
        using Aes aes = Aes.Create();
        using MemoryStream stream = new();
        EncryptedPackets packets = new(stream, aes, default);
        for (int index = 0; index < 8; index++)
        {
            stream.SetLength(0);
            await packets.WriteAsync(data);
            stream.Position = 0;
            byte[] ciphertext = await PacketStream.ReadAsync(stream, default, EncryptedPackets.MaximumOutgoingCiphertextBytes);
            Assert.Equal(BlueMarshal.Encode(data), aes.DecryptCbc(ciphertext, aes.IV));
        }
    }

    [Theory]
    [InlineData(4096)]
    [InlineData(EncryptedPackets.CompressionThreshold * 2)]
    public async Task PooledCiphertextStaysOwnedUntilTheAsynchronousWriteCompletes(int length)
    {
        using Aes aes = Aes.Create();
        using DelayedStream stream = new();
        EncryptedPackets packets = new(stream, aes, default);
        var value = new BlueUnicode(new string('a', length));
        Task pending = packets.WriteAsync(value);
        await stream.Writing.Task.WaitAsync(TimeSpan.FromSeconds(10));
        try
        {
            using MemoryStream other = new();
            for (int index = 0; index < 16; index++)
            {
                await new EncryptedPackets(other, aes, default).WriteAsync(new BlueUnicode(new string('z', length)));
            }
            Assert.False(pending.IsCompleted);
        }
        finally
        {
            stream.Release.TrySetResult();
        }
        await pending;
        stream.Position = 0;
        Assert.Equal(value.Value, await packets.ReadAsync());
    }

    private sealed class DelayedStream : MemoryStream
    {
        internal TaskCompletionSource Writing { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (buffer.Length > sizeof(uint))
            {
                Writing.TrySetResult();
                await Release.Task.WaitAsync(cancellationToken);
            }
            await base.WriteAsync(buffer, cancellationToken);
        }
    }

    [Fact]
    public async Task FullCatalogSummaryCompressesIntoOneEncryptedFrameWithoutChangingNativeRows()
    {
        var rows = Enumerable
            .Range(1, 19_727)
            .ToDictionary(
                id => (long)id,
                id => new MarketOrderRow(
                    new(
                        id,
                        60003760,
                        30000142,
                        10000002,
                        1000035,
                        id,
                        false,
                        100,
                        TestingMarketRules.Stock,
                        TestingMarketRules.Stock,
                        134000000000000000
                    ),
                    MarketDefinitions.NpcOrderDays
                )
            );
        BlueMap<long, MarketOrderRow> reply = new(rows);
        byte[] expected = BlueMarshal.Encode(reply);
        Assert.True(expected.Length > PacketStream.MaxSessionBytes);
        using Aes aes = Aes.Create();
        using MemoryStream stream = new();
        await new EncryptedPackets(stream, aes, default).WriteAsync(reply);
        stream.Position = 0;
        byte[] ciphertext = await PacketStream.ReadAsync(stream, default, PacketStream.MaxSessionBytes);
        byte[] compressed = aes.DecryptCbc(ciphertext, aes.IV);
        Assert.Equal(0x78, compressed[0]);
        using ZLibStream inflater = new(new MemoryStream(compressed), CompressionMode.Decompress);
        using MemoryStream decoded = new();
        await inflater.CopyToAsync(decoded);
        Assert.Equal(expected, decoded.ToArray());
        Assert.Equal(19_727, rows.Count);
    }

    [Fact]
    public async Task SmallRepliesRemainPlainAndOversizedOutgoingMessagesAreRejected()
    {
        using Aes aes = Aes.Create();
        using MemoryStream stream = new();
        EncryptedPackets packets = new(stream, aes, default);
        await packets.WriteAsync(42L);
        stream.Position = 0;
        Assert.Equal(42L, await packets.ReadAsync());
        long before = stream.Length;
        await Assert.ThrowsAsync<OutgoingPacketSizeException>(() => packets.WriteAsync(new byte[EncryptedPackets.MaximumOutgoingBytes]));
        Assert.Equal(before, stream.Length);
    }

    [Fact]
    public async Task CiphertextLimitIncludesPaddingAndRejectsBeforeWriting()
    {
        using Aes aes = Aes.Create();
        int blockSize = aes.BlockSize / 8;
        int limit = EncryptedPackets.MaximumOutgoingCiphertextBytes;
        int lastBlock = limit - limit % blockSize;
        int blueOverhead = BlueMarshal.Encode(new byte[256]).Length - 256;
        byte[] data = new byte[lastBlock - blueOverhead];
        new Random(42).NextBytes(data);

        using MemoryStream stream = new();
        EncryptedPackets packets = new(stream, aes, default);
        await packets.WriteAsync(data[..^1]);
        Assert.Equal(lastBlock + sizeof(uint), stream.Length);
        stream.SetLength(0);
        await Assert.ThrowsAsync<OutgoingPacketSizeException>(() => packets.WriteAsync(data));
        Assert.Empty(stream.ToArray());
    }

    [Fact]
    public async Task EncodedLimitAllowsAnExactlySizedCompressibleReply()
    {
        int blueOverhead = BlueMarshal.Encode(new byte[256]).Length - 256;
        byte[] data = new byte[EncryptedPackets.MaximumOutgoingBytes - blueOverhead];
        using Aes aes = Aes.Create();
        using MemoryStream stream = new();
        await new EncryptedPackets(stream, aes, default).WriteAsync(data);
        stream.Position = 0;
        byte[] ciphertext = await PacketStream.ReadAsync(stream, default, EncryptedPackets.MaximumOutgoingCiphertextBytes);
        using ZLibStream inflater = new(new MemoryStream(aes.DecryptCbc(ciphertext, aes.IV)), CompressionMode.Decompress);
        using MemoryStream decoded = new();
        await inflater.CopyToAsync(decoded);
        Assert.Equal(EncryptedPackets.MaximumOutgoingBytes, decoded.Length);
    }

    [Fact]
    public async Task IncomingCompressedExpansionRemainsLimitedToOneMiB()
    {
        using Aes aes = Aes.Create();
        using MemoryStream compressed = new();
        using (ZLibStream compressor = new(compressed, CompressionLevel.Fastest, leaveOpen: true))
        {
            compressor.Write(new byte[PacketStream.MaxSessionBytes + 1]);
        }
        using MemoryStream stream = new();
        await PacketStream.WriteAsync(stream, aes.EncryptCbc(compressed.ToArray(), aes.IV), default, PacketStream.MaxSessionBytes);
        stream.Position = 0;
        await Assert.ThrowsAsync<InvalidDataException>(() => new EncryptedPackets(stream, aes, default).ReadAsync());
    }
}
