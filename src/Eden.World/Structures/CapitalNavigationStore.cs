using Eden.Data.World.Alliances;
using Eden.Data.World.Inventory;
using Eden.Data.World.Sovereignty;
using Eden.Data.World.Structures;
using Eden.Gameplay.Dogma;
using Eden.Gameplay.Flight;
using Eden.Gameplay.Propulsion;
using Eden.Gameplay.Sovereignty;
using Eden.Gameplay.Structures;
using Eden.Infrastructure.Registration;
using Eden.World.Catalog;
using Eden.World.Runtime;
using Eden.World.Runtime.Economy;
using Eden.World.Storage;

namespace Eden.World.Structures;

[Injectable]
internal sealed class CapitalNavigationStore(WorldDatabase database, TimeProvider clock) : ICapitalNavigation
{
    public IReadOnlyList<StructureCynoBeacon> AccessibleBeacons(long? pilotId)
    {
        if (LiveWorld.Find(database.Path) is not { Economy: var economy })
        {
            return [];
        }
        using CatalogDbContext catalog = new(database.CatalogPath);
        long now = clock.GetUtcNow().UtcDateTime.ToFileTimeUtc();
        return economy.Read(
            world =>
            {
                var beacons = world.Filter<SavedStructure>(row => row.TypeId == CapitalTravelRules.Pharolux).ToArray();
                foreach (int system in beacons.Select(row => row.SystemId).Distinct())
                {
                    SettleFuel(world, catalog, system, now);
                }
                return beacons
                    .Where(row => CanUseBeacon(world, catalog, row, pilotId, now))
                    .OrderBy(row => row.Id)
                    .Select(row => new StructureCynoBeacon(row.Id, row.TypeId, row.CorporationId, row.SystemId, row.State, row.Name))
                    .ToArray();
            },
            new(pilots: pilotId is long id ? [id] : [])
        );
    }

    public long? JammerOnlineAt(int systemId)
    {
        if (LiveWorld.Find(database.Path) is not { Economy: var economy })
        {
            return null;
        }
        using CatalogDbContext catalog = new(database.CatalogPath);
        long now = clock.GetUtcNow().UtcDateTime.ToFileTimeUtc();
        return economy.Read(
            world =>
            {
                SettleFuel(world, catalog, systemId, now);
                return JammerOnlineAt(world, catalog, systemId, now);
            },
            new(systems: [systemId])
        );
    }

    internal static int? ServiceType(StructureFamily family) =>
        family switch
        {
            StructureFamily.CynoBeacon => CapitalTravelRules.PharoluxService,
            StructureFamily.CynoJammer => CapitalTravelRules.TenebrexService,
            _ => null,
        };

    internal static void CheckPlacement(WorldAction world, int typeId, int systemId, int corporationId)
    {
        if (typeId is not (CapitalTravelRules.Pharolux or CapitalTravelRules.Tenebrex))
        {
            return;
        }
        if (world is not EconomyTransaction || !HasUpgrade(world, typeId, systemId, corporationId))
        {
            throw new InvalidDataException("Deploy in an alliance-held system with the required online sovereignty upgrade.");
        }
        int limit = typeId == CapitalTravelRules.Pharolux ? 1 : CapitalTravelRules.MaximumJammers;
        if (
            world
                .Filter<SavedStructure>(row => row.SystemId == systemId && row.TypeId == typeId)
                .Count(row => row.State != StructureState.Destroyed && row.State != StructureState.Unanchored) >= limit
        )
        {
            throw new InvalidDataException("This system already has the maximum number of this navigation structure.");
        }
    }

    internal static void CheckOnlining(WorldAction world, SavedStructure structure, long now)
    {
        if (world is not EconomyTransaction || !HasUpgrade(world, structure.TypeId, structure.SystemId, structure.CorporationId))
        {
            throw new InvalidDataException("An online sovereignty upgrade is required to use this navigation structure.");
        }
        if (
            structure.TypeId == CapitalTravelRules.Tenebrex
            && Structures(world, structure.SystemId, CapitalTravelRules.Tenebrex)
                .AsEnumerable()
                .Any(row => row.Id != structure.Id && OnlineService(world, row, CapitalTravelRules.TenebrexService, now) is not null)
        )
        {
            throw new InvalidDataException("Another cynosural jammer is already online or starting in this system.");
        }
    }

