using System.Security.Claims;
using System.Text.Json;
using Eden.Data.Catalog.Capsuleers;
using Eden.World.Capsuleers;
using Microsoft.Data.Sqlite;

namespace Eden.Server.Administration;

internal sealed record AdminOptionalData(AdminMarketSnapshot Market, TrafficSnapshotManifest? Traffic);

internal sealed partial class AdministrationOperations
{
    internal async Task<AdminOptionalData> OptionalData(ClaimsPrincipal user)
    {
        await Authorize(user);
        var market = ReadMarketSnapshot();
        TrafficSnapshotManifest? traffic = null;
        if (File.Exists(database.TrafficSnapshotPath))
        {
            try
            {
                traffic = TrafficSnapshot.ReadManifest(database.TrafficSnapshotPath, database.Catalog.Identity.SourceFingerprint);
            }
            catch (Exception error)
                when (error is IOException or InvalidDataException or UnauthorizedAccessException or JsonException or SqliteException)
            {
                // The page can still show and replace unavailable data using the import controls.
            }
        }
        return new(market, traffic);
    }

    internal async Task<(OptionalDataImportStatus? Market, OptionalDataImportStatus? Traffic)> OptionalDataStatus(ClaimsPrincipal user)
    {
        await Authorize(user);
        return optionalDataImports.Status();
    }

    internal async Task StartOptionalDataImport(ClaimsPrincipal user, OptionalDataset dataset)
    {
        await Authorize(user);
        optionalDataImports.Start(dataset);
    }
}
