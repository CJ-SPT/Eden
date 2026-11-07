using System.Globalization;
using Eden.Data.Catalog;
using Eden.Gameplay.Inventory;
using Eden.World.Catalog;

namespace Eden.World.Docking;

internal static class StationCapabilities
{
    internal static bool Has(CatalogDbContext catalog, long operationId, StationService service) =>
        catalog
            .Set<SourceRecord>()
            .Find("client/stationoperations", operationId.ToString(CultureInfo.InvariantCulture))
            ?.Read<ClientStationoperations>()
            .Services?.Contains((long)service) == true;
}
