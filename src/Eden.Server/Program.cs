using Eden.Server.Authentication;
using Eden.Server.Persistence;
using Microsoft.Extensions.DependencyInjection;

namespace Eden.Server;

internal static class Program
{
    private static async Task Main(string[] args)
    {
        string directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Eden");
        using var services = ServerServices.Build(new WorldDatabase(Path.Combine(directory, "world.sqlite")));
        if (args is ["--prepare-login", string root] && OperatingSystem.IsWindows())
        {
            Console.WriteLine(services.GetRequiredService<DevelopmentIdentity>().Prepare(root));
            return;
        }
        Directory.CreateDirectory(directory);
        services.GetRequiredService<SqliteWorld>();
        await services.GetRequiredService<LoopbackServer>().RunAsync();
    }
}
