using System.ComponentModel;
using System.Diagnostics;
using Eden.Infrastructure.Diagnostics;
using Eden.Server.Capsuleers;
using Eden.World.Market;
using Eden.World.Storage;

namespace Eden.Server.Administration;

internal sealed class OptionalDataImporter(WorldDatabase database)
{
    internal Task Import(OptionalDataset dataset, Action<string> progress, CancellationToken cancellationToken) =>
        dataset switch
        {
            OptionalDataset.Market => ImportMarket(progress, cancellationToken),
            OptionalDataset.Traffic => TrafficImportCommand.ImportPublished(
                database.CatalogPath,
                database.TrafficSnapshotPath,
                null,
                null,
                progress,
                cancellationToken
            ),
            _ => throw new ArgumentOutOfRangeException(nameof(dataset)),
        };

    private async Task ImportMarket(Action<string> progress, CancellationToken cancellationToken)
    {
        var snapshot = database.MarketSnapshot;
        string root = snapshot.Root;
        string staging = Directory.CreateDirectory(Path.Combine(root, ".import-" + Guid.NewGuid().ToString("N"))).FullName;
        try
        {
            progress("Downloading and validating the latest EVE Ref market orders…");
            await RunMarketImporter(database.CatalogPath, staging, cancellationToken);
            PublishMarketSnapshot(snapshot, staging, cancellationToken);
        }
        finally
        {
            DeleteStaging(staging);
        }
    }

    internal static void PublishMarketSnapshot(LiveMarketSnapshotCatalog snapshot, string staging, CancellationToken cancellationToken)
    {
        var manifest = snapshot.ReadManifest(staging);
        cancellationToken.ThrowIfCancellationRequested();
        string generation = Path.Combine(snapshot.Root, manifest.Generation);
        Directory.Move(Path.Combine(staging, manifest.Generation), generation);
        try
        {
            File.Move(Path.Combine(staging, "manifest.json"), Path.Combine(snapshot.Root, "manifest.json"), overwrite: true);
        }
        catch
        {
            DeleteStaging(generation);
            throw;
        }
    }

    private static async Task RunMarketImporter(string catalog, string staging, CancellationToken cancellationToken)
    {
        string python = Environment.GetEnvironmentVariable("EDEN_PYTHON") ?? (OperatingSystem.IsWindows() ? "python" : "python3");
        ProcessStartInfo start = new(python)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        start.Environment["TMPDIR"] = staging;
        start.Environment["TEMP"] = staging;
        start.Environment["TMP"] = staging;
        start.ArgumentList.Add("-I");
        start.ArgumentList.Add(Path.Combine(AppContext.BaseDirectory, "data-tools", "Import-LiveMarket.py"));
        start.ArgumentList.Add("--catalog");
        start.ArgumentList.Add(catalog);
        start.ArgumentList.Add("--output");
        start.ArgumentList.Add(staging);
        using Process process = new() { StartInfo = start };
        try
        {
            process.Start();
        }
        catch (Win32Exception error)
        {
            throw new InvalidDataException(
                "Market imports need Python 3.8 or newer. Install Python or set EDEN_PYTHON on the server.",
                error
            );
        }
        var output = process.StandardOutput.BaseStream.CopyToAsync(Stream.Null);
        var errors = process.StandardError.ReadToEndAsync();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromMinutes(30));
        try
        {
            await process.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException)
        {
            try
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                }
            }
            catch (InvalidOperationException) when (process.HasExited)
            {
                // The process exited between the check and the kill.
            }
            await process.WaitForExitAsync(CancellationToken.None);
            await Task.WhenAll(output, errors);
            cancellationToken.ThrowIfCancellationRequested();
            throw new InvalidDataException("The EVE Ref market import timed out.");
        }
        await output;
        string errorText = await errors;
        if (process.ExitCode != 0)
        {
            ServerLog.Console.Error($"EVE Ref market importer exited with code {process.ExitCode}: {errorText}");
            throw new InvalidDataException("The EVE Ref download or validation failed. Check the server log for details and try again.");
        }
    }

    private static void DeleteStaging(string path)
    {
        try
        {
            Directory.Delete(path, recursive: true);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            ServerLog.Console.Error($"Could not remove optional market import files at {path}: {error.Message}");
        }
    }
}
