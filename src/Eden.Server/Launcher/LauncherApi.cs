using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Threading.RateLimiting;
using Eden.Infrastructure.Diagnostics;
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
        LauncherReadiness readiness = new();
        readiness.Open(new(accounts, identity, chatCertificatePem, skillPublisher, mods));
        return Create(settings, readiness, clock);
    }

    public static WebApplication Create(LauncherOptions settings, LauncherReadiness readiness, TimeProvider clock)
    {
        settings.Validate();
        byte[]? certificateAuthority = LoadCertificateAuthority(settings.CertificateAuthorityPath);
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
                    var path = context.Request.Path;
                    if (path != "/api/v1/server" && path != "/api/v1/ca-certificate")
                    {
                        _ = readiness.RequireWorld();
                    }
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
        if (certificateAuthority is not null)
        {
            app.MapGet("/api/v1/ca-certificate", () => Results.Bytes(certificateAuthority, "application/pkix-cert"));
            ServerLog.Console.Information($"Launcher CA SHA-256 fingerprint: {Convert.ToHexString(SHA256.HashData(certificateAuthority))}");
        }
        app.MapGet(
            "/api/v1/server",
            (HttpContext context) =>
            {
                var availability = readiness.Read();
                var world = availability.World;
                var mods = world?.Mods;
                var activeSet = mods?.ActiveSet;
                string? setupUrl = availability.SetupUrl;
                if (
                    Uri.TryCreate(setupUrl, UriKind.Absolute, out var uri)
                    && uri.IsLoopback
                    && !context.Request.Host.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase)
                    && !(IPAddress.TryParse(context.Request.Host.Host, out var ip) && IPAddress.IsLoopback(ip))
                )
                {
                    setupUrl = null;
                }
                return new ServerReply(
                    settings.ServerId,
                    settings.Name,
                    mods is null ? 1 : 2,
                    CompatibilityHandshake.Build,
                    settings.GameAddress,
                    settings.GatewayAddress,
                    world?.ChatCertificatePem,
                    activeSet is null ? world?.SkillPublisher?.Status().Active : null,
                    activeSet,
                    availability.State,
                    setupUrl
                );
            }
        );
        app.MapPost("/api/v1/accounts", (AccountRequest request) => readiness.RequireWorld().Accounts.Register(request))
            .RequireRateLimiting("register");
        app.MapPost("/api/v1/sessions", (SignInRequest request) => readiness.RequireWorld().Accounts.SignIn(request))
            .RequireRateLimiting("sign-in");
        app.MapPost("/api/v1/sessions/refresh", (RefreshRequest request) => readiness.RequireWorld().Accounts.Refresh(request.RefreshToken))
            .RequireRateLimiting("sign-in");
        app.MapDelete(
            "/api/v1/sessions/current",
            (HttpContext context) =>
            {
                readiness.RequireWorld().Accounts.Revoke(Access(context));
                return Results.NoContent();
            }
        );
        app.MapPost(
                "/api/v1/launch-tickets",
                async (LaunchRequest request, HttpContext context) =>
                {
                    if (request.Build != CompatibilityHandshake.Build)
                    {
                        throw new LauncherException("unsupported_build", "This server requires client build 3662168.");
                    }
                    var (accounts, identity, _, skillPublisher, mods) = readiness.RequireWorld();
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
                    string ticket = await accounts.IssueTicketAsync(Access(context), context.RequestAborted);
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
                var (accounts, _, _, skillPublisher, _) = readiness.RequireWorld();
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
                var (accounts, _, _, _, mods) = readiness.RequireWorld();
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

    private static byte[]? LoadCertificateAuthority(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }
        try
        {
            using var certificate = X509CertificateLoader.LoadCertificateFromFile(path);
            if (!certificate.Extensions.OfType<X509BasicConstraintsExtension>().Any(extension => extension.CertificateAuthority))
            {
                throw new InvalidDataException("CertificateAuthorityPath must point to the public CA certificate.");
            }
            return certificate.RawData;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or CryptographicException)
        {
            throw new InvalidDataException("Could not load the public CA certificate from CertificateAuthorityPath.", error);
        }
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
