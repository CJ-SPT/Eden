using System.Net;
using System.Net.Sockets;
using Eden.Server.Chat;
using Microsoft.Extensions.DependencyInjection;

namespace Eden.Protocol.Tests.Chat;

public sealed class ChatListenerTests
{
    [Fact]
    public async Task AcceptFailureIsObservableAndShutdownClosesPendingSessions()
    {
        using TestWorld world = new();
        await using var chat = world.Services.GetRequiredService<ChatServer>();
        using TcpListener listener = new(IPAddress.Loopback, 0);
        using CancellationTokenSource deadline = new(TimeSpan.FromSeconds(10));
        await chat.StartListenerAsync(listener);
        using TcpClient pending = new();
        await pending.ConnectAsync(chat.Endpoint!, deadline.Token);

        // A second connection waits for a server TLS greeting. Its arrival proves
        // that the preceding connection has already entered the accept loop.
        using TcpClient barrier = new();
        await barrier.ConnectAsync(chat.Endpoint!, deadline.Token);
        using var tls = new System.Net.Security.SslStream(barrier.GetStream(), false, (_, _, _, _) => true);
        await tls.AuthenticateAsClientAsync(new() { TargetHost = ChatServer.Domain }, deadline.Token);

        listener.Stop();
        await Assert.ThrowsAnyAsync<SocketException>(() => chat.Completion.WaitAsync(deadline.Token));
        await chat.DisposeAsync().AsTask().WaitAsync(deadline.Token);
        Assert.Equal(0, await pending.GetStream().ReadAsync(new byte[1], deadline.Token));
    }

    [Fact]
    public async Task NormalShutdownCompletesTheAcceptLoopSuccessfully()
    {
        using TestWorld world = new();
        await using var chat = world.Services.GetRequiredService<ChatServer>();
        await chat.StartAsync(new(IPAddress.Loopback, 0));
        await chat.DisposeAsync();
        Assert.True(chat.Completion.IsCompletedSuccessfully);
    }
}
