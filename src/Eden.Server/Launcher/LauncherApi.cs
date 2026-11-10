using System.Net;
using System.Threading.RateLimiting;
using Eden.Protocol.Transport;
using Eden.Server.Administration;
using Eden.Server.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace Eden.Server.Launcher;

internal static class LauncherApi
{
    public static WebApplication Create(
        LauncherOptions settings,
        LauncherStore accounts,
        DevelopmentIdentity identity,
        TimeProvider clock,
        string? chatCertificatePem = null,
        SkillClientPublisher? skillPublisher = null,
        Eden.World.Modding.ModStore? mods = null
    )
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
                    mods is null ? 1 : 2,
                    CompatibilityHandshake.Build,
                    settings.GameAddress,
                    settings.GatewayAddress,
                    chatCertificatePem,
                    mods?.ActiveSet is null ? skillPublisher?.Status().Active : null,
                    mods?.ActiveSet
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
                    var set = mods?.ActiveSet;
                    var package = set is null
                        ? skillPublisher?.Status().Active
                        : new Eden.World.Skills.SkillClientPackage(set.Id, set.Generation, set.CodeSha256, set.CodeLength);
                    if (
                        set?.Id != request.ModSetId
                        || (set is null && package?.Id != request.SkillPackageId)
                        || (set is not null && request.SkillPackageId is not null)
                    )
                    {
                        throw new LauncherException(
                            "client_update_required",
                            "Refresh the server and install its required mod set before playing.",
                            409
                        );
                    }
                    accounts.AuthorizeAccess(Access(context));
                    byte[] manifest = identity.SignManifest(request.ManifestPrefix, request.ChatBundleSha256, package?.CodeSha256);
                    string ticket = accounts.IssueTicket(Access(context));
                    return new LaunchReply(
                        ticket,
                        clock.GetUtcNow().ToUnixTimeSeconds() + LauncherStore.TicketSeconds,
                        Convert.ToBase64String(manifest)
                    );
                }
            )
            .RequireRateLimiting("sign-in");
        app.MapGet(
            "/api/v1/skill-packages/{id}/code",
            (string id, HttpContext context) =>
            {
                accounts.AuthorizeAccess(Access(context));
                if (skillPublisher?.Status().Active?.Id != id)
                {
                    throw new LauncherException(
                        "client_update_required",
                        "This skill package is not active. Refresh the server information.",
                        409
                    );
                }
                return Results.File(skillPublisher.ActiveCodePath(id), "application/octet-stream", enableRangeProcessing: false);
            }
        );
        app.MapGet(
            "/api/v2/mod-sets/{id}/code",
            (string id, HttpContext context) =>
            {
                accounts.AuthorizeAccess(Access(context));
                if (mods?.ActiveSet?.Id != id)
                {
                    throw new LauncherException("client_update_required", "This mod set is no longer active. Refresh the server.", 409);
                }
                return Results.File(
                    Path.Combine(mods.SetDirectory(id), "code.ccp"),
                    "application/octet-stream",
                    enableRangeProcessing: false
                );
            }
        );
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
