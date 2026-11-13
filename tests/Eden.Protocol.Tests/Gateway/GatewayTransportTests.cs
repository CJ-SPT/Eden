using System.Buffers.Binary;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Eden.Protocol.Gateway;
using Eden.Server.Gateway;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Microsoft.Extensions.DependencyInjection;

namespace Eden.Protocol.Tests.Gateway;

public sealed class GatewayTransportTests
{
    [Fact]
    public async Task RelayedCleartextHttp2HeadersReachTheTlsGateway()
    {
        using TestWorld world = new();
        using var key = RSA.Create(2048);
        CertificateRequest certificateRequest = new("CN=localhost", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        using var certificate = certificateRequest.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));
        string certificatePath = Path.GetTempFileName();
        try
        {
            File.WriteAllBytes(certificatePath, certificate.Export(X509ContentType.Pfx));
            await using var app = LocalGateway.Create(
                world.Services.GetRequiredService<GatewayRequests>(),
                new() { CertificatePath = certificatePath, GatewayPort = 0 }
            );
            using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(15));
            await app.StartAsync(timeout.Token);
            using SocketsHttpHandler relay = new()
            {
                UseProxy = false,
                ConnectCallback = async (context, cancellation) =>
                {
                    Socket socket = new(SocketType.Stream, ProtocolType.Tcp);
                    SslStream? tls = null;
                    try
                    {
                        await socket.ConnectAsync(context.DnsEndPoint, cancellation);
                        tls = new(new NetworkStream(socket, ownsSocket: true));
                        await tls.AuthenticateAsClientAsync(
                            new SslClientAuthenticationOptions
                            {
                                TargetHost = "localhost",
                                ApplicationProtocols = [SslApplicationProtocol.Http2],
                                RemoteCertificateValidationCallback = (_, presented, _, _) =>
                                    presented?.GetCertHashString() == certificate.Thumbprint,
                            },
                            cancellation
                        );
                        return tls;
                    }
                    catch
                    {
                        if (tls is null)
                        {
                            socket.Dispose();
                        }
                        else
                        {
                            await tls.DisposeAsync();
                        }
                        throw;
                    }
                },
            };
            using HttpClient client = new(relay);
            var endpoint = new UriBuilder(Assert.Single(app.Urls)) { Scheme = "http", Path = "/eve_public.gateway.Requests/Send" };
            const string activityApi = "type.googleapis.com/eve_public.sovereignty.mercenaryden.activity.api.";
            Request message = new()
            {
                CorrelationUuid = ByteString.CopyFrom(Guid.NewGuid().ToByteArray()),
                Payload = new Any { TypeUrl = activityApi + "GetAllRequest" },
            };
            byte[] frame = new byte[5 + message.CalculateSize()];
            BinaryPrimitives.WriteInt32BigEndian(frame.AsSpan(1), frame.Length - 5);
            message.ToByteArray().CopyTo(frame, 5);
            using HttpRequestMessage request = new(HttpMethod.Post, endpoint.Uri)
            {
                Version = HttpVersion.Version20,
                VersionPolicy = HttpVersionPolicy.RequestVersionExact,
                Content = new ByteArrayContent(frame),
            };
            request.Content.Headers.ContentType = new("application/grpc");
            request.Headers.TE.ParseAdd("trailers");
            using var httpReply = await client.SendAsync(request, timeout.Token);
            Assert.Equal(HttpStatusCode.OK, httpReply.StatusCode);
            byte[] responseFrame = await httpReply.Content.ReadAsByteArrayAsync(timeout.Token);
            Assert.Equal(0, responseFrame[0]);
            Assert.Equal(responseFrame.Length - 5, BinaryPrimitives.ReadInt32BigEndian(responseFrame.AsSpan(1)));
            var response = Response.Parser.ParseFrom(responseFrame.AsSpan(5));
            Assert.Equal(200u, response.StatusCode);
            Assert.Equal(message.CorrelationUuid, response.CorrelationUuid);
            Assert.Equal(activityApi + "GetAllResponse", response.Payload.TypeUrl);
            Assert.Empty(response.Payload.Value);
        }
        finally
        {
            File.Delete(certificatePath);
        }
    }
}
