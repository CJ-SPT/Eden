using System.Globalization;
using Eden.Data.Catalog;
using Eden.Gameplay.Inventory;
using Eden.World.Catalog;

namespace Eden.World.Docking;

internal static class StationCapabilities
{
    internal static bool Has(CatalogDbContext catalog, long operationId, StationService service) =>
        catalog.Definitions.Find(CatalogData.Client.Stationoperations, operationId)?.Services?.Contains((long)service) == true;
}
