using System.Text.Json;
using Eden.Protocol.Gateway;
using Eden.Protocol.Serialization;
using Eden.Server.Authentication;
using Eden.Server.Catalog;
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
        using var services = ServerServices.Build(new WorldDatabase(worldPath), options: options);
        if (Value("--prepare-login") is { } root && OperatingSystem.IsWindows())
        {
            if (!options.DevelopmentLogin)
            {
                throw new InvalidDataException("Pass --development-login explicitly for local development tickets.");
            }
            long accountId = args.Contains("Wing") ? 1000002 : 1000001;
            Console.WriteLine(services.GetRequiredService<DevelopmentIdentity>().Prepare(root, accountId));
            return;
        }
        Directory.CreateDirectory(Path.GetDirectoryName(worldPath)!);
        WorldInitialization.Initialize(new WorldDatabase(worldPath));
        var accounts = services.GetRequiredService<LauncherStore>();
        accounts.Initialize();
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
            TimeProvider.System
        );
        await gateway.StartAsync();
        await api.StartAsync();
        Console.WriteLine($"Launcher API listening on {options.ListenAddress}:{options.ApiPort}.");
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
