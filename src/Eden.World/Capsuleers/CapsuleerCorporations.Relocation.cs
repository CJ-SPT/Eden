using Eden.Data.World.Corporations;
using Eden.Data.World.Inventory;
using Eden.Gameplay.Capsuleers;
using Eden.Gameplay.Docking;
using Eden.Gameplay.Inventory;
using Eden.Gameplay.Ships;
using Eden.World.Catalog;
using Eden.World.Inventory;
using Eden.World.Runtime.Economy;

namespace Eden.World.Capsuleers;

internal sealed record RelocationCargo(long ItemId, int Quantity);

internal sealed record CapsuleerRelocation(int CorporationId, int HeadquartersId, int DestinationId)
{
    public long ShipId { get; init; }
    public int SourceId { get; init; }
    public int[] Systems { get; init; } = [];
    public RelocationCargo[] Cargo { get; init; } = [];
}

internal static partial class CapsuleerCorporations
{
    private const int MaximumSupplyStacks = 3;

    private static CapsuleerOrganization ChooseOperatingBase(
        CapsuleerOrganization organization,
        CatalogDbContext catalog,
        int source,
        SavedCorporation corporation,
        HullClass hull,
        CapsuleerProfession profession
    )
    {
        int destination = corporation.StationId;
        if (profession == CapsuleerProfession.Mining)
        {
            var site = CapsuleerProvisioningCatalog.Mining(catalog, corporation.SystemId);
            if (site.Eligibility != CapsuleerEligibility.Eligible || site.HomeStationId is not int miningStation)
            {
                return organization;
            }
            destination = miningStation;
        }
        if (source == destination)
        {
            return organization with { OperatingStationId = destination };
        }
        return RelocationRoute(catalog, source, destination, hull) is not null
            ? organization with
            {
                Relocation = new(corporation.Id, corporation.StationId, destination),
            }
            : organization;
    }

    internal static CapsuleerTrafficRoute? StartRelocation(WorldAction world, CatalogDbContext catalog, DockedPilot pilot, long now)
    {
        var organization = Organization(world, pilot.PilotId);
        if (organization.Relocation is not { } move || pilot.Position is not null || pilot.ShipGroupId == FleetDefinitions.CapsuleGroup)
        {
            return null;
        }
        if (
            pilot.CorpId != move.CorporationId
            || world.Set<SavedCorporation>().Find(move.CorporationId) is not { ClosedAt: null } corporation
            || corporation.StationId != move.HeadquartersId
        )
        {
            SaveOrganization(world, pilot.PilotId, organization with { Relocation = null });
            return null;
        }
        if (pilot.StationId == move.DestinationId)
        {
            FinishRelocation(world, catalog, pilot, now);
            return null;
        }
        var route = RelocationRoute(catalog, pilot.StationId, move.DestinationId, (HullClass)pilot.ShipGroupId);
        if (route is null)
        {
            SaveOrganization(world, pilot.PilotId, organization with { Relocation = null });
            return null;
        }
        var cargo = move.ShipId == pilot.ShipId ? move.Cargo : PackRelocation(world, catalog, pilot, now);
        SaveOrganization(
            world,
            pilot.PilotId,
            organization with
            {
                Relocation = move with { ShipId = pilot.ShipId, SourceId = pilot.StationId, Systems = route.Systems, Cargo = cargo },
            }
        );
        return route;
    }

    internal static bool FinishRelocation(WorldAction world, CatalogDbContext catalog, DockedPilot pilot, long now)
    {
        var organization = Organization(world, pilot.PilotId);
        if (
            organization.Relocation is not { } move
            || pilot.Position is not null
            || pilot.StationId != move.DestinationId
            || pilot.CorpId != move.CorporationId
            || pilot.ShipGroupId == FleetDefinitions.CapsuleGroup
        )
        {
            return false;
        }
        if (move.ShipId == pilot.ShipId)
        {
            foreach (var cargo in move.Cargo)
            {
                if (
                    world.Set<OwnedItem>().Find(cargo.ItemId) is { } item
                    && item.OwnerId == pilot.PilotId
                    && item.LocationId == pilot.ShipId
                    && item.FlagId == (int)InventoryFlag.Cargo
                )
                {
                    PilotInventoryStore.Move(
                        world,
                        catalog,
                        pilot.PilotId,
                        pilot.StationId,
                        new([item.Id], pilot.ShipId, (int)InventoryFlag.Hangar, Math.Min(item.Quantity, cargo.Quantity)),
                        now
                    );
                }
            }
        }
        SaveOrganization(
            world,
            pilot.PilotId,
            organization with
            {
                OperatingStationId = pilot.StationId,
                Relocation = null,
                RelocationsCompleted = checked(organization.RelocationsCompleted + 1),
            }
        );
        return true;
    }

    private static CapsuleerTrafficRoute? RelocationRoute(CatalogDbContext catalog, int source, int destination, HullClass hull) =>
        catalog.FindStation(source) is not null && catalog.FindStation(destination) is not null
            ? CapsuleerTrafficRoutes.ToStation(catalog, source, destination, hull, maximumJumps: CapsuleerRecruitmentRules.MaximumJumps)
            : null;

    private static RelocationCargo[] PackRelocation(WorldAction world, CatalogDbContext catalog, DockedPilot pilot, long now)
    {
        const int cargoFlag = (int)InventoryFlag.Cargo;
        var supplies = PilotInventoryStore
            .List(world, catalog, pilot.PilotId, pilot.StationId)
            .Where(item =>
                item.FlagId == (int)InventoryFlag.Hangar
                && !item.Singleton
                && item.Volume > 0
                && double.IsFinite(item.Volume)
                && (InventoryCategory)item.CategoryId is InventoryCategory.Charge or InventoryCategory.Module or InventoryCategory.Drone
            )
            .Take(MaximumSupplyStacks)
            .ToArray();
        if (supplies.Length == 0)
        {
            return [];
        }
        double free =
            PilotInventoryStore.HoldCapacity(world, catalog, pilot.PilotId, pilot.ShipId, cargoFlag)
            - InventoryPersistence.UsedVolume(world, catalog, pilot.PilotId, pilot.ShipId, cargoFlag);
        List<RelocationCargo> cargo = [];
        foreach (var supply in supplies)
        {
            int quantity = (int)Math.Min(supply.Quantity, Math.Max(0, Math.Floor(free / supply.Volume)));
            if (quantity == 0)
            {
                continue;
            }
            var moved = PilotInventoryStore.Move(
                world,
                catalog,
                pilot.PilotId,
                pilot.ShipId,
                new([supply.Id], pilot.StationId, cargoFlag, quantity),
                now
            );
            cargo.Add(new(moved.Single(change => change.Item.LocationId == pilot.ShipId).Item.Id, quantity));
            free -= quantity * supply.Volume;
        }
        return cargo.ToArray();
    }
}
