using System.Net;
using System.Text.Json;
using Eden.Gameplay.Redemption;
using Eden.Protocol.Chat;
using Eden.Protocol.Gateway;
using Eden.Protocol.Serialization;
using Eden.Server.Authentication;
using Eden.Server.Catalog;
using Eden.Server.Chat;
using Eden.Server.Gateway;
using Eden.Server.Launcher;
using Eden.Server.Persistence;
using Microsoft.Extensions.DependencyInjection;

namespace Eden.Server;

internal static class Program
{
    private static async Task Main(string[] args)
    {
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
        string? Value(string option)
        {
            int index = Array.IndexOf(args, option);
            return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
        }
        string directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Eden");
        string worldPath = Path.GetFullPath(Value("--world") ?? Path.Combine(directory, "world.sqlite"));
        var options = LauncherOptions.Load(Value("--config"));
        if (args.Contains("--development-login"))
        {
            options = options with { DevelopmentLogin = true };
            options.Validate();
        }
        string? preparationRoot = Value("--prepare-login");
        // Ticket preparation signs a local claim without opening the saved world.
        using var services = ServerServices.Build(new WorldDatabase(worldPath), options: options, liveWorld: preparationRoot is null);
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
            string claim = services.GetRequiredService<DevelopmentIdentity>().Prepare(root, accountId);
            services.GetRequiredService<ChatCertificate>().TrustClient(root);
            Console.WriteLine(claim);
            return;
        }
        Directory.CreateDirectory(Path.GetDirectoryName(worldPath)!);
        WorldInitialization.Initialize(new WorldDatabase(worldPath));
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
        await using var gateway = LocalGateway.Create(services.GetRequiredService<GatewayRequests>(), options);
        await using var api = LauncherApi.Create(
            options,
            accounts,
            services.GetRequiredService<DevelopmentIdentity>(),
            TimeProvider.System,
            services.GetRequiredService<ChatCertificate>().PublicPem
        );
        await using var chat = services.GetRequiredService<ChatServer>();
        await chat.StartAsync(new(IPAddress.Parse(options.ListenAddress), ChatServer.ClientPort));
        await gateway.StartAsync();
        await api.StartAsync();
        Console.WriteLine($"Launcher API listening on {options.ListenAddress}:{options.ApiPort}.");
        Console.WriteLine($"Chat listening on {options.ListenAddress}:{ChatServer.ClientPort} (TLS).");
        try
        {
            await services.GetRequiredService<LoopbackServer>().RunAsync();
        }
        finally
        {
            await api.StopAsync();
            await gateway.StopAsync();
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
