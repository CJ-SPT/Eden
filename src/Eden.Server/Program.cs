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
        await services.GetRequiredService<LoopbackServer>().RunAsync();
    }
}
