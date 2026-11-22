using Eden.Data.Catalog;
using Eden.Data.World.Inventory;
using Eden.Gameplay.Capsuleers;
using Eden.Gameplay.Combat;
using Eden.Gameplay.Docking;
using Eden.Gameplay.Inventory;
using Eden.Gameplay.Ships;
using Eden.World.Catalog;
using Eden.World.Combat;
using Eden.World.Corporations;
using Eden.World.Runtime.Economy;
using Eden.World.Skills;

namespace Eden.World.Capsuleers;

internal static class CapsuleerWorkHulls
{
    internal static Ship? Find(
        EconomyTransaction world,
        CatalogDbContext catalog,
        DockedPilot pilot,
        CapsuleerProfession profession,
        long now,
        bool improveCurrent = false
    )
    {
        if (profession is not (CapsuleerProfession.Mining or CapsuleerProfession.Combat) || !CanLeaveShip(world, catalog, pilot))
        {
            return null;
        }

        long owner = pilot.PilotId;
        int station = pilot.StationId;
        long activeShip = pilot.ShipId;
        int minimumStage = improveCurrent ? Stage(catalog, pilot.ShipTypeId, profession) : 0;
        var candidates = world
            .Filter<Ship>(ship => ship.OwnerId == owner && ship.StationId == station)
            .Where(ship => ship.Id != activeShip && ship.StructureId == null && ship.StoredInId == null && ship.Position == null)
            .AsEnumerable()
            .Select(ship => (Ship: ship, Stage: Stage(catalog, ship.TypeId, profession)))
            .Where(candidate => candidate.Stage > minimumStage)
            .OrderByDescending(candidate => candidate.Stage)
            .ThenBy(candidate => candidate.Ship.Id);
        foreach (var (ship, _) in candidates)
        {
            if (CanFly(world, catalog, owner, ship, profession, now))
            {
                return ship;
            }
        }
        return null;
    }

    // Call after checking the current worker's commitments, in the same transaction as Find.
    internal static void Board(EconomyTransaction world, CatalogDbContext catalog, DockedPilot pilot, Ship candidate, long now) =>
        PilotFleetStore.Board(world, catalog, pilot.PilotId, candidate.Id, pilot.ShipId, now);

    internal static bool CanLeaveShip(EconomyTransaction world, CatalogDbContext catalog, DockedPilot pilot)
    {
        if (
            pilot.Position is not null
            || pilot.InStructure
            || pilot.StationId <= 0
            || pilot.ShipGroupId == FleetDefinitions.CapsuleGroup
            || world.Pilots.Find(pilot.PilotId)?.ActiveShipId != pilot.ShipId
        )
        {
            return false;
        }
        long ship = pilot.ShipId;
        // Charge stacks and the drone bay stay with the fitted ship when its pilot switches hulls.
        return !world
                .Filter<OwnedItem>(item => item.LocationId == ship && item.Quantity > 0)
                .AsEnumerable()
                .Any(item =>
                    ShipHoldRules.IsHold(item.FlagId)
                    && item.FlagId != (int)InventoryFlag.DroneBay
                    && catalog.FittingType(item.TypeId)?.CategoryId != (int)InventoryCategory.Charge
                ) && !world.Filter<Ship>(child => child.StoredInId == ship).Any();
    }

    private static int Stage(CatalogDbContext catalog, int type, CapsuleerProfession profession)
    {
        var hull = (HullClass)(catalog.FittingType(type)?.GroupId ?? 0);
        if (CapsuleerCareerTraining.Profession(catalog, type, hull) != profession)
        {
            return 0;
        }
        var definition = catalog.Read(CatalogData.Client.Types, (long)type);
        int tech = (int)(definition?.TechLevel ?? (type == (int)TechOneHullType.Venture ? 1 : 0));
        return CapsuleerCareerTrainingRules.HullStage(profession, hull, tech, definition?.MetaGroupID is null or 1);
    }

    private static bool CanFly(
        EconomyTransaction world,
        CatalogDbContext catalog,
        long owner,
        Ship ship,
        CapsuleerProfession profession,
        long now
    )
    {
        try
        {
            PilotSkillReader.CheckRequirements(world, catalog, owner, ship.TypeId);
            if (profession == CapsuleerProfession.Mining)
            {
                int laser = (int)CapsuleerMiningRules.Laser((CapsuleerMiningHull)ship.TypeId);
                PilotSkillReader.CheckRequirements(world, catalog, owner, laser);
                return world
                    .FittedItems(owner, ship.Id)
                    .Any(item => item.TypeId == laser && item.Online && InventoryDefinitions.IsHighSlot(item.FlagId));
            }

            var profile = CapsuleerDefenseProfile.Read(world, catalog, owner, ship: ship);
            return profile.Combat
                && profile.HasDamage
                && !CapsuleerDefenseRules.Retreat(
                    PilotHealthStore.Read(world, ship.Id, ShipHealth.DefenseFrom(profile.ShipAttributes), now),
                    true
                );
        }
        catch (InvalidDataException)
        {
            // An untrained pilot or unsupported fit can still have another flyable hull at this station.
            return false;
        }
    }
}
