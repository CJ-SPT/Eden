namespace Eden.Protocol.Tests;

public sealed class PacketStreamTests
{
    [Fact]
    public async Task HandlesFragmentedAndAdjacentFrames()
    {
        using Stream stream = new FragmentedStream(Convert.FromHexString("02000000ABCD01000000EF"));
        Assert.Equal(new byte[] { 0xab, 0xcd }, await PacketStream.ReadAsync(stream, default));
        Assert.Equal(new byte[] { 0xef }, await PacketStream.ReadAsync(stream, default));
    }

    [Theory]
    [InlineData("00000000")]
    [InlineData("01000020")]
    [InlineData("01400000")]
    public async Task RejectsInvalidHeadersBeforeReadingPayload(string hex)
    {
        using MemoryStream stream = new(Convert.FromHexString(hex));
        await Assert.ThrowsAsync<InvalidDataException>(() => PacketStream.ReadAsync(stream, default));
    }

    [Theory]
    [InlineData("0100")]
    [InlineData("02000000AB")]
    public async Task DetectsTruncatedFrames(string hex)
    {
        using MemoryStream stream = new(Convert.FromHexString(hex));
        await Assert.ThrowsAsync<EndOfStreamException>(() => PacketStream.ReadAsync(stream, default));
    }

    [Fact]
    public async Task WritesPlainLittleEndianLength()
    {
        using MemoryStream stream = new();
        await PacketStream.WriteAsync(stream, new byte[] { 0xab, 0xcd }, default);
        Assert.Equal(Convert.FromHexString("02000000ABCD"), stream.ToArray());
    }

    private sealed class FragmentedStream(byte[] bytes) : MemoryStream(bytes)
    {
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            return base.ReadAsync(buffer[..Math.Min(1, buffer.Length)], cancellationToken);
        }
    }
}
