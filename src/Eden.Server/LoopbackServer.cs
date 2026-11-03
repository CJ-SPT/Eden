using System.Net;
using System.Net.Sockets;
using Eden.Protocol;
using Eden.Protocol.Transport;
using Eden.Server.Launcher;
using Microsoft.Extensions.DependencyInjection;

namespace Eden.Server;

[Injectable(InjectionType.Singleton)]
internal sealed class LoopbackServer(ConnectionRunner connections, LauncherOptions? options = null)
{
    public async Task RunAsync()
    {
        using CancellationTokenSource shutdown = new();
        void OnCancel(object? sender, ConsoleCancelEventArgs args)
        {
            args.Cancel = true;
            shutdown.Cancel();
        }

        using TcpListener listener = new(IPAddress.Parse((options ?? new()).ListenAddress), (options ?? new()).GamePort);
        listener.Start();
        Console.WriteLine($"Eden listening on {listener.LocalEndpoint} for build {CompatibilityHandshake.Build}.");
        Console.CancelKeyPress += OnCancel;
        try
        {
            await ListenAsync(listener, shutdown.Token);
        }
        finally
        {
            Console.CancelKeyPress -= OnCancel;
        }
    }

    internal async Task ListenAsync(TcpListener listener, CancellationToken cancellationToken)
    {
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var shutdown = lifetime.Token;
        const int MaximumClients = 8;
        List<Task> clients = [];
        try
        {
            while (!shutdown.IsCancellationRequested)
            {
                clients.RemoveAll(task => task.IsCompleted);
                if (clients.Count >= MaximumClients)
                {
                    Task completed = await Task.WhenAny(clients);
                    await completed;
                    clients.Remove(completed);
                }
                TcpClient client = await listener.AcceptTcpClientAsync(shutdown);
                clients.Add(ServeInScopeAsync(client, shutdown));
            }
        }
        catch (OperationCanceledException) when (shutdown.IsCancellationRequested)
        {
            // Ctrl+C stops the listener and active connections.
        }
        finally
        {
            await lifetime.CancelAsync();
            await Task.WhenAll(clients);
        }
    }

    private async Task ServeInScopeAsync(TcpClient client, CancellationToken shutdown)
    {
        using (client)
        {
            try
            {
                await connections.RunAsync(
                    (services, admission) => services.GetRequiredService<ClientConnection>().ServeAsync(client, admission, shutdown)
                );
            }
            catch (Exception exception)
            {
                // Include scope-disposal failures. One failed session must not stop the accept loop.
                Console.Error.WriteLine($"Connection failed: {exception}");
            }
        }
    }
}
