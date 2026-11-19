using Eden.Data.Catalog;
using Eden.Data.World.Inventory;
using Eden.Gameplay.Inventory;
using Eden.Gameplay.Structures;
using Eden.World.Catalog;
using Eden.World.Runtime.Economy;

namespace Eden.World.Capsuleers;

internal sealed partial class CapsuleerMiner
{
    private bool MoonOreReadyToRefine(WorldAction world, CatalogDbContext catalog) =>
        world
            .Filter<OwnedItem>(item => item.LocationId == Pilot.ShipId && item.OwnerId == pilotId)
            .AsEnumerable()
            .Any(item =>
                ShipHoldRules.IsHold(item.FlagId)
                && MoonMiningRules.OreTypes.Contains(item.TypeId)
                && catalog.Read(CatalogData.Client.Types, (long)item.TypeId)?.PortionSize is > 0 and var portion
                && item.Quantity >= portion
            );
}
