using System.Globalization;
using Eden.Data.World.Inventory;
using Eden.Gameplay.Inventory;
using Eden.Gameplay.Loot;
using Eden.Gameplay.Mining;
using Eden.World.Catalog;
using Eden.World.Inventory;
using Eden.World.Loot;
using Eden.World.Runtime;
using Eden.World.Runtime.Economy;

namespace Eden.World.Mining;

internal sealed partial class PilotAutoMiningStore
{
    private const string HaulKey = "eden.automining.hauling";

    public AutoMiningHaulSettings ReadHauling(long pilotId) => ParseHauling(ReadSetting(pilotId, HaulKey));

    private static AutoMiningHaulSettings ParseHauling(string? saved) =>
        saved?.Split(':') is [var station, var threshold]
        && long.TryParse(station, NumberStyles.None, CultureInfo.InvariantCulture, out long stationId)
        && stationId <= int.MaxValue
        && int.TryParse(threshold, NumberStyles.None, CultureInfo.InvariantCulture, out int percent)
        && percent is >= 1 and <= 100
            ? new(stationId, percent)
            : AutoMiningHaulSettings.Disabled;

    public void SaveHauling(long pilotId, int systemId, AutoMiningHaulSettings hauling)
    {
        CheckHaulStation(systemId, hauling);
        SaveSetting(pilotId, HaulKey, FormatHauling(hauling));
    }

    private static string FormatHauling(AutoMiningHaulSettings hauling) =>
        FormattableString.Invariant($"{hauling.StationId}:{hauling.Threshold}");

    private void CheckHaulStation(int systemId, AutoMiningHaulSettings hauling)
    {
        if (hauling.Enabled && !Stations(systemId).Any(station => station.Id == hauling.StationId))
        {
            throw new InvalidDataException("Choose an NPC station in your current solar system.");
        }
    }

    public IReadOnlyList<AutoMiningStation> Stations(int systemId)
    {
        using CatalogDbContext catalog = new(database.CatalogPath);
        return catalog
            .Definitions.StationsInSystem(systemId)
            .Select(station => new AutoMiningStation(station.Id, catalog.StationName(station.Id)))
            .OrderBy(station => station.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(station => station.Id)
            .ToArray();
    }

    public AutoMiningHold ReadHold(long pilotId, long shipId, MiningFamily family)
    {
        var live = RequireResidentWorld();
        AutoMiningHold Load()
        {
            using CatalogDbContext catalog = new(database.CatalogPath);
            var attributes = ShipAttributes(pilotId);
            return live.Economy.Inspect(
                world =>
                {
                    var ship = ActiveShip(world, pilotId, shipId);
                    var flag = PilotMiningStore.OreHold(catalog, ship.TypeId, family);
                    double capacity = ShipHoldRules.Capacity((int)flag, attributes);
                    var items = PilotInventoryStore.List(world, catalog, pilotId, shipId).Where(item => item.FlagId == (int)flag);
                    var stacks = items.ToArray();
                    return new AutoMiningHold(
                        new(capacity, stacks.Sum(item => item.Volume * item.Quantity)),
                        stacks.Any(item => AutoMiningHold.IsResource(item, family))
                    );
                },
                new(ships: [shipId])
            );
        }

        return live.Reads is { } resident ? resident.AutoMiningHold(pilotId, shipId, family, Load) : Load();
    }

    public IReadOnlyList<InventoryChange> Unload(long pilotId, long shipId, long stationId, MiningFamily family)
    {
        using CatalogDbContext catalog = new(database.CatalogPath);
        using var action = RequireResidentWorld().Economy.Begin(new(pilots: [pilotId]));
        var ship = ActiveShip(action, pilotId, shipId);
        if (ship.Position is not null || ship.StructureId is not null || ship.StationId != stationId)
        {
            throw new InvalidDataException("Dock the active ship at the selected station before unloading.");
        }

        var flag = PilotMiningStore.OreHold(catalog, ship.TypeId, family);
        long[] resources = PilotInventoryStore
            .List(action, catalog, pilotId, shipId)
            .Where(item => item.FlagId == (int)flag && AutoMiningHold.IsResource(item, family))
            .Select(item => item.Id)
            .ToArray();
        if (resources.Length == 0)
        {
            return [];
        }
        var request = new InventoryMoveRequest(resources, shipId, (int)InventoryFlag.Hangar, null);
        var result = PilotInventoryStore.Move(action, catalog, pilotId, stationId, request, clock.GetUtcNow().UtcDateTime.ToFileTimeUtc());
        action.Commit();
        return result;
    }

    public JettisonResult Jettison(long pilotId, long shipId, MiningFamily family)
    {
        using CatalogDbContext catalog = new(database.CatalogPath);
        using var action = RequireResidentWorld().Economy.Begin(new(pilots: [pilotId], ships: [shipId]));
        var ship = ActiveShip(action, pilotId, shipId);
        var flag = PilotMiningStore.OreHold(catalog, ship.TypeId, family);
        var resources = PilotInventoryStore
            .List(action, catalog, pilotId, shipId)
            .Where(item => item.FlagId == (int)flag && AutoMiningHold.IsResource(item, family))
            .Take(JetcanRules.MaximumItemsPerCall)
            .Select(item => item.Id)
            .ToArray();
        var result = JetcanStore.Jettison(
            action,
            catalog,
            pilotId,
            shipId,
            resources,
            clock.GetUtcNow().UtcDateTime.ToFileTimeUtc(),
            allowSplit: true
        );
        action.Commit();
        return result;
    }

    private LiveWorld RequireResidentWorld() =>
        LiveWorld.Find(database.Path) ?? throw new InvalidDataException("AutoMining requires the resident world.");

    private static Ship ActiveShip(WorldAction world, long pilot, long ship) =>
        world.Pilots.Find(pilot) is { ActiveShipId: var id } owner && id == ship
            ? owner.ActiveShip
            : throw new InvalidDataException("Use the active ship's mining hold.");
}
