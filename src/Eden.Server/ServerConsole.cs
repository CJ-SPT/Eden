using System.Runtime.InteropServices;
using Eden.Server.Setup;
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

        Console.Error.WriteLine(
            "Run Eden with --setup --catalog <file> to install a verified catalog. Use the same --world and data directory."
        );
        Console.Error.WriteLine("On Windows, use --setup --build-catalog <client-folder> to generate the catalog from the pinned client.");
        Console.Error.WriteLine("Run Eden with --setup --help for usage, then start Eden again after setup.");
    }

    internal static string ReadAdministratorPassword(bool fromStdin)
    {
        if (fromStdin != Console.IsInputRedirected)
        {
            throw new InvalidDataException(
                $"Use an interactive terminal or redirect two password lines with {ServerSetup.PasswordStdinOption}."
            );
        }
        string password = Read("Administrator password: ");
        if (password != Read("Confirm password: "))
        {
            throw new InvalidDataException("Passwords do not match.");
        }
        return password;

        string Read(string prompt)
        {
            if (fromStdin)
            {
                return Console.ReadLine()
                    ?? throw new InvalidDataException("Standard input must contain the password and confirmation on separate lines.");
            }
            Console.Write(prompt);
            return ReadPassword();
        }
    }

    internal static string ReadPassword()
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

    [DllImport("kernel32.dll", ExactSpelling = true)]
    private static extern uint GetConsoleProcessList(out uint processId, uint processCount);
}
