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
using Microsoft.Extensions.DependencyInjection;

namespace Eden.Server;

internal static class Program
{
    private static async Task Main(string[] args)
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
        bool economyWorld = args.Contains("--economy-world");
        bool testingMarket = args.Contains("--testing-market");
        if (testingMarket && (economyWorld || File.Exists(worldPath)))
        {
            throw new InvalidDataException("--testing-market requires a new world without --economy-world.");
        }
        if (economyWorld && File.Exists(worldPath))
        {
            throw new InvalidDataException("--economy-world requires a new world path.");
        }
        int npcStock = Eden.Gameplay.Market.NpcSupplyRules.Stock;
        Eden.Gameplay.Economy.EconomicConfiguration? economicConfiguration = null;
        if (Value("--economy-config") is { } economicPath)
        {
            if (!economyWorld)
            {
                throw new InvalidDataException("--economy-config requires a new --economy-world.");
            }
            economicConfiguration =
                System.Text.Json.JsonSerializer.Deserialize<Eden.Gameplay.Economy.EconomicConfiguration>(
                    File.ReadAllText(economicPath),
                    new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web)
                    {
                        UnmappedMemberHandling = System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow,
                    }
                ) ?? throw new InvalidDataException("Missing economy configuration.");
            Eden.Gameplay.Economy.EconomicRules.Validate(economicConfiguration);
        }
        if (Value("--npc-stock") is { } stockText)
        {
            if (!economyWorld || !int.TryParse(stockText, out npcStock))
            {
                throw new InvalidDataException("--npc-stock requires a new --economy-world and a whole-number stock quantity.");
            }
            Eden.Gameplay.Market.NpcSupplyRules.ValidateStock(npcStock);
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
        if (preparationRoot is null)
        {
            if (Value("--grant-redeem") is null && Value("--set-account-password") is null)
            {
                ServerBanner.Write(Console.Out);
            }
            Directory.CreateDirectory(Path.GetDirectoryName(worldPath)!);
            using FileStream startupOwnership = new(worldPath + ".owner", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            long reconciliationStarted = Stopwatch.GetTimestamp();
            Eden.Gameplay.Market.WorldEconomySettings? startingEconomy = File.Exists(worldPath)
                ? null
                : new(testingMarket, !economyWorld, npcStock);
            bool preparePopulation = Value("--grant-redeem") is null && Value("--set-account-password") is null;
            if (preparePopulation)
            {
                ServerLog.Console.Information($"Loading saved world from {worldPath}...");
            }
            WorldInitialization.Initialize(database, economy: startingEconomy, economics: economicConfiguration);
            if (preparePopulation)
            {
                using var populationPhase = Eden.Infrastructure.Diagnostics.SessionPerformance.Phase("startup.capsuleerPopulation");
                WorldInitialization.PrepareCapsuleers(database, maximumCommunities: 0);
            }
            ServerLog.Console.Information(
                $"World reconciliation completed in {Stopwatch.GetElapsedTime(reconciliationStarted).TotalSeconds:F2}s."
            );
        }
        long hostStarted = Stopwatch.GetTimestamp();
        // Ticket preparation signs a local claim without opening the saved world.
        using var services = ServerServices.Build(
            database,
            options: options,
            liveWorld: preparationRoot is null,
            loadConfiguration: preparationRoot is null
        );
        ServerLog.Console.Information($"Host creation completed in {Stopwatch.GetElapsedTime(hostStarted).TotalSeconds:F2}s.");
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
        var accounts = services.GetRequiredService<LauncherStore>();
        accounts.Initialize();
        if (Value("--grant-redeem") is { } redeemPath)
        {
            var grant =
                JsonSerializer.Deserialize<RedeemGrant>(File.ReadAllText(redeemPath))
                ?? throw new InvalidDataException("The redeem grant file is empty.");
            var token = services.GetRequiredService<IAccountRedemption>().Grant(grant);
            Console.WriteLine($"Redeem token {token.Id} issued to account {grant.AccountId}.");
            return;
        }
        if (Value("--set-account-password") is { } userName)
        {
            Console.Write("New password: ");
            string password = ReadPassword();
            Console.Write("Confirm password: ");
            if (password != ReadPassword())
            {
                throw new InvalidDataException("Passwords do not match.");
            }
            accounts.SetPassword(userName, password);
            Console.WriteLine("Account password updated. Previous launcher sessions have been revoked.");
            return;
        }
        await ServerLifetime.RunAsync(async lifetime =>
        {
            var world = services.GetRequiredService<Eden.World.Runtime.LiveWorld>();
            lifetime.OnStop("resident world", world.Dispose);
            // Load the canonical world image before accepting players or starting bot turns.
            // The first economic action must not pay the full saved-world hydration cost.
            long residentStarted = Stopwatch.GetTimestamp();
            world.Economy.Warm();
            ServerLog.Console.Information($"Resident world loaded in {Stopwatch.GetElapsedTime(residentStarted).TotalSeconds:F2}s.");
            var mods = services.GetRequiredService<Eden.Server.Modding.ModRuntime>();
            lifetime.OnStop("mods", mods.Stop);
            mods.Start();
            services.GetRequiredService<Eden.World.Modding.ModStore>().CompleteActivation();
            services.GetRequiredService<CatalogProvider>().PreloadCore();
            if (shutdown.IsCancellationRequested)
            {
                return;
            }
            int administrationPort = Value("--admin-port") is { } adminPort
                ? int.TryParse(adminPort, out int parsedPort) && parsedPort is > 0 and <= 65535
                    ? parsedPort
                    : throw new InvalidDataException("--admin-port requires a port between 1 and 65535.")
                : AdministrationHost.DefaultPort;
            bool administrationEnabled = !args.Contains("--disable-admin") && new AdministratorAccounts(worldPath).Provisioned;
            string? administrationOrigin = Environment.GetEnvironmentVariable("EDEN_ADMIN_ORIGIN");
            if (
                administrationEnabled
                && new[] { options.GamePort, options.GatewayPort, options.ApiPort, ChatServer.ClientPort }.Contains(administrationPort)
            )
            {
                throw new InvalidDataException("Administration requires a separate port.");
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
            var capsuleerMaintenance = services.GetRequiredService<CapsuleerMaintenance>();
            lifetime.OnStop(nameof(CapsuleerMaintenance), capsuleerMaintenance.Dispose);
            capsuleerMaintenance.Start();
            var population = services.GetRequiredService<CapsuleerPopulationMaintenance>();
            lifetime.OnStop(nameof(CapsuleerPopulationMaintenance), population.Dispose);
            var gateway = LocalGateway.Create(services.GetRequiredService<GatewayRequests>(), options);
            lifetime.OnStop("gateway", gateway.DisposeAsync);
            var api = LauncherApi.Create(
                options,
                accounts,
                services.GetRequiredService<DevelopmentIdentity>(),
                TimeProvider.System,
                services.GetRequiredService<ChatCertificate>().PublicPem,
                services.GetRequiredService<Eden.Server.Administration.SkillClientPublisher>(),
                services.GetRequiredService<Eden.World.Modding.ModStore>()
            );
            lifetime.OnStop("launcher API", api.DisposeAsync);
            var chat = services.GetRequiredService<ChatServer>();
            lifetime.OnStop("chat", chat.DisposeAsync);
            await chat.StartAsync(new(IPAddress.Parse(options.ListenAddress), ChatServer.ClientPort));
            lifetime.OnStop("gateway listener", () => new ValueTask(gateway.StopAsync()));
            await gateway.StartAsync();
            lifetime.OnStop("launcher API listener", () => new ValueTask(api.StopAsync()));
            await api.StartAsync();
            if (administrationEnabled)
            {
                var administration = AdministrationHost.Create(
                    services,
                    worldPath,
                    administrationPort,
                    administrationOrigin,
                    IPAddress.Parse(options.ListenAddress),
                    options.CertificatePath
                );
                lifetime.OnStop("administration", administration.DisposeAsync);
                lifetime.OnStop("administration listener", () => new ValueTask(administration.StopAsync()));
                await administration.StartAsync();
                string address = string.IsNullOrEmpty(administrationOrigin)
                    ? $"http://127.0.0.1:{administrationPort}"
                    : administrationOrigin;
                ServerLog.Console.Information($"Administration available at {address}.");
            }
            else if (!args.Contains("--disable-admin") && !string.IsNullOrEmpty(administrationOrigin))
            {
                ServerLog.Console.Information("Provision an administrator with --set-admin-password to start the administration listener.");
            }
            ServerLog.Console.Information($"Launcher API listening on {options.ListenAddress}:{options.ApiPort}.");
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
            if (!server.IsCompleted)
            {
                population.Start();
            }
            if (await Task.WhenAny(server, chat.Completion) == chat.Completion)
            {
                await chat.Completion;
                throw new InvalidOperationException("Chat stopped before the game listener.");
            }
            joined = true;
            await server;
        });
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
