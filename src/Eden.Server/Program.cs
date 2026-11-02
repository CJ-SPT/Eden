using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using Eden.Protocol;

namespace Eden.Server;

internal static class Program
{
    private static async Task Main(string[] args)
    {
        using RSA? identity = OperatingSystem.IsWindows() ? DevelopmentIdentity.Open() : null;
        if (args is ["--prepare-login", string root] && OperatingSystem.IsWindows())
        {
            Console.WriteLine(DevelopmentIdentity.Prepare(root, identity!));
            return;
        }

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
                timeout.CancelAfter(TimeSpan.FromSeconds(120));
                Console.WriteLine($"{DateTimeOffset.Now:O} Accepted {client.Client.RemoteEndPoint}.");
                try
                {
                    await CompatibilityHandshake.ServeAsync(client.GetStream(), Console.WriteLine, timeout.Token, async (command, token) =>
                    {
                        if (!OperatingSystem.IsWindows() || identity is null) { throw new InvalidDataException("Local login requires Windows."); }
                        PilotLogin login = new(identity, ticket => DevelopmentIdentity.Validate(identity, ticket));
                        await login.RunAsync(client.GetStream(), command, Console.WriteLine, token);
                    });
                }
                catch (OperationCanceledException) when (!shutdown.IsCancellationRequested)
                {
                    Console.WriteLine("Startup exchange timed out.");
                }
                catch (Exception exception) when (exception is IOException or InvalidDataException or SocketException or CryptographicException or KeyNotFoundException or ArgumentException)
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
