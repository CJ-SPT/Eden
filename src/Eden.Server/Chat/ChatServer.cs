using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using System.Xml;
using Eden.Infrastructure.Diagnostics;
using Eden.Infrastructure.Registration;
using Eden.Protocol.Chat;
using Microsoft.Extensions.DependencyInjection;

namespace Eden.Server.Chat;

[Injectable(InjectionType.Singleton)]
internal sealed class ChatServer(IServiceScopeFactory scopes, ChatCertificate certificate) : IAsyncDisposable, IDisposable
{
    // Build 3662168 XmppConnection fixes its direct-TLS listener port at 5222.
    internal const int ClientPort = 5222;
    internal const string Domain = "eden.chat";
    private const int MaximumConnections = 128;
    private readonly CancellationTokenSource _stop = new();
    private readonly ConcurrentDictionary<long, TcpClient> _clients = [];
    private readonly ConcurrentDictionary<long, Task> _sessions = [];
    private TcpListener? _listener;
    private Task? _accept;
    private long _nextConnection;
    private int _disposed;
    public IPEndPoint? Endpoint => _listener?.LocalEndpoint as IPEndPoint;
    internal Task Completion => _accept ?? throw new InvalidOperationException("Chat has not started.");

    public Task StartAsync(IPEndPoint endpoint) => StartListenerAsync(new TcpListener(endpoint));

    internal Task StartListenerAsync(TcpListener listener)
    {
        ObjectDisposedException.ThrowIf(_disposed != 0, this);
        if (_listener is not null)
        {
            throw new InvalidOperationException("Chat is already listening.");
        }
        _listener = listener;
        _listener.Start();
        _accept = AcceptAsync();
        return Task.CompletedTask;
    }

    private async Task AcceptAsync()
    {
        try
        {
            await AcceptConnectionsAsync();
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
        catch (Exception error)
        {
            ServerLog.Console.Error($"Chat listener failed: {error}");
            throw;
        }
    }

    private async Task AcceptConnectionsAsync()
    {
        while (!_stop.IsCancellationRequested)
        {
            var client = await _listener!.AcceptTcpClientAsync(_stop.Token);
            if (_clients.Count >= MaximumConnections)
            {
                client.Dispose();
                continue;
            }
            long id = Interlocked.Increment(ref _nextConnection);
            _clients[id] = client;
            var serving = ServeAsync(id, client);
            _sessions[id] = serving;
            _ = serving.ContinueWith(
                completed =>
                {
                    _sessions.TryRemove(id, out _);
                    if (completed.Exception is { } error)
                    {
                        ServerLog.Console.Error($"Chat connection failed ({error.GetType().Name}).");
                    }
                },
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default
            );
        }
    }

    private async Task ServeAsync(long id, TcpClient client)
    {
        long started = Stopwatch.GetTimestamp();
        long? pilotId = null;
        string reason = "peer-stream-ended";
        try
        {
            client.NoDelay = true;
            await using SslStream stream = new(client.GetStream(), leaveInnerStreamOpen: false);
            using (var login = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token))
            {
                login.CancelAfter(TimeSpan.FromSeconds(30));
                await stream.AuthenticateAsServerAsync(
                    new SslServerAuthenticationOptions
                    {
                        ServerCertificate = certificate.Certificate,
                        EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
                        CertificateRevocationCheckMode = X509RevocationMode.NoCheck,
                    },
                    login.Token
                );
            }
            using var scope = scopes.CreateScope();
            await scope
                .ServiceProvider.GetRequiredService<XmppSession>()
                .RunAsync(
                    stream,
                    _stop.Token,
                    pilot =>
                    {
                        pilotId = pilot;
                        ServerLog.Console.Information($"Chat connection {id} established for pilot {pilot}.");
                    }
                );
        }
        catch (AuthenticationException error)
        {
            reason = "tls-" + error.GetType().Name;
            if (!_stop.IsCancellationRequested)
            {
                ServerLog.Console.Warning($"Chat TLS negotiation failed ({error.GetType().Name}).");
            }
        }
        catch (ObjectDisposedException) when (_stop.IsCancellationRequested)
        {
            reason = "server-stopped";
        }
        catch (Exception error)
            when (error
                    is IOException
                        or SocketException
                        or AuthenticationException
                        or OperationCanceledException
                        or InvalidDataException
                        or XmlException
                        or ArgumentException
            )
        {
            reason = error.GetType().Name;
            // Stanzas and authentication tokens must never enter host logs.
            if (!_stop.IsCancellationRequested)
            {
                if (error is IOException or SocketException or OperationCanceledException)
                {
                    ServerLog.Console.Debug($"Chat connection closed ({error.GetType().Name}).");
                }
                else
                {
                    ServerLog.Console.Warning($"Chat connection rejected ({error.GetType().Name}).");
                }
            }
        }
        finally
        {
            ServerLog.Console.Information(
                $"Chat connection {id} ended: pilot={pilotId}, reason={(_stop.IsCancellationRequested ? "server-stopped" : reason)}, "
                    + $"elapsed={Stopwatch.GetElapsedTime(started).TotalSeconds:F2}s."
            );
            client.Dispose();
            _clients.TryRemove(id, out _);
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }
        try
        {
            await _stop.CancelAsync();
        }
        finally
        {
            try
            {
                if (_accept is not null)
                {
                    // Completion exposes accept failures to the host. Disposal still drains the sessions.
                    await _accept.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
                }
                _listener?.Stop();
                foreach (var client in _clients.Values)
                {
                    client.Dispose();
                }
                await Task.WhenAll(_sessions.Values).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
            }
            finally
            {
                _stop.Dispose();
            }
        }
    }

    public void Dispose() => DisposeAsync().AsTask().GetAwaiter().GetResult();
}
