using System.Net;
using System.Security.Claims;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.RateLimiting;
using Eden.Gameplay.Configuration;
using Eden.Infrastructure.Diagnostics;
using Eden.Server.Administration.Components;
using Eden.World.Catalog;
using Eden.World.Runtime;
using Eden.World.Storage;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Eden.Server.Administration;

internal static class AdministrationHost
{
    internal const int DefaultPort = 26003;
    internal const string CsrfHeader = "X-Eden-Admin-Csrf";

    internal static WebApplication Create(IServiceProvider worldServices, string worldPath, int port = DefaultPort)
    {
        if (port is < 0 or > 65535)
        {
            throw new InvalidDataException("Invalid local administration port.");
        }
        AdministratorAccounts administrators = new(worldPath);
        if (!administrators.Provisioned)
        {
            throw new InvalidDataException("Provision a local administrator before starting administration.");
        }
        var builder = WebApplication.CreateBuilder(
            new WebApplicationOptions
            {
                ApplicationName = typeof(AdministrationHost).Assembly.GetName().Name,
                ContentRootPath = AppContext.BaseDirectory,
            }
        );
        // Inherited Kestrel endpoints and URLs cannot add an administration listener.
        builder.Configuration.Sources.Clear();
        builder.WebHost.UseStaticWebAssets();
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(options =>
        {
            options.Limits.MaxRequestBodySize = 8 * 1024 * 1024;
            options.Listen(IPAddress.Loopback, port);
        });
        builder.Services.AddSingleton(administrators);
        builder.Services.ConfigureHttpJsonOptions(options =>
            options.SerializerOptions.UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
        );
        builder.Services.AddSingleton(worldServices.GetRequiredService<IWorldConfiguration>());
        builder.Services.AddSingleton(worldServices.GetRequiredService<CatalogProvider>());
        builder.Services.AddSingleton(provider => new CatalogBrowser(provider.GetRequiredService<CatalogProvider>()));
        builder.Services.AddScoped<AdministrationOperations>();
        builder.Services.AddSingleton(
            new ServerPerformanceMonitor(
                TimeProvider.System,
                worldServices.GetRequiredService<PilotGate>(),
                worldServices.GetService<LiveWorld>()
            )
        );
        var protection = builder
            .Services.AddDataProtection()
            .SetApplicationName("Eden.Administration." + administrators.Realm)
            .PersistKeysToFileSystem(new DirectoryInfo(Path.GetFullPath(worldPath) + ".admin-keys"));
        if (OperatingSystem.IsWindows())
        {
            protection.ProtectKeysWithDpapi();
        }
        builder
            .Services.AddAuthentication(AdministratorAccounts.Scheme)
            .AddCookie(
                AdministratorAccounts.Scheme,
                options =>
                {
                    options.Cookie.Name = ".Eden.Admin." + administrators.Realm;
                    options.Cookie.HttpOnly = true;
                    options.Cookie.SameSite = SameSiteMode.Strict;
                    options.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
                    options.LoginPath = "/login";
                    options.ExpireTimeSpan = TimeSpan.FromMinutes(30);
                    options.SlidingExpiration = false;
                    options.Events.OnValidatePrincipal = async context =>
                    {
                        if (context.Principal is not { } user || !administrators.IsCurrent(user))
                        {
                            context.RejectPrincipal();
                            await context.HttpContext.SignOutAsync(AdministratorAccounts.Scheme);
                        }
                    };
                    options.Events.OnRedirectToLogin = context =>
                    {
                        if (context.Request.Path.StartsWithSegments("/api"))
                        {
                            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                        }
                        else
                        {
                            context.Response.Redirect(context.RedirectUri);
                        }
                        return Task.CompletedTask;
                    };
                }
            );
        builder.Services.AddAuthorization(options =>
            options.AddPolicy(
                AdministratorAccounts.Policy,
                policy =>
                    policy
                        .AddAuthenticationSchemes(AdministratorAccounts.Scheme)
                        .RequireAuthenticatedUser()
                        .RequireRole(AdministratorAccounts.Role)
            )
        );
        builder.Services.AddAntiforgery(options =>
        {
            options.HeaderName = CsrfHeader;
            options.Cookie.Name = ".Eden.Admin.Csrf." + administrators.Realm;
            options.Cookie.SameSite = SameSiteMode.Strict;
        });
        builder.Services.AddRazorComponents().AddInteractiveServerComponents();
        builder.Services.AddCascadingAuthenticationState();
        builder.Services.AddRateLimiter(options =>
        {
            options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            options.AddPolicy(
                "administrator-login",
                _ =>
                    RateLimitPartition.GetFixedWindowLimiter(
                        "local",
                        _ => new FixedWindowRateLimiterOptions { PermitLimit = 10, Window = TimeSpan.FromMinutes(1) }
                    )
            );
        });
        var app = builder.Build();
        app.Use(
            async (context, next) =>
            {
                if (!LocalRequest(context))
                {
                    context.Response.StatusCode = StatusCodes.Status403Forbidden;
                    return;
                }
                context.Response.Headers.CacheControl = "no-store";
                context.Response.Headers.XFrameOptions = "DENY";
                context.Response.Headers.ContentSecurityPolicy = "frame-ancestors 'none'; base-uri 'self'; form-action 'self'";
                try
                {
                    await next(context);
                }
                catch (AntiforgeryValidationException)
                {
                    context.Response.StatusCode = StatusCodes.Status400BadRequest;
                }
                catch (UnauthorizedAccessException)
                {
                    context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                }
                catch (Exception error) when (error is InvalidDataException or JsonException or ArgumentException or KeyNotFoundException)
                {
                    context.Response.StatusCode = StatusCodes.Status400BadRequest;
                    await context.Response.WriteAsJsonAsync(new AdminValidationResult(false, [error.Message]));
                }
                catch (InvalidOperationException error)
                {
                    context.Response.StatusCode = StatusCodes.Status409Conflict;
                    await context.Response.WriteAsJsonAsync(new AdminValidationResult(false, [error.Message]));
                }
                catch (Exception error)
                {
                    ServerLog.Console.Error($"Local administration request failed: {error}");
                    throw;
                }
            }
        );
        app.UseRouting();
        app.UseAuthentication();
        app.UseAuthorization();
        app.UseAntiforgery();
        app.UseRateLimiter();
        app.MapPost(
                "/session/login",
                async (HttpContext context, IAntiforgery antiforgery) =>
                {
                    await antiforgery.ValidateRequestAsync(context);
                    if (context.Request.ContentLength is > 4096)
                    {
                        return Results.BadRequest();
                    }
                    var form = await context.Request.ReadFormAsync();
                    var user = administrators.Authenticate(form["name"].ToString(), form["password"].ToString());
                    if (user is null)
                    {
                        return Results.Redirect("/login?failed=true");
                    }
                    await context.SignInAsync(AdministratorAccounts.Scheme, user, new AuthenticationProperties { IsPersistent = false });
                    return Results.Redirect("/");
                }
            )
            .AllowAnonymous()
            .RequireRateLimiting("administrator-login");
        app.MapPost(
                "/logout",
                async (HttpContext context, IAntiforgery antiforgery) =>
                {
                    await antiforgery.ValidateRequestAsync(context);
                    await context.SignOutAsync(AdministratorAccounts.Scheme);
                    return Results.Redirect("/login");
                }
            )
            .RequireAuthorization(AdministratorAccounts.Policy);
        var api = app.MapGroup("/api/admin").RequireAuthorization(AdministratorAccounts.Policy);
        api.AddEndpointFilter(
            async (context, next) =>
            {
                if (HttpMethods.IsPost(context.HttpContext.Request.Method))
                {
                    await context.HttpContext.RequestServices.GetRequiredService<IAntiforgery>().ValidateRequestAsync(context.HttpContext);
                }
                return await next(context);
            }
        );
        api.MapGet(
            "/antiforgery",
            (HttpContext context, IAntiforgery antiforgery) => new { token = antiforgery.GetAndStoreTokens(context).RequestToken }
        );
        api.MapGet("/performance", (ClaimsPrincipal user, AdministrationOperations operations) => operations.Performance(user));
        api.MapGet("/configuration", (ClaimsPrincipal user, AdministrationOperations operations) => operations.Read(user));
        api.MapGet(
            "/revisions",
            (ClaimsPrincipal user, AdministrationOperations operations, int skip = 0) => operations.History(user, skip)
        );
        api.MapGet(
            "/revisions/{id:long}",
            (long id, ClaimsPrincipal user, AdministrationOperations operations) => operations.Revision(user, id)
        );
        api.MapGet(
            "/export/{id:long}",
            async (long id, ClaimsPrincipal user, AdministrationOperations operations) =>
                Results.File(
                    JsonSerializer.SerializeToUtf8Bytes((await operations.Revision(user, id)).Draft, JsonSerializerOptions.Web),
                    "application/json",
                    $"eden-configuration-{id}.json"
                )
        );
        api.MapPost(
            "/validate",
            (WorldConfigurationDraft draft, ClaimsPrincipal user, AdministrationOperations operations) => operations.Validate(user, draft)
        );
        api.MapPost(
            "/revisions",
            (AdminSaveRequest request, ClaimsPrincipal user, AdministrationOperations operations) => operations.Save(user, request)
        );
        api.MapPost(
            "/restore",
            (AdminRestoreRequest request, ClaimsPrincipal user, AdministrationOperations operations) => operations.Restore(user, request)
        );
        api.MapPost(
            "/import",
            (AdminImportRequest request, ClaimsPrincipal user, AdministrationOperations operations) =>
                operations.Import(user, request.Generation, request.Document)
        );
        api.MapGet(
            "/compare",
            (long left, long right, ClaimsPrincipal user, AdministrationOperations operations) => operations.Compare(user, left, right)
        );
        api.MapGet("/datasets", (ClaimsPrincipal user, AdministrationOperations operations) => operations.Datasets(user));
        api.MapGet(
            "/catalog",
            (
                ClaimsPrincipal user,
                AdministrationOperations operations,
                string dataset,
                string? after = null,
                int limit = 50,
                string? search = null,
                string? key = null
            ) => operations.Page(user, dataset, after, limit, search, key)
        );
        app.MapStaticAssets();
        app.MapRazorComponents<AdministrationApp>().AddInteractiveServerRenderMode().RequireAuthorization(AdministratorAccounts.Policy);
        return app;
    }

    private static bool LocalRequest(HttpContext context)
    {
        string host = context.Request.Host.Host;
        bool localHost =
            host.Equals("localhost", StringComparison.OrdinalIgnoreCase)
            || IPAddress.TryParse(host, out var address) && IPAddress.IsLoopback(address);
        if (!localHost || context.Connection.RemoteIpAddress is not { } remote || !IPAddress.IsLoopback(remote))
        {
            return false;
        }
        return !context.Request.Headers.TryGetValue("Origin", out var origin)
            || Uri.TryCreate(origin.ToString(), UriKind.Absolute, out var uri)
                && uri.Scheme == context.Request.Scheme
                && uri.Authority.Equals(context.Request.Host.Value, StringComparison.OrdinalIgnoreCase);
    }
}
