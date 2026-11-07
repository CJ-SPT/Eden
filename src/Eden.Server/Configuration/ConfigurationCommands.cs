using System.Globalization;
using Eden.World.Catalog;
using Eden.World.Configuration;
using Eden.World.Storage;

namespace Eden.Server.Configuration;

internal static class ConfigurationCommands
{
    internal static bool TryRun(string[] args, string worldPath)
    {
        bool history = args.Contains("--configuration-history");
        foreach (string command in new[] { "--configuration-export", "--configuration-import", "--configuration-restore" })
        {
            int index = Array.IndexOf(args, command);
            if (index >= 0 && (index + 1 >= args.Length || args[index + 1].StartsWith("--", StringComparison.Ordinal)))
            {
                throw new InvalidDataException($"{command} requires a value.");
            }
        }
        string? Value(string option)
        {
            int index = Array.IndexOf(args, option);
            return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
        }
        string? export = Value("--configuration-export");
        string? import = Value("--configuration-import");
        string? restore = Value("--configuration-restore");
        if (!history && export is null && import is null && restore is null)
        {
            return false;
        }
        if ((history ? 1 : 0) + (export is null ? 0 : 1) + (import is null ? 0 : 1) + (restore is null ? 0 : 1) != 1)
        {
            throw new InvalidDataException("Choose one configuration command.");
        }
        if (!File.Exists(worldPath))
        {
            throw new FileNotFoundException("Configuration commands require an existing saved world.", worldPath);
        }
        // A running server owns this file. Its authenticated editor is the live write boundary.
        using FileStream ownership = new(worldPath + ".owner", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        WorldDatabase database = new(worldPath);
        using (WorldDbContext world = new(worldPath))
        using (CatalogDbContext catalog = new(database.CatalogPath))
        {
            WorldMigration.Open(world, catalog, TimeProvider.System);
        }
        WorldConfigurationStore.LoadActive(database);
        WorldConfigurationStore store = new(database, TimeProvider.System);
        var state = store.Read();
        if (history)
        {
            Console.WriteLine($"Active revision {state.Active.Id}; pending revision {state.Pending?.Id.ToString() ?? "none"}.");
            foreach (var revision in store.History())
            {
                Console.WriteLine($"{revision.Id}: {revision.Author} ({DateTime.FromFileTimeUtc(revision.CreatedAt):u})");
            }
            if (state.ActivationFailure is { } failure)
            {
                Console.WriteLine(failure);
            }
        }
        else if (export is not null)
        {
            string output = Value("--output") ?? throw new InvalidDataException("Export requires --output <file>.");
            File.WriteAllText(output, store.Export(long.Parse(export, CultureInfo.InvariantCulture)));
            Console.WriteLine($"Exported configuration revision {export}.");
        }
        else
        {
            var saved = import is not null
                ? store.Import(state.Generation, File.ReadAllText(import), "Local import command")
                : store.Restore(state.Generation, long.Parse(restore!, CultureInfo.InvariantCulture), "Local restore command");
            Console.WriteLine($"Saved revision {saved.Pending!.Id}; pending restart.");
        }
        return true;
    }
}
