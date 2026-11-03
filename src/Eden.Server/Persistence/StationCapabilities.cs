using System.Globalization;
using Eden.Gameplay.Inventory;
using Eden.Server.Catalog;

namespace Eden.Server.Persistence;

internal static class StationCapabilities
{
    internal static bool Has(CatalogDbContext catalog, long operationId, StationService service) =>
        catalog
            .Set<SourceRecord>()
            .Find("client/stationoperations", operationId.ToString(CultureInfo.InvariantCulture))
            ?.Read<ClientStationoperations>()
            .Services?.Contains((long)service) == true;
}
