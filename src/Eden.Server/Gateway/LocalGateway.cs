using System.Net;
using Eden.Protocol;
using Eden.Protocol.Gateway;
using Eden.Server.Launcher;
using Google.Protobuf.WellKnownTypes;
using Grpc.Core;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Eden.Server.Gateway;

internal static class LocalGateway
{
    public static WebApplication Create(GatewayRequests requests, LauncherOptions? configuration = null)
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        var settings = configuration ?? new LauncherOptions();
        builder.WebHost.ConfigureKestrel(options =>
            options.Listen(
                IPAddress.Parse(settings.ListenAddress),
                settings.GatewayPort,
                endpoint =>
                {
                    endpoint.Protocols = HttpProtocols.Http2;
                    if (settings.CertificatePath is { } certificate)
                    {
                        endpoint.UseHttps(certificate, Environment.GetEnvironmentVariable("EDEN_CERTIFICATE_PASSWORD"));
                    }
                }
            )
        );
        builder.Services.AddGrpc();
        builder.Services.AddSingleton(requests);
        var app = builder.Build();
        app.MapGrpcService<RequestStream>();
        app.MapGrpcService<NoticeStream>();
        app.MapGrpcService<EventStream>();
        return app;
    }
}

[Injectable]
internal sealed class RequestStream(GatewayRequests requests) : Requests.RequestsBase
{
    public override Task<Empty> Ping(Empty request, ServerCallContext context) => Task.FromResult(new Empty());

    public override async Task Send(IAsyncStreamReader<Request> input, IServerStreamWriter<Response> output, ServerCallContext context)
    {
        await context.WriteResponseHeadersAsync(new Metadata());
        await foreach (var request in input.ReadAllAsync(context.CancellationToken))
        {
            var response = requests.Handle(request);
            Console.WriteLine($"Gateway {Any.GetTypeName(request.Payload?.TypeUrl ?? "")} ({response.StatusCode}).");
            await output.WriteAsync(response);
        }
    }
}

[Injectable]
internal sealed class NoticeStream : Notices.NoticesBase
{
    public override Task<Empty> Ping(Empty request, ServerCallContext context) => Task.FromResult(new Empty());

    public override async Task Consume(IAsyncStreamReader<Empty> input, IServerStreamWriter<Notice> output, ServerCallContext context)
    {
        await context.WriteResponseHeadersAsync(new Metadata());
        while (await input.MoveNext(context.CancellationToken))
        {
            // Keep the subscription open. This host publishes gameplay notifications over MachoNet.
        }
    }
}

[Injectable]
internal sealed class EventStream : Events.EventsBase
{
    public override Task<Empty> Ping(Empty request, ServerCallContext context) => Task.FromResult(new Empty());

    public override async Task Publish(IAsyncStreamReader<Event> input, IServerStreamWriter<Empty> output, ServerCallContext context)
    {
        await context.WriteResponseHeadersAsync(new Metadata());
        while (await input.MoveNext(context.CancellationToken))
        {
            await output.WriteAsync(new Empty());
        }
    }
}
