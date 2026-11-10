using System.IO.Compression;
using System.Security.Cryptography;
using Eden.Gameplay.Market;
using Eden.Protocol.Market;
using Eden.Protocol.Serialization;
using Eden.Protocol.Transport;

namespace Eden.Protocol.Tests.Transport;

public sealed class EncryptedPacketsTests
{
    [Fact]
    public async Task IncompressibleRepliesKeepTheirOriginalPayloadAcrossRepeatedPoolReuse()
    {
        byte[] data = new byte[EncryptedPackets.CompressionThreshold * 2];
        new Random(42).NextBytes(data);
        using Aes aes = Aes.Create();
        using MemoryStream stream = new();
        EncryptedPackets packets = new(stream, aes, default);
        for (int index = 0; index < 8; index++)
        {
            stream.SetLength(0);
            await packets.WriteAsync(data);
            stream.Position = 0;
            byte[] ciphertext = await PacketStream.ReadAsync(stream, default, PacketStream.MaxSessionBytes);
            Assert.Equal(BlueMarshal.Encode(data), aes.DecryptCbc(ciphertext, aes.IV));
            stream.Position = 0;
            Assert.Equal(data, Assert.IsType<byte[]>(await packets.ReadAsync()));
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
        await Assert.ThrowsAsync<InvalidDataException>(() => packets.WriteAsync(new byte[EncryptedPackets.MaximumOutgoingBytes]));
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
