using System.Runtime.InteropServices;
using Eden.World.Catalog;

namespace Eden.Server;

internal static class ServerConsole
{
    internal static bool ShouldPauseOnError() =>
        OperatingSystem.IsWindows()
        && Environment.UserInteractive
        && Environment.GetEnvironmentVariable("EDEN_NO_PAUSE_ON_ERROR") != "1"
        && !Console.IsInputRedirected
        && !Console.IsOutputRedirected
        && !Console.IsErrorRedirected
        // An existing terminal shares its console with the shell that launched Eden.
        && GetConsoleProcessList(out _, 1) == 1;

    internal static void WriteMissingCatalog(MissingCatalogException error)
    {
        string path = Path.GetFullPath(error.FileName!);
        Console.Error.WriteLine("Eden could not start because its game data catalog (catalog.sqlite) is missing.");
        Console.Error.WriteLine($"Expected file: {path}");
        if (error.IsPackageCatalog)
        {
            Console.Error.WriteLine("Restore the missing catalog from a backup of the active mod or skill package, then start Eden again.");
            return;
        }

        Console.Error.WriteLine("If you already have the matching catalog, copy it to the path above.");
        Console.Error.WriteLine("To build it, run this from the Eden repository in PowerShell with your staged EVE client folder:");
        string output = path.Replace("'", "''", StringComparison.Ordinal);
        Console.Error.WriteLine($"  ./tools/Setup-Catalog.ps1 -DevelopmentRoot '<staged client folder>' -Output '{output}'");
        Console.Error.WriteLine("Then start Eden again. See docs/development-client.md for client staging and catalog setup.");
    }

    [DllImport("kernel32.dll", ExactSpelling = true)]
    private static extern uint GetConsoleProcessList(out uint processId, uint processCount);
}
