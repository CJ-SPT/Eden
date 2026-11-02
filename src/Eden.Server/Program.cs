using System.Net;
using System.Net.Sockets;
using Eden.Protocol;

namespace Eden.Server;

internal static class Program
{
    private static async Task Main()
    {
        using CancellationTokenSource shutdown = new();
        Console.CancelKeyPress += (_, args) =>
        {
            args.Cancel = true;
            shutdown.Cancel();
        };

        using TcpListener listener = new(IPAddress.Loopback, 26000);
        listener.Start();
        Console.WriteLine($"Eden listening on {listener.LocalEndpoint} for build {CompatibilityHandshake.Build}.");
        try
        {
            while (!shutdown.IsCancellationRequested)
            {
                using TcpClient client = await listener.AcceptTcpClientAsync(shutdown.Token);
                using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(shutdown.Token);
                timeout.CancelAfter(TimeSpan.FromSeconds(15));
                Console.WriteLine($"{DateTimeOffset.Now:O} Accepted {client.Client.RemoteEndPoint}.");
                try
                {
                    await CompatibilityHandshake.ServeAsync(client.GetStream(), Console.WriteLine, timeout.Token);
                }
                catch (OperationCanceledException) when (!shutdown.IsCancellationRequested)
                {
                    Console.WriteLine("Startup exchange timed out.");
                }
                catch (Exception exception) when (exception is IOException or InvalidDataException or SocketException)
                {
                    Console.WriteLine(exception.Message);
                }
            }
        }
        catch (OperationCanceledException) when (shutdown.IsCancellationRequested)
        {
            // Ctrl+C interrupts both accept and an active read.
        }
    }
}
