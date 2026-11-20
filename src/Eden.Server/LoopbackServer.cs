using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using Eden.Infrastructure.Diagnostics;
using Eden.Infrastructure.Registration;
using Eden.Protocol.Transport;
using Eden.Server.Launcher;

namespace Eden.Server;

[Injectable(InjectionType.Singleton)]
internal sealed class LoopbackServer(ConnectionRunner connections, LauncherOptions? options = null)
{
    public async Task RunAsync(long? startupStarted = null, CancellationToken cancellationToken = default)
    {
        using TcpListener listener = new(IPAddress.Parse((options ?? new()).ListenAddress), (options ?? new()).GamePort);
        listener.Start();
        if (startupStarted is { } started)
        {
            ServerLog.Console.Information($"Listeners ready after {Stopwatch.GetElapsedTime(started).TotalSeconds:F2}s.");
        }
        ServerLog.Console.Information($"Eden listening on {listener.LocalEndpoint} for build {CompatibilityHandshake.Build}.");
        try
        {
            await ListenAsync(listener, cancellationToken);
        }
        finally
        {
            ServerLog.Console.Information("Game listener stopped.");
        }
    }

    internal async Task ListenAsync(TcpListener listener, CancellationToken cancellationToken)
    {
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var shutdown = lifetime.Token;
        PendingLogins pending = new(options ?? new());
        List<Task> clients = [];
        try
        {
            while (!shutdown.IsCancellationRequested)
            {
                clients.RemoveAll(task => task.IsCompleted);
                TcpClient client = await listener.AcceptTcpClientAsync(shutdown);
                var login = pending.TryEnter(((IPEndPoint)client.Client.RemoteEndPoint!).Address);
                if (login is null)
                {
                    client.Dispose();
                    continue;
                }
                clients.Add(ServeInScopeAsync(client, login, shutdown));
            }
        }
        catch (OperationCanceledException) when (shutdown.IsCancellationRequested)
        {
            // Shutdown stops the listener and active connections before the world is saved.
        }
        finally
        {
            await lifetime.CancelAsync();
            await Task.WhenAll(clients);
        }
    }

    private async Task ServeInScopeAsync(TcpClient client, IDisposable login, CancellationToken shutdown)
    {
        using (login)
        using (client)
        {
            try
            {
                await connections.RunAsync(
                    (services, admission) => services.GetRequiredService<ClientConnection>().ServeAsync(client, admission, shutdown),
                    login.Dispose
                );
            }
            catch (Exception exception)
            {
                // Include scope-disposal failures. One failed session must not stop the accept loop.
                ServerLog.Console.Error($"Connection failed: {exception}");
            }
        }
    }
}
