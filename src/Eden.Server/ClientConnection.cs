using System.Net.Sockets;
using System.Security.Cryptography;
using Eden.Infrastructure.Diagnostics;
using Eden.Infrastructure.Registration;
using Eden.Protocol.PilotSessions;
using Eden.Protocol.Transport;

namespace Eden.Server;

[Injectable(InjectionType.Scoped)]
internal sealed class ClientConnection(PilotLogin login)
{
    public async Task ServeAsync(TcpClient client, ConnectionAdmission admission, CancellationToken shutdown)
    {
        using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(shutdown))
        {
            timeout.CancelAfter(TimeSpan.FromSeconds(30));
            long? activeAccount = null;
            ServerLog.Console.Debug($"Accepted {client.Client.RemoteEndPoint}.");
            try
            {
                NetworkStream stream = client.GetStream();
                await CompatibilityHandshake.ServeAsync(
                    stream,
                    ServerLog.Console.Debug,
                    timeout.Token,
                    async (command, token) =>
                    {
                        await login.RunAsync(
                            stream,
                            command,
                            ServerLog.Console.Debug,
                            token,
                            accountId =>
                            {
                                admission.Enter(accountId);
                                activeAccount = accountId;
                                timeout.CancelAfter(Timeout.InfiniteTimeSpan);
                            }
                        );
                    }
                );
            }
            catch (OperationCanceledException)
            {
                if (!shutdown.IsCancellationRequested)
                {
                    ServerLog.Console.Warning("Connection operation timed out.");
                }
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
                if (exception is EndOfStreamException or SocketException)
                {
                    ServerLog.Console.Debug($"Session disconnected ({exception.GetType().Name}).");
                }
                else
                {
                    ServerLog.Console.Warning($"Session rejected: {exception.Message}");
                }
            }
            finally
            {
                if (activeAccount is { } accountId)
                {
                    ServerLog.Console.Information($"Account {accountId} disconnected.");
                }
            }
        }
    }
}
