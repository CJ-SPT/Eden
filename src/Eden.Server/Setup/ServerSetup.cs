using System.Runtime.InteropServices;
using System.Text.Json;
using Eden.Server.Administration;
using Eden.Server.Configuration;
using Eden.Server.Logging;
using Eden.World.Catalog;
using Eden.World.Storage;
using Microsoft.Data.Sqlite;

namespace Eden.Server.Setup;

internal static class ServerSetup
{
    internal const string Option = "--setup";
    private const string CatalogOption = "--catalog";
    private const string BuildCatalogOption = "--build-catalog";
    private const string ArchiveOption = "--sde-archive";
    private const string ImportMarketOption = "--import-market";
    private const string AdministratorOption = "--administrator";
    private const string WorldOption = "--world";
    internal const string PasswordStdinOption = "--admin-password-stdin";

    internal static async Task<int> RunAsync(string[] arguments)
    {
        if (arguments.Length == 2 && arguments.Contains("--help"))
        {
            Console.WriteLine(
                """
                Eden.Server --setup [--catalog <file> | --build-catalog <client-folder>] [--sde-archive <zip>]
                                    [--import-market] [--administrator <name> [--admin-password-stdin]] [--world <file>]

                Install a verified EVE build 3662168 catalog or reuse the one beside the world file.
                --build-catalog generates it on Windows from the pinned EVE client and its ResFiles cache.
                --import-market prepares the Standard market using Python 3.8 or newer.
                --administrator provisions the first local administrator. By default Eden prompts without echoing.
                """
            );
            return 0;
        }

        using CancellationTokenSource cancellation = new();
        ConsoleCancelEventHandler cancel = (_, args) =>
        {
            args.Cancel = true;
            cancellation.Cancel();
        };
        Console.CancelKeyPress += cancel;
        using var terminate = OperatingSystem.IsWindows()
            ? null
            : PosixSignalRegistration.Create(
                PosixSignal.SIGTERM,
                context =>
                {
                    context.Cancel = true;
                    cancellation.Cancel();
                }
            );
        try
        {
            await using var logging = BufferedServerLog.Start();
            var options = Parse(arguments);
            string world = ServerDataPaths.WorldPath(
                options.GetValueOrDefault(WorldOption),
                Environment.GetEnvironmentVariable("EDEN_DATA_DIR"),
                Directory.GetCurrentDirectory(),
                AppContext.BaseDirectory
            );
            WorldDatabase database = new(world);
            string catalog = database.OriginalCatalogPath;
            if (string.Equals(world, catalog, StringComparison.OrdinalIgnoreCase) || Directory.Exists(world))
            {
                throw new InvalidDataException("--world must name a world database file separate from catalog.sqlite.");
            }

            await PrepareCatalog(options, catalog, cancellation.Token);
            cancellation.Token.ThrowIfCancellationRequested();
            if (options.ContainsKey(ImportMarketOption))
            {
                await PrepareMarket(database, cancellation.Token);
            }
            if (options.GetValueOrDefault(AdministratorOption) is { } name)
            {
                cancellation.Token.ThrowIfCancellationRequested();
                AdministratorAccounts administrators = new(world);
                bool created =
                    !administrators.Provisioned
                    && administrators.TryCreate(name, ServerConsole.ReadAdministratorPassword(options.ContainsKey(PasswordStdinOption)));
                Console.WriteLine(created ? "Local administrator provisioned." : "The local administrator is already provisioned.");
            }

            Console.WriteLine($"Server data is ready for {world}.");
            Console.WriteLine("Start Eden without --setup using the same world path, then follow the console to world administration.");
            return 0;
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            Console.Error.WriteLine("Eden setup canceled.");
            return 1;
        }
        catch (Exception error)
            when (error is InvalidDataException or IOException or UnauthorizedAccessException or SqliteException or ArgumentException)
        {
            Console.Error.WriteLine($"Eden setup failed. {error.Message}");
            return 1;
        }
        finally
        {
            Console.CancelKeyPress -= cancel;
        }
    }

