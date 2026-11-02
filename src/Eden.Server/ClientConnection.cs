using System.Net.Sockets;
using System.Security.Cryptography;
using Eden.Protocol;
using Eden.Protocol.PilotSessions;
using Eden.Protocol.Transport;

namespace Eden.Server;

[Injectable(InjectionType.Scoped)]
internal sealed class ClientConnection(PilotLogin login, PilotGate gate)
{
    public async Task ServeAsync(TcpClient client, CancellationToken shutdown)
    {
        using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(shutdown))
        {
            timeout.CancelAfter(TimeSpan.FromSeconds(30));
            Console.WriteLine($"{DateTimeOffset.Now:O} Accepted {client.Client.RemoteEndPoint}.");
            try
            {
                NetworkStream stream = client.GetStream();
                await CompatibilityHandshake.ServeAsync(stream, Console.WriteLine, timeout.Token, async (command, token) =>
                {
                    if (!gate.TryEnter())
                    {
                        throw new InvalidDataException("The local pilot session is unavailable.");
                    }

                    try
                    {
                        await login.RunAsync(stream, command, Console.WriteLine, token, () => timeout.CancelAfter(Timeout.InfiniteTimeSpan));
                    }
                    finally
                    {
                        gate.Leave();
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
