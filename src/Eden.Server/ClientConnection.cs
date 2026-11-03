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
                await CompatibilityHandshake.ServeAsync(
                    stream,
                    Console.WriteLine,
                    timeout.Token,
                    async (command, token) =>
                    {
                        long? admitted = null;
                        try
                        {
                            await login.RunAsync(
                                stream,
                                command,
                                Console.WriteLine,
                                token,
                                accountId =>
                                {
                                    if (!gate.TryEnter(accountId))
                                    {
                                        throw new InvalidDataException("This account is already connected.");
                                    }
                                    admitted = accountId;
                                    timeout.CancelAfter(Timeout.InfiniteTimeSpan);
                                }
                            );
                        }
                        finally
                        {
                            if (admitted is long accountId)
                            {
                                gate.Leave(accountId);
                            }
                        }
                    }
                );
            }
            catch (OperationCanceledException)
            {
                Console.WriteLine(shutdown.IsCancellationRequested ? "Session closed." : "Login timed out.");
            }
            catch (Exception exception)
                when (exception
                        is IOException
                            or InvalidDataException
                            or SocketException
                            or CryptographicException
                            or KeyNotFoundException
                            or ArgumentException
                )
            {
                Console.WriteLine(exception.Message);
            }
        }
    }
}
