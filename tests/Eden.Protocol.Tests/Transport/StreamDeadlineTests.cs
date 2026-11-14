using System.Text;
using Eden.Protocol.Chat;
using Eden.Protocol.Tests.Persistence;
using Eden.Protocol.Transport;
using Microsoft.Extensions.DependencyInjection;

namespace Eden.Protocol.Tests.Transport;

public sealed class StreamDeadlineTests
{
    [Fact]
    public async Task ChatStreamGreetingCannotBlockItsSessionIndefinitely()
    {
        using TestWorld world = new();
        TestClock time = new();
        CheckpointClock clock = new(time);
        using StalledStream stream = new(
            Encoding.UTF8.GetBytes("<stream:stream xmlns='jabber:client' xmlns:stream='http://etherx.jabber.org/streams' to='eden.chat'>")
        );
        var serving = new XmppSession(world.Services.GetRequiredService<IXmppChat>(), clock).RunAsync(stream, default);
        await stream.Started.Task;
        time.Seconds = 30;
        clock.FireDueTimers();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => serving);
    }

    private sealed class StalledStream(byte[] prefix, bool blockPayload = false) : MemoryStream(prefix)
    {
        internal TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _writes;

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (Position < Length)
            {
                return await base.ReadAsync(buffer, cancellationToken);
            }
            Started.TrySetResult();
            await Task.Delay(Timeout.Infinite, cancellationToken);
            return 0;
        }

        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (blockPayload && _writes++ == 0)
            {
                return;
            }
            Started.TrySetResult();
            await Task.Delay(Timeout.Infinite, cancellationToken);
        }
    }

    [Theory]
    [InlineData("01")]
    [InlineData("02000000AB")]
    public async Task PartialFramesExpireWithoutWaitingForAnotherByte(string prefix)
    {
        TestClock time = new();
        CheckpointClock clock = new(time);
        using StalledStream stream = new(Convert.FromHexString(prefix));
        var reading = PacketStream.ReadAsync(stream, default, clock: clock);
        await stream.Started.Task;
        time.Seconds = 30;
        clock.FireDueTimers();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => reading);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task HeaderAndPayloadWritesShareABoundedDeadline(bool payload)
    {
        TestClock time = new();
        CheckpointClock clock = new(time);
        using StalledStream stream = new([], payload);
        var writing = PacketStream.WriteAsync(stream, new byte[] { 1 }, default, clock: clock);
        await stream.Started.Task;
        time.Seconds = 30;
        clock.FireDueTimers();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => writing);
    }

    [Fact]
    public async Task PreparedFrameWriteRetainsItsDeadlineAndShutdownCancellation()
    {
        TestClock time = new();
        CheckpointClock clock = new(time);
        using StalledStream stream = new([]);
        var writing = PacketStream.WriteFrameAsync(stream, new byte[5], default, 1, clock);
        await stream.Started.Task;
        time.Seconds = 30;
        clock.FireDueTimers();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => writing);
        using CancellationTokenSource stop = new();
        using StalledStream cancelled = new([]);
        writing = PacketStream.WriteFrameAsync(cancelled, new byte[5], stop.Token, 1, clock);
        await cancelled.Started.Task;
        await stop.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => writing);
    }

    [Fact]
    public async Task SilenceBeforeAFrameHasNoDeadlineAndShutdownStillCancelsIt()
    {
        TestClock time = new();
        CheckpointClock clock = new(time);
        using CancellationTokenSource stop = new();
        using StalledStream stream = new([]);
        var reading = PacketStream.ReadAsync(stream, stop.Token, clock: clock);
        await stream.Started.Task;
        time.Seconds = 60;
        clock.FireDueTimers();
        Assert.False(reading.IsCompleted);
        await stop.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => reading);
    }
}