    internal static void StopUnavailableServices(WorldAction world, int? systemId, long now)
    {
        if (world is not EconomyTransaction)
        {
            return;
        }
        foreach (
            var structure in Structures(world, systemId, CapitalTravelRules.Pharolux)
                .Concat(Structures(world, systemId, CapitalTravelRules.Tenebrex))
        )
        {
            foreach (var service in world.Filter<SavedStructureService>(row => row.StructureId == structure.Id && row.Online))
            {
                if (!HasUpgrade(world, structure.TypeId, structure.SystemId, structure.CorporationId))
                {
                    service.Online = false;
                    service.OnlineSince = null;
                    service.NextFuelAt = null;
                    world.Set<OwnedItem>().Find(service.ItemId)!.Online = false;
                    structure.LowPowerAt ??= now;
                }
            }
        }
    }

    internal static bool CanUseBeacon(WorldAction world, CatalogDbContext catalog, SavedStructure structure, long? pilotId, long now) =>
        structure.TypeId == CapitalTravelRules.Pharolux
        && OnlineService(world, structure, CapitalTravelRules.PharoluxService, now) is not null
        && !Jammed(world, catalog, structure.SystemId, now)
        && (pilotId is null || PilotStructureStore.Access(world, structure, pilotId.Value));

    internal static bool Jammed(WorldAction world, CatalogDbContext catalog, int systemId, long now) =>
        JammerOnlineAt(world, catalog, systemId, now) is long at && at <= now;

    private static long? JammerOnlineAt(WorldAction world, CatalogDbContext catalog, int systemId, long now)
    {
        foreach (var structure in Structures(world, systemId, CapitalTravelRules.Tenebrex))
        {
            if (OnlineService(world, structure, CapitalTravelRules.TenebrexService, now)?.OnlineSince is long since)
            {
                return SpatialModuleRules.Deadline(
                    since,
                    catalog.FittingAttributes(CapitalTravelRules.TenebrexService)[DogmaAttributeId.CynoJammerActivationDelay]
                );
            }
        }
        return null;
    }

    internal static void SettleFuel(WorldAction world, CatalogDbContext catalog, int systemId, long now)
    {
        if (
            Structures(world, systemId, CapitalTravelRules.Pharolux)
                .Concat(Structures(world, systemId, CapitalTravelRules.Tenebrex))
                .Any(structure =>
                    structure.UnanchorAt <= now
                    || world
                        .Filter<SavedStructureService>(row => row.StructureId == structure.Id && row.Online)
                        .Any(row => row.NextFuelAt <= now)
                )
        )
        {
            PilotStructureStore.Advance(world, catalog, now, systemId);
        }
    }

    private static IQueryable<SavedStructure> Structures(WorldAction world, int? systemId, int typeId) =>
        systemId is int system
            ? world.Filter<SavedStructure>(row => row.SystemId == system && row.TypeId == typeId)
            : world.Filter<SavedStructure>(row => row.TypeId == typeId);

    private static SavedStructureService? OnlineService(WorldAction world, SavedStructure structure, int typeId, long now) =>
        StructureRules.Operational(structure.State)
        && structure.HasCore
        && !(structure.UnanchorAt <= now)
        && HasUpgrade(world, structure.TypeId, structure.SystemId, structure.CorporationId)
            ? world
                .Filter<SavedStructureService>(row => row.StructureId == structure.Id && row.Online)
                .AsEnumerable()
                .FirstOrDefault(row => world.Set<OwnedItem>().Find(row.ItemId)?.TypeId == typeId)
            : null;

    private static bool HasUpgrade(WorldAction world, int typeId, int system, int corporation)
    {
        int upgrade =
            typeId == CapitalTravelRules.Pharolux
                ? CapitalTravelRules.CynosuralNavigationUpgrade
                : CapitalTravelRules.CynosuralSuppressionUpgrade;
        return world.Set<SavedSovereigntyClaim>().Find(system) is { } claim
            && (claim.CorporationId == corporation || world.Set<SavedAllianceMember>().Find(corporation)?.AllianceId == claim.AllianceId)
            && world
                .Filter<SavedSovereigntyUpgrade>(row => row.HubId == claim.HubId)
                .Any(row => row.TypeId == upgrade && row.PowerState == SovereigntyUpgradePower.Online);
    }
}