    private static Dictionary<string, string?> Parse(string[] arguments)
    {
        Dictionary<string, string?> options = new(StringComparer.Ordinal);
        for (int index = 0; index < arguments.Length; index++)
        {
            string option = arguments[index];
            if (
                option
                is not (
                    Option
                    or CatalogOption
                    or BuildCatalogOption
                    or ArchiveOption
                    or ImportMarketOption
                    or AdministratorOption
                    or PasswordStdinOption
                    or WorldOption
                )
            )
            {
                throw new InvalidDataException("Unknown setup option. Run Eden with --setup --help for usage.");
            }
            string? value = null;
            if (option is not (Option or ImportMarketOption or PasswordStdinOption))
            {
                if (
                    ++index >= arguments.Length
                    || string.IsNullOrWhiteSpace(arguments[index])
                    || arguments[index].StartsWith("--", StringComparison.Ordinal)
                )
                {
                    throw new InvalidDataException($"{option} requires a value.");
                }
                value = arguments[index];
            }
            if (!options.TryAdd(option, value))
            {
                throw new InvalidDataException($"Use {option} only once.");
            }
        }
        if (options.ContainsKey(BuildCatalogOption) && options.ContainsKey(CatalogOption))
        {
            throw new InvalidDataException("Choose either --catalog or --build-catalog.");
        }
        if (options.ContainsKey(ArchiveOption) && !options.ContainsKey(BuildCatalogOption))
        {
            throw new InvalidDataException("--sde-archive requires --build-catalog.");
        }
        if (options.ContainsKey(PasswordStdinOption) && !options.ContainsKey(AdministratorOption))
        {
            throw new InvalidDataException("--admin-password-stdin requires --administrator <name>.");
        }
        return options;
    }

    private static async Task PrepareCatalog(Dictionary<string, string?> options, string destination, CancellationToken cancellationToken)
    {
        string? source = options.GetValueOrDefault(CatalogOption);
        if (source is not null)
        {
            source = Path.GetFullPath(source);
        }
        string? client = options.GetValueOrDefault(BuildCatalogOption);
        if ((source is null && client is null) || string.Equals(source, destination, PathComparison))
        {
            if (!File.Exists(destination))
            {
                throw new FileNotFoundException($"Catalog missing at {destination}. Supply a matching catalog with --catalog <file>.");
            }
            VerifyCatalog(destination);
            return;
        }

        if (File.Exists(destination) || Directory.Exists(destination))
        {
            throw new IOException($"Catalog already exists at {destination}. Run setup without a catalog source to reuse it.");
        }
        string staging = Directory
            .CreateDirectory(Path.Combine(Path.GetDirectoryName(destination)!, $".catalog-{Guid.NewGuid():N}"))
            .FullName;
        string candidate = Path.Combine(staging, "catalog.sqlite");
        try
        {
            if (source is not null)
            {
                EnsureStandaloneCatalog(source);
                Console.WriteLine($"Copying catalog from {source}.");
                await using (var input = File.OpenRead(source))
                await using (FileStream output = new(candidate, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    await input.CopyToAsync(output, cancellationToken);
                    output.Flush(flushToDisk: true);
                }
                EnsureStandaloneCatalog(source);
                VerifyCatalog(candidate);
            }
            else
            {
                await ClientCatalogBuilder.BuildAsync(client!, options.GetValueOrDefault(ArchiveOption), candidate, cancellationToken);
            }
            cancellationToken.ThrowIfCancellationRequested();
            File.Move(candidate, destination);
            Console.WriteLine($"Installed catalog at {destination}.");
        }
        finally
        {
            Directory.Delete(staging, recursive: true);
        }
    }

    private static void VerifyCatalog(string path)
    {
        EnsureStandaloneCatalog(path);
        Console.WriteLine($"Verifying catalog at {path}.");
        try
        {
            CatalogSources.Verify(path);
        }
        catch (Exception error) when (error is InvalidOperationException or JsonException or KeyNotFoundException or SqliteException)
        {
            throw new InvalidDataException($"The file is not a usable Eden catalog for build {CatalogSources.PinnedBuild}.", error);
        }
    }

    private static void EnsureStandaloneCatalog(string path)
    {
        if (File.Exists(path + "-wal") || File.Exists(path + "-journal"))
        {
            throw new InvalidDataException($"Close the source database and use a standalone catalog file: {path}");
        }
    }

    private static async Task PrepareMarket(WorldDatabase database, CancellationToken cancellationToken)
    {
        try
        {
            var existing = database.MarketSnapshot.ReadManifest();
            Console.WriteLine($"Reusing the installed Standard market snapshot captured at {existing.CapturedAt:u}.");
            return;
        }
        catch (Exception error) when (error is IOException or InvalidDataException or JsonException)
        {
            // The importer publishes a new generation only after validating its staged manifest.
        }
        await new OptionalDataImporter(database).Import(OptionalDataset.Market, Console.WriteLine, cancellationToken);
        Console.WriteLine($"Installed the Standard market snapshot at {database.MarketSnapshot.Root}.");
    }

    private static StringComparison PathComparison =>
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
}
