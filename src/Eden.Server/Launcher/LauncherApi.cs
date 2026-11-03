using System.Net;
using System.Threading.RateLimiting;
using Eden.Protocol.Transport;
using Eden.Server.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Eden.Server.Launcher;

internal static class LauncherApi
{
    public static WebApplication Create(LauncherOptions settings, LauncherStore accounts, DevelopmentIdentity identity, TimeProvider clock)
    {
        settings.Validate();
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(options =>
        {
            options.Limits.MaxRequestBodySize = 64 * 1024;
            options.Listen(
                IPAddress.Parse(settings.ListenAddress),
                settings.ApiPort,
                endpoint =>
                {
                    if (settings.CertificatePath is { } certificate)
                    {
                        endpoint.UseHttps(certificate, Environment.GetEnvironmentVariable("EDEN_CERTIFICATE_PASSWORD"));
                    }
                }
            );
        });
        builder.Services.AddRateLimiter(options =>
        {
            options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            options.OnRejected = async (context, cancellation) =>
                await context.HttpContext.Response.WriteAsJsonAsync(
                    new ApiError("rate_limited", "Too many attempts. Try again later."),
                    cancellation
                );
            options.AddPolicy(
                "sign-in",
                context =>
                    RateLimitPartition.GetFixedWindowLimiter(
                        context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
                        _ => new FixedWindowRateLimiterOptions { PermitLimit = 20, Window = TimeSpan.FromMinutes(1) }
                    )
            );
            options.AddPolicy(
                "register",
                context =>
                    RateLimitPartition.GetFixedWindowLimiter(
                        context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
                        _ => new FixedWindowRateLimiterOptions { PermitLimit = 5, Window = TimeSpan.FromHours(1) }
                    )
            );
        });
        var app = builder.Build();
        app.Use(
            async (context, next) =>
            {
                context.Response.Headers.CacheControl = "no-store";
                try
                {
                    await next(context);
                }
                catch (LauncherException error)
                {
                    context.Response.StatusCode = error.Status;
                    await context.Response.WriteAsJsonAsync(new ApiError(error.Code, error.Message));
                }
            }
        );
        app.UseRateLimiter();
        app.MapGet(
            "/api/v1/server",
            () =>
                new ServerReply(
                    settings.ServerId,
                    settings.Name,
                    1,
                    CompatibilityHandshake.Build,
                    settings.GameAddress,
                    settings.GatewayAddress
                )
        );
        app.MapPost("/api/v1/accounts", (AccountRequest request) => accounts.Register(request)).RequireRateLimiting("register");
        app.MapPost("/api/v1/sessions", (SignInRequest request) => accounts.SignIn(request)).RequireRateLimiting("sign-in");
        app.MapPost("/api/v1/sessions/refresh", (RefreshRequest request) => accounts.Refresh(request.RefreshToken))
            .RequireRateLimiting("sign-in");
        app.MapDelete(
            "/api/v1/sessions/current",
            (HttpContext context) =>
            {
                accounts.Revoke(Access(context));
                return Results.NoContent();
            }
        );
        app.MapPost(
                "/api/v1/launch-tickets",
                (LaunchRequest request, HttpContext context) =>
                {
                    if (request.Build != CompatibilityHandshake.Build)
                    {
                        throw new LauncherException("unsupported_build", "This server requires client build 3662168.");
                    }
                    string ticket = accounts.IssueTicket(Access(context));
                    byte[] manifest = identity.SignManifest(request.ManifestPrefix);
                    return new LaunchReply(
                        ticket,
                        clock.GetUtcNow().ToUnixTimeSeconds() + LauncherStore.TicketSeconds,
                        Convert.ToBase64String(manifest)
                    );
                }
            )
            .RequireRateLimiting("sign-in");
        return app;
    }

    private static string Access(HttpContext context)
    {
        string header = context.Request.Headers.Authorization.ToString();
        if (!header.StartsWith("Bearer ", StringComparison.Ordinal) || header.Length > 128)
        {
            throw new LauncherException("unauthorized", "Please sign in again.", 401);
        }
        return header[7..];
    }
}
