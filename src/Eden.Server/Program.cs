using System.Text.Json;
using Eden.Protocol.Gateway;
using Eden.Protocol.Serialization;
using Eden.Server.Authentication;
using Eden.Server.Catalog;
using Eden.Server.Gateway;
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
        string directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Eden");
        using var services = ServerServices.Build(new WorldDatabase(Path.Combine(directory, "world.sqlite")));
        if (args is ["--prepare-login", ..] && OperatingSystem.IsWindows())
        {
            long accountId = args switch
            {
                [_, _] or [_, _, "Pilot"] => 1000001,
                [_, _, "Wing"] => 1000002,
                _ => throw new ArgumentException("Expected a staged root and Pilot or Wing."),
            };
            Console.WriteLine(services.GetRequiredService<DevelopmentIdentity>().Prepare(args[1], accountId));
            return;
        }
        Directory.CreateDirectory(directory);
        services.GetRequiredService<SqliteWorld>();
        await using var gateway = LocalGateway.Create(services.GetRequiredService<GatewayRequests>());
        await gateway.StartAsync();
        try
        {
            await services.GetRequiredService<LoopbackServer>().RunAsync();
        }
        finally
        {
            await gateway.StopAsync();
        }
    }
}
