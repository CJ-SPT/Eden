using System.Net;
using Eden.Infrastructure.Diagnostics;
using Eden.Infrastructure.Operations;
using Eden.Infrastructure.Registration;
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
    public static WebApplication Create(
        GatewayRequests requests,
        LauncherOptions? configuration = null,
        IGatewayAuthentication? authentication = null,
        IPilotOperationScope? operations = null
    )
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        var settings = configuration ?? new LauncherOptions();
        builder.WebHost.ConfigureKestrel(options =>
        {
            // The native client uses cleartext HTTP/2 to the launcher relay, which forwards the same headers over TLS.
            options.AllowAlternateSchemes = settings.CertificatePath is not null;
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
            );
        });
        builder.Services.AddGrpc();
        builder.Services.AddSingleton(requests);
        if (operations is not null)
        {
            builder.Services.AddSingleton(operations);
        }
        builder.Services.AddSingleton(authentication ?? (IGatewayAuthentication)new PublicGatewayAuthentication());
        var app = builder.Build();
        app.Use(
            async (context, next) =>
            {
                // These duplex subscriptions cannot drain until their peer closes. Signal the real transport abort
                // when this gateway stops so gRPC readers wake without waiting for Kestrel's drain deadline.
                using var stopping = app.Lifetime.ApplicationStopping.Register(context.Abort);
                await next(context);
            }
        );
        if (settings.CertificatePath is not null)
        {
            app.Use(
                (context, next) =>
                {
                    if (context.Request.Scheme is not ("http" or "https"))
                    {
                        context.Response.StatusCode = (int)HttpStatusCode.BadRequest;
                        return Task.CompletedTask;
                    }
                    context.Request.Scheme = "https";
                    return next(context);
                }
            );
        }
        app.MapGrpcService<RequestStream>();
        app.MapGrpcService<NoticeStream>();
        app.MapGrpcService<EventStream>();
        return app;
    }

    private sealed class PublicGatewayAuthentication : IGatewayAuthentication
    {
        public long? Account(string authorization) => null;
    }
}

[Injectable]
internal sealed class RequestStream(
    GatewayRequests requests,
    IGatewayAuthentication authentication,
    IPilotOperationScope? operations = null
) : Requests.RequestsBase
{
    public override Task<Empty> Ping(Empty request, ServerCallContext context) => Task.FromResult(new Empty());

    public override async Task Send(IAsyncStreamReader<Request> input, IServerStreamWriter<Response> output, ServerCallContext context)
    {
        await context.WriteResponseHeadersAsync(new Metadata());
        await foreach (var request in input.ReadAllAsync(context.CancellationToken))
        {
            using var operation = operations?.Begin();
            long? account = authentication.Account(context.RequestHeaders.GetValue("authorization") ?? "");
            var response = requests.Handle(request, account);
            ServerLog.Console.Debug($"Gateway {Any.GetTypeName(request.Payload?.TypeUrl ?? "")} ({response.StatusCode}).");
            await output.WriteAsync(response);
        }
    }
}

[Injectable]
internal sealed class NoticeStream(GatewayRequests requests, IGatewayAuthentication authentication, IPilotOperationScope? operations = null)
    : Notices.NoticesBase
{
    public override Task<Empty> Ping(Empty request, ServerCallContext context) => Task.FromResult(new Empty());

    public override async Task Consume(IAsyncStreamReader<Empty> input, IServerStreamWriter<Notice> output, ServerCallContext context)
    {
        await context.WriteResponseHeadersAsync(new Metadata());
        using var stopping = CancellationTokenSource.CreateLinkedTokenSource(context.CancellationToken);
        using PeriodicTimer timer = new(TimeSpan.FromSeconds(1));
        ShipSkinNoticeCursor skinNotices = new();
        string authorization = context.RequestHeaders.GetValue("authorization") ?? "";
        async Task Receive()
        {
            while (await input.MoveNext(stopping.Token)) { }
        }
        async Task Publish()
        {
            while (await timer.WaitForNextTickAsync(stopping.Token))
            {
                using var operation = operations?.Begin();
                if (authentication.Account(authorization) is not long account)
                {
                    skinNotices = new();
                    continue;
                }
                foreach (
                    var notice in requests
                        .SovereigntyNotices(account)
                        .Concat(requests.FactionActivityNotices(account))
                        .Concat(requests.ShipSkinNotices(account, skinNotices))
                )
                {
                    await output.WriteAsync(notice, stopping.Token);
                }
            }
        }
        Task receive = Receive(),
            publish = Publish();
        try
        {
            await await Task.WhenAny(receive, publish);
        }
        finally
        {
            await stopping.CancelAsync();
            await Task.WhenAll(receive, publish).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
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
