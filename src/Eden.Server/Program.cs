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
        List<Task> clients = [];
        using SemaphoreSlim pilotSlot = new(1);
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
                clients.Add(ServeAsync(client, identity, pilotSlot, shutdown.Token));
            }
        }
        catch (OperationCanceledException) when (shutdown.IsCancellationRequested)
        {
            // Ctrl+C stops the listener and active connections.
        }
        finally
        {
            shutdown.Cancel();
            await Task.WhenAll(clients);
        }
    }

    private static async Task ServeAsync(TcpClient client, RSA? identity, SemaphoreSlim pilotSlot, CancellationToken shutdown)
    {
        using (client)
        using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(shutdown))
        {
            timeout.CancelAfter(TimeSpan.FromSeconds(30));
            Console.WriteLine($"{DateTimeOffset.Now:O} Accepted {client.Client.RemoteEndPoint}.");
            try
            {
                NetworkStream stream = client.GetStream();
                await CompatibilityHandshake.ServeAsync(stream, Console.WriteLine, timeout.Token, async (command, token) =>
                {
                    if (identity is null || !pilotSlot.Wait(0))
                    {
                        throw new InvalidDataException("The local pilot session is unavailable.");
                    }

                    try
                    {
                        PilotLogin login = new(identity, ticket => DevelopmentIdentity.Validate(identity, ticket));
                        await login.RunAsync(stream, command, Console.WriteLine, token, () => timeout.CancelAfter(Timeout.InfiniteTimeSpan));
                    }
                    finally
                    {
                        pilotSlot.Release();
                    }
                });
            }
            catch (OperationCanceledException)
            {
                Console.WriteLine(shutdown.IsCancellationRequested ? "Session closed." : "Login timed out.");
            }
            catch (Exception exception) when (exception is IOException or InvalidDataException or SocketException or CryptographicException or KeyNotFoundException or ArgumentException)
            {
                Console.WriteLine(exception.Message);
            }
        }
    }
}
