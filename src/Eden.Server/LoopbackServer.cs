using System.Net;
using System.Net.Sockets;
using Eden.Protocol;
using Eden.Protocol.Transport;
using Microsoft.Extensions.DependencyInjection;

namespace Eden.Server;

[Injectable(InjectionType.Singleton)]
internal sealed class LoopbackServer(IServiceScopeFactory scopes)
{
    public async Task RunAsync()
    {
        using CancellationTokenSource shutdown = new();
        void OnCancel(object? sender, ConsoleCancelEventArgs args)
        {
            args.Cancel = true;
            shutdown.Cancel();
        }

        using TcpListener listener = new(IPAddress.Loopback, 26000);
        listener.Start();
        Console.WriteLine($"Eden listening on {listener.LocalEndpoint} for build {CompatibilityHandshake.Build}.");
        List<Task> clients = [];
        Console.CancelKeyPress += OnCancel;
        try
        {
            while (!shutdown.IsCancellationRequested)
            {
                clients.RemoveAll(task => task.IsCompletedSuccessfully);
                if (clients.Count >= 8)
                {
                    Task completed = await Task.WhenAny(clients);
                    await completed;
                    clients.Remove(completed);
                }

                TcpClient client = await listener.AcceptTcpClientAsync(shutdown.Token);
                clients.Add(ServeInScopeAsync(client, shutdown.Token));
            }
        }
        catch (OperationCanceledException) when (shutdown.IsCancellationRequested)
        {
            // Ctrl+C stops the listener and active connections.
        }
        finally
        {
            Console.CancelKeyPress -= OnCancel;
            shutdown.Cancel();
            await Task.WhenAll(clients);
        }
    }

    private async Task ServeInScopeAsync(TcpClient client, CancellationToken shutdown)
    {
        using (client)
        using (var scope = scopes.CreateScope())
        {
            await scope.ServiceProvider.GetRequiredService<ClientConnection>().ServeAsync(client, shutdown);
        }
    }
}
