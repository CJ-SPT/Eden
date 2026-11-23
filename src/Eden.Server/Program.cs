using System.Diagnostics;
using System.Net;
using System.Runtime.InteropServices;
using System.Text.Json;
using Eden.Gameplay.Redemption;
using Eden.Infrastructure.Diagnostics;
using Eden.Protocol.Gateway;
using Eden.Protocol.Serialization;
using Eden.Server.Administration;
using Eden.Server.Authentication;
using Eden.Server.Chat;
using Eden.Server.Configuration;
using Eden.Server.Gateway;
using Eden.Server.Launcher;
using Eden.Server.Logging;
using Eden.Server.Maintenance;
using Eden.World.Catalog;
using Eden.World.Fittings;
using Eden.World.Storage;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;

namespace Eden.Server;

internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        try
        {
            await RunAsync(args);
            return 0;
        }
        catch (Exception error)
        {
            bool pause = ServerConsole.ShouldPauseOnError();
            if (error is MissingCatalogException missingCatalog)
            {
                ServerConsole.WriteMissingCatalog(missingCatalog);
            }
            else if (pause)
            {
                Console.Error.WriteLine($"Eden stopped because of an error.{Environment.NewLine}{error}");
            }
            else
            {
                throw;
            }
            if (pause)
            {
                Console.Error.WriteLine();
                Console.Error.WriteLine("Press any key to close this window.");
                Console.ReadKey(intercept: true);
            }
            return 1;
        }
    }

    private static async Task RunAsync(string[] args)
    {
        await using var logging = BufferedServerLog.Start();
        if (await Eden.Server.Capsuleers.TrafficImportCommand.TryRun(args))
        {
            return;
        }
        if (args is ["--create-catalog", var candidate])
        {
            if (File.Exists(candidate))
            {
                throw new IOException("Catalog creation requires a new file.");
            }
            using CatalogDbContext catalog = new(candidate, writable: true);
            catalog.Database.EnsureCreated();
            return;
        }
        if (args is ["--decode-planet-files", var manifest])
        {
            foreach (var (input, output) in JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(manifest))!)
            {
                using StreamWriter writer = new(output);
                foreach (var planet in PlanetAttributeFile.Read(File.ReadAllBytes(input)))
                {
                    writer.WriteLine(JsonSerializer.Serialize(new object[] { planet.Key, planet.Value }));
                }
            }
            return;
        }
        if (args is ["--verify-catalog", var path])
        {
            CatalogSources.Verify(path);
            return;
        }
        if (args is ["--export-hull-coverage", var hullCatalog, var hullOutput])
        {
            HullSupportAudit.Export(hullCatalog, hullOutput);
            return;
        }
        if (args is ["--export-ship-content-coverage", var contentCatalog, var contentOutput])
        {
            ShipContentAudit.Export(contentCatalog, contentOutput);
            return;
        }
        string? Value(string option)
        {
            int index = Array.IndexOf(args, option);
            return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
        }
        long startupStarted = Stopwatch.GetTimestamp();
        foreach (string option in new[] { "--world", "--config", "--set-admin-password", "--admin-port" })
        {
            if (args.Contains(option) && (Value(option) is not { } value || value.StartsWith("--", StringComparison.Ordinal)))
            {
                throw new InvalidDataException($"{option} requires a value.");
            }
        }
        string worldPath = ServerDataPaths.WorldPath(
            Value("--world"),
            Environment.GetEnvironmentVariable("EDEN_DATA_DIR"),
            Directory.GetCurrentDirectory(),
            AppContext.BaseDirectory
        );
        if (Value("--set-admin-password") is { } administrator)
        {
            Console.Write("Administrator password: ");
            string password = ReadPassword();
            Console.Write("Confirm password: ");
            if (password != ReadPassword())
            {
                throw new InvalidDataException("Passwords do not match.");
            }
            new AdministratorAccounts(worldPath).SetPassword(administrator, password);
            Console.WriteLine("Local administrator provisioned. Previous administrator sessions have been revoked.");
            return;
        }
        if (ConfigurationCommands.TryRun(args, worldPath))
        {
            return;
        }
        string[] creationFlags = ["--economy-world", "--testing-market", "--economy-config", "--npc-stock"];
        if (args.Any(creationFlags.Contains))
        {
            throw new InvalidDataException("World creation settings have moved to administration. Start Eden without the creation flags.");
        }
        if (await Eden.Server.Modding.ModCommands.TryRun(args, new WorldDatabase(worldPath)))
        {
            return;
        }
        var options = LauncherOptions.Load(Value("--config"));
        if (args.Contains("--development-login"))
        {
            options = options with { DevelopmentLogin = true };
            options.Validate();
        }
        string? preparationRoot = Value("--prepare-login");
        using CancellationTokenSource shutdown = new();
        using var termination = OperatingSystem.IsWindows()
            ? null
            : PosixSignalRegistration.Create(
                PosixSignal.SIGTERM,
                context =>
                {
                    context.Cancel = true;
                    shutdown.Cancel();
                }
            );
        WorldDatabase database = new(worldPath);
        string? redeemPath = Value("--grant-redeem");
        string? accountName = Value("--set-account-password");
        bool online = preparationRoot is null && redeemPath is null && accountName is null;
        WorldWipeRequest worldWipe = new(worldPath);
        await ServerLifetime.RunAsync(async startup =>
        {
            ConsoleCancelEventHandler interrupt = (_, eventArgs) =>
            {
                eventArgs.Cancel = true;
                shutdown.Cancel();
            };
            Console.CancelKeyPress += interrupt;
            startup.OnStop("console interrupt", () => Console.CancelKeyPress -= interrupt);

            int port = Value("--admin-port") is { } adminPort
                ? int.TryParse(adminPort, out int parsedPort) && parsedPort is > 0 and <= 65535
                    ? parsedPort
                    : throw new InvalidDataException("--admin-port requires a port between 1 and 65535.")
                : AdministrationHost.DefaultPort;
            AdministrationBinding admin = new(
                port,
                Environment.GetEnvironmentVariable("EDEN_ADMIN_ORIGIN"),
                IPAddress.Parse(options.ListenAddress),
                options.CertificatePath,
                args.Contains("--disable-admin")
            );
            AdministratorAccounts administrators = new(worldPath);
            LauncherReadiness readiness = new();
            var api = online ? LauncherApi.Create(options, readiness, TimeProvider.System) : null;
            bool runtimeStopsApi = false;
            if (api is not null)
            {
                startup.OnStop("launcher API", api.DisposeAsync);
                startup.OnStop("setup launcher API listener", () => runtimeStopsApi ? ValueTask.CompletedTask : new(api.StopAsync()));
            }

            if (preparationRoot is null)
            {
                if (online)
                {
                    ServerBanner.Write(Console.Out);
                }
                Directory.CreateDirectory(Path.GetDirectoryName(worldPath)!);
                using FileStream startupOwnership = new(worldPath + ".owner", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
                var setup = WorldSetupStore.Read(worldPath);
                if (!online && setup is not null)
                {
                    throw new InvalidOperationException("Start Eden and complete world setup before managing game accounts.");
                }
                bool needsSetup = setup is { Approval: null };
                if (needsSetup && admin.Disabled)
                {
                    throw new InvalidDataException("World setup requires administration. Start Eden without --disable-admin.");
                }
                if (
                    online
                    && !admin.Disabled
                    && (needsSetup || administrators.Provisioned)
                    && new[] { options.GamePort, options.GatewayPort, options.ApiPort, ChatServer.ClientPort }.Contains(admin.Port)
                )
                {
                    throw new InvalidDataException("Administration requires a separate port.");
                }
                if (needsSetup)
                {
                    readiness.RequireSetup(null);
                }
                if (api is not null)
                {
                    await api.StartAsync(shutdown.Token);
                    ServerLog.Console.Information($"Launcher API listening on {options.ListenAddress}:{options.ApiPort}.");
                }
                var approval = setup?.Approval;
                if (setup is { Approval: null })
                {
                    approval = await ConfigureWorldAsync(database, setup, readiness, administrators, admin, shutdown.Token);
                    if (approval is null)
                    {
                        return;
                    }
                }
                readiness.StartWorld();
                using var reconciliationPerformance = SessionPerformance.Begin("startup.reconciliation", ServerLog.Console.Trace);
                long reconciliationStarted = Stopwatch.GetTimestamp();
                if (online)
                {
                    ServerLog.Console.Information($"Loading saved world from {worldPath}...");
                }
                WorldInitialization.Initialize(database, setup: approval);
                if (online)
                {
                    using var populationPhase = Eden.Infrastructure.Diagnostics.SessionPerformance.Phase("startup.capsuleerPopulation");
                    WorldInitialization.PrepareCapsuleers(database, maximumCommunities: 0);
                }

                ServerLog.Console.Information(
                    $"World reconciliation completed in {Stopwatch.GetElapsedTime(reconciliationStarted).TotalSeconds:F2}s."
                );
            }
            // Ticket preparation signs a local claim without opening the saved world.
            using var services = ServerStartup.Measure(
                "host creation",
                () =>
                    ServerServices.Build(
                        database,
                        options: options,
                        liveWorld: preparationRoot is null,
                        loadConfiguration: preparationRoot is null
                    )
            );
            if (preparationRoot is { } root)
            {
                if (!OperatingSystem.IsWindows())
                {
                    throw new PlatformNotSupportedException("Local development ticket preparation requires Windows.");
                }
                if (!options.DevelopmentLogin)
                {
                    throw new InvalidDataException("Pass --development-login explicitly for local development tickets.");
                }
                long accountId = args.Contains("Wing") ? 1000002 : 1000001;
                string claim = services
                    .GetRequiredService<DevelopmentIdentity>()
                    .Prepare(root, accountId, services.GetRequiredService<ChatCertificate>());
                Console.WriteLine(claim);
                return;
            }
            Directory.CreateDirectory(Path.GetDirectoryName(worldPath)!);
            if (redeemPath is not null)
            {
                var grant =
                    JsonSerializer.Deserialize<RedeemGrant>(File.ReadAllText(redeemPath))
                    ?? throw new InvalidDataException("The redeem grant file is empty.");
                var token = services.GetRequiredService<IAccountRedemption>().Grant(grant);
                Console.WriteLine($"Redeem token {token.Id} issued to account {grant.AccountId}.");
                return;
            }
            if (accountName is not null)
            {
                Console.Write("New password: ");
                string password = ReadPassword();
                Console.Write("Confirm password: ");
                if (password != ReadPassword())
                {
                    throw new InvalidDataException("Passwords do not match.");
                }
                services.GetRequiredService<LauncherStore>().SetPassword(accountName, password);
                Console.WriteLine("Account password updated. Previous launcher sessions have been revoked.");
                return;
            }
            runtimeStopsApi = true;
            await RunLiveWorldAsync(services, database, options, admin, api!, readiness, worldWipe, startupStarted, shutdown);
        });
        // Reaching here requires every producer and the final resident checkpoint to
        // have stopped successfully. Never replace a save while a resident owner exists.
        if (worldWipe.ShutdownRequested.IsCompletedSuccessfully && worldWipe.Pending is { } wipe)
        {
            ServerLog.Console.Information($"Backing up and wiping world. Backup directory: {wipe.BackupDirectory}");
            bool reconfigure = wipe.Recreation == WorldRecreation.Configure;
            WorldWipe.Execute(worldPath, wipe.BackupDirectory, reconfigure, wipe.ConfigurationGeneration);
            string next = WorldSetupStore.Read(worldPath) is not null
                ? "configure the replacement in administration"
                : "recreate it with the saved settings";
            ServerLog.Console.Information($"World wipe complete. Accounts retained. Restart Eden to {next}.");
        }
    }

    private static async Task RunLiveWorldAsync(
        IServiceProvider services,
        WorldDatabase database,
        LauncherOptions options,
        AdministrationBinding admin,
        WebApplication api,
        LauncherReadiness readiness,
        WorldWipeRequest worldWipe,
        long startupStarted,
        CancellationTokenSource shutdown
    )
    {
        var accounts = services.GetRequiredService<LauncherStore>();
        await ServerLifetime.RunAsync(async lifetime =>
        {
            Eden.World.Runtime.LiveWorld? world = null;
            lifetime.OnStop("resident world", () => world?.Dispose());
            lifetime.OnStop("launcher API listener", () => new ValueTask(api.StopAsync()));
            world = services.GetRequiredService<Eden.World.Runtime.LiveWorld>();
            // Load the canonical world image before accepting players or starting bot turns.
            // The first economic action must not pay the full saved-world hydration cost.
            ServerStartup.Measure("resident readiness", world.Economy.Warm);
            ServerStartup.Measure("accounts", accounts.Initialize);
            var mods = services.GetRequiredService<Eden.Server.Modding.ModRuntime>();
            using var modFailure = mods.FailureToken.Register(shutdown.Cancel);
            lifetime.OnStop("mods", mods.Stop);
            ServerStartup.Measure(
                "mod activation",
                () =>
                {
                    mods.Start();
                    services.GetRequiredService<Eden.World.Modding.ModStore>().CompleteActivation();
                }
            );
            Eden.World.Skills.SkillPackageRetention.PruneAfterStartup(database.Path);
            ServerStartup.WarmCatalogs(services, database);
            if (shutdown.IsCancellationRequested)
            {
                return;
            }
            var launcherMaintenance = services.GetRequiredService<LauncherMaintenance>();
            lifetime.OnStop(nameof(LauncherMaintenance), launcherMaintenance.DisposeAsync);
            launcherMaintenance.Start();
            var marketMaintenance = services.GetRequiredService<MarketMaintenance>();
            lifetime.OnStop(nameof(MarketMaintenance), marketMaintenance.DisposeAsync);
            marketMaintenance.Start();
            var concordMaintenance = services.GetRequiredService<ConcordMaintenance>();
            lifetime.OnStop(nameof(ConcordMaintenance), concordMaintenance.Dispose);
            concordMaintenance.Start();
            var standupFighterMaintenance = services.GetRequiredService<StandupFighterMaintenance>();
            lifetime.OnStop(nameof(StandupFighterMaintenance), standupFighterMaintenance.Dispose);
            standupFighterMaintenance.Start();
            var abyssalMaintenance = services.GetRequiredService<AbyssalMaintenance>();
            lifetime.OnStop(nameof(AbyssalMaintenance), abyssalMaintenance.Dispose);
            abyssalMaintenance.Start();
            var activityRewardMaintenance = services.GetRequiredService<ActivityRewardMaintenance>();
            lifetime.OnStop(nameof(ActivityRewardMaintenance), activityRewardMaintenance.Dispose);
            activityRewardMaintenance.Start();
            var sovereigntyMaintenance = services.GetRequiredService<SovereigntyMaintenance>();
            lifetime.OnStop(nameof(SovereigntyMaintenance), sovereigntyMaintenance.Dispose);
            sovereigntyMaintenance.Start();
            var corporationMaintenance = services.GetRequiredService<CorporationMaintenance>();
            lifetime.OnStop(nameof(CorporationMaintenance), corporationMaintenance.Dispose);
            corporationMaintenance.Start();
            var capsuleerMaintenance = services.GetRequiredService<CapsuleerMaintenance>();
            lifetime.OnStop(nameof(CapsuleerMaintenance), capsuleerMaintenance.Dispose);
            capsuleerMaintenance.Start(shutdown.Token);
            var population = services.GetRequiredService<CapsuleerPopulationMaintenance>();
            lifetime.OnStop(nameof(CapsuleerPopulationMaintenance), population.Dispose);
            var gateway = LocalGateway.Create(
                services.GetRequiredService<GatewayRequests>(),
                options,
                services.GetRequiredService<IGatewayAuthentication>()
            );
            lifetime.OnStop("gateway", gateway.DisposeAsync);
            var chat = services.GetRequiredService<ChatServer>();
            lifetime.OnStop("chat", chat.DisposeAsync);
            await chat.StartAsync(new(IPAddress.Parse(options.ListenAddress), ChatServer.ClientPort));
            lifetime.OnStop("gateway listener", () => new ValueTask(gateway.StopAsync()));
            await gateway.StartAsync();
            string? administrationUrl = null;
            if (!admin.Disabled && new AdministratorAccounts(database.Path).Provisioned)
            {
                var administration = AdministrationHost.Create(
                    services,
                    database.Path,
                    admin.Port,
                    admin.Origin,
                    admin.ListenAddress,
                    admin.CertificatePath,
                    worldWipe
                );
                lifetime.OnStop("administration", administration.DisposeAsync);
                lifetime.OnStop("administration listener", () => new ValueTask(administration.StopAsync()));
                await administration.StartAsync();
                administrationUrl = admin.Address;
                ServerLog.Console.Information($"Administration available at {administrationUrl}.");
            }
            else if (!admin.Disabled && admin.Origin is not null)
            {
                ServerLog.Console.Information("Provision an administrator with --set-admin-password to start the administration listener.");
            }
            ServerLog.Console.Information($"Gateway listening on {options.ListenAddress}:{options.GatewayPort}.");
            ServerLog.Console.Information($"Chat listening on {options.ListenAddress}:{ChatServer.ClientPort} (TLS).");
            Task server = services.GetRequiredService<LoopbackServer>().RunAsync(startupStarted, shutdown.Token);
            // Startup can fail after the listener starts. Always cancel and join it before stopping world producers.
            bool joined = false;
            lifetime.OnStop(
                "game listener",
                async () =>
                {
                    await shutdown.CancelAsync();
                    if (!joined)
                    {
                        await server;
                    }
                }
            );
            if (!server.IsCompleted && !shutdown.IsCancellationRequested)
            {
                population.Start(shutdown.Token);
                readiness.Open(
                    new(
                        accounts,
                        services.GetRequiredService<DevelopmentIdentity>(),
                        services.GetRequiredService<ChatCertificate>().PublicPem,
                        services.GetRequiredService<SkillClientPublisher>(),
                        services.GetRequiredService<Eden.World.Modding.ModStore>()
                    ),
                    administrationUrl
                );
                lifetime.OnStop("launcher account access", readiness.StartWorld);
            }
            var completed = await Task.WhenAny(server, chat.Completion, worldWipe.ShutdownRequested);
            readiness.StartWorld();
            if (completed == worldWipe.ShutdownRequested)
            {
                await shutdown.CancelAsync();
            }
            if (completed == chat.Completion)
            {
                await chat.Completion;
                throw new InvalidOperationException("Chat stopped before the game listener.");
            }
            joined = true;
            await server;
        });
    }

    private sealed record AdministrationBinding(int Port, string? Origin, IPAddress ListenAddress, string? CertificatePath, bool Disabled)
    {
        public string Address => string.IsNullOrEmpty(Origin) ? $"http://127.0.0.1:{Port}" : Origin;
    }

    private static async Task<WorldSetupApproval?> ConfigureWorldAsync(
        WorldDatabase database,
        WorldSetupState setup,
        LauncherReadiness readiness,
        AdministratorAccounts administrators,
        AdministrationBinding admin,
        CancellationToken shutdown
    )
    {
        ServerLog.Console.Information("World setup required. Game accounts are closed until an administrator creates the world.");
        if (!administrators.Provisioned)
        {
            ServerLog.Console.Information(
                $"In another terminal run Eden with --world \"{database.Path}\" --set-admin-password <name> to provision an administrator."
            );
        }
        try
        {
            while (!administrators.Provisioned)
            {
                await Task.Delay(TimeSpan.FromSeconds(1), shutdown);
            }
            WorldSetupRequest request = new(database, setup);
            await using var administration = AdministrationHost.Create(
                null,
                database.Path,
                admin.Port,
                admin.Origin,
                admin.ListenAddress,
                admin.CertificatePath,
                worldSetup: request
            );
            await administration.StartAsync(shutdown);
            string address = admin.Address.TrimEnd('/') + "/world/setup";
            readiness.RequireSetup(admin.Address);
            ServerLog.Console.Information($"Open {address} and sign in to configure the world.");
            var approval = await request.Approved.WaitAsync(shutdown);
            readiness.StartWorld();
            await administration.StopAsync();
            return approval;
        }
        catch (OperationCanceledException) when (shutdown.IsCancellationRequested)
        {
            return null;
        }
    }

    private static string ReadPassword()
    {
        if (Console.IsInputRedirected)
        {
            throw new InvalidOperationException("Password setup requires an interactive terminal.");
        }
        List<char> characters = [];
        while (true)
        {
            var key = Console.ReadKey(intercept: true);
            if (key.Key == ConsoleKey.Enter)
            {
                Console.WriteLine();
                return new string(characters.ToArray());
            }
            if (key.Key == ConsoleKey.Backspace && characters.Count > 0)
            {
                characters.RemoveAt(characters.Count - 1);
            }
            else if (!char.IsControl(key.KeyChar) && characters.Count < 129)
            {
                characters.Add(key.KeyChar);
            }
        }
    }
}
