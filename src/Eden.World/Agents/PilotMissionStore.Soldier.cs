using Eden.Data.World.Agents;
using Eden.Data.World.Characters;
using Eden.Data.World.Combat;
using Eden.Data.World.Combat.Npcs;
using Eden.Data.World.Inventory;
using Eden.Gameplay.Agents;
using Eden.Gameplay.Characters;
using Eden.Gameplay.Combat;
using Eden.Gameplay.Docking;
using Eden.Gameplay.Flight;
using Eden.Gameplay.Inventory;
using Eden.World.Catalog;
using Eden.World.Characters;
using Eden.World.Combat;
using Eden.World.Combat.Npcs;
using Eden.World.Fittings;
using Eden.World.Runtime.Economy;

namespace Eden.World.Agents;

internal sealed partial class PilotMissionStore
{
    private bool ApplyCareerAreaObjective(
        WorldAction world,
        CatalogDbContext catalog,
        Pilot pilot,
        SavedMission row,
        MissionProfile profile,
        MissionSite site,
        StaticSpaceObject target,
        SpacePosition position,
        long now
    )
    {
        var ship = pilot.ActiveShip;
        var location = ship.Position!;
        var hull = catalog.RequireItem(ship.TypeId);
        var visit = profile.Definition.CareerVisit!;
        bool inVisitRange = position.DistanceTo(target.Position) - (hull.Radius ?? 0) - target.Radius <= visit.Range;
        if (profile.Definition.CareerSacrifice)
        {
            if (!inVisitRange || ship.TypeId != profile.CareerGift?.TypeId || world.Set<ShipLoss>().Find(ship.Id) is not null)
            {
                return false;
            }
            if (visit.RequiredCargoTypeId is int cargoType)
            {
                bool hasCargo = world
                    .Filter<OwnedItem>(item =>
                        item.OwnerId == pilot.Id
                        && item.LocationId == ship.Id
                        && item.FlagId == (int)InventoryFlag.Cargo
                        && item.TypeId == cargoType
                        && item.Quantity > 0
                    )
                    .Any();
                if (!hasCargo)
                {
                    return false;
                }
                _ = MissionGatePayment.Pay(world, catalog, pilot.Id, ship.Id, new(cargoType, Consume: true), unlocked: false);
            }
            _ = Damage(target, DamageVector.None, forceLoss: true);
            row.ObjectiveComplete = true;
            foreach (var guard in world.Filter<WorldNpc>(npc => npc.EncounterId == MissionRules.FirstSiteId + row.Id))
            {
                guard.RetiredAt ??= now;
            }
            return true;
        }
        if (hull.GroupId == FleetDefinitions.CapsuleGroup)
        {
            return false;
        }
        if (visit.ThermalDamage > 0 && inVisitRange)
        {
            return Damage(target, new DamageVector(0, 0, 0, visit.ThermalDamage));
        }
        if (profile.Definition.CareerHazard is { } hazard && !inVisitRange && site.CareerHazard is { } zone)
        {
            var last = world.Set<ShipHealthState>().Find(ship.Id)?.UpdatedAt;
            if (
                position.DistanceTo(zone.Position) <= hazard.Range
                && (last is null || now - last >= hazard.IntervalSeconds * TimeSpan.TicksPerSecond)
            )
            {
                var source = site.Objects.Single(o => o.Id == zone.ObjectId);
                return Damage(source, new DamageVector(hazard.ElectromagneticDamage, 0, 0, 0));
            }
        }
        return false;

        bool Damage(StaticSpaceObject source, DamageVector damage, bool forceLoss = false)
        {
            if (world.Set<ShipLoss>().Find(ship.Id) is not null)
            {
                return false;
            }
            var defense = ShipHealth.DefenseFrom(PilotFittingStore.ShipAttributes(world, catalog, pilot));
            var current = forceLoss ? PilotHealthStore.Read(world, ship.Id, defense, now).Remaining : null;
            var result = current is not null
                ? new DamageResult(new(0, 0, 0), current.Shield + current.Armor + current.Structure)
                : PilotHealthStore.Apply(world, ship.Id, defense, damage, now);
            if (result.Applied <= 0 && !forceLoss)
            {
                return false;
            }
            var attacker = new CombatParty(source.Id, null, source.OwnerId, null, source.TypeId, source.TypeId, null, 0);
            PilotInteractionHistoryStore.Damage(world, ship.Id, attacker, result.Applied);
            PilotLossResult? loss = result.Remaining.Destroyed
                ? losses.Apply(
                    world,
                    catalog,
                    pilot,
                    ship.Id,
                    site.SystemId,
                    new()
                    {
                        SystemId = location.SystemId,
                        X = position.X,
                        Y = position.Y,
                        Z = position.Z,
                    },
                    now,
                    attacker
                )
                : null;
            DockedPilot? replacement = null;
            if (loss is not null)
            {
                world.SaveChanges();
                replacement = lookup.FindPilot(world, pilot.AccountId, pilot.Id);
            }
            var notification = new PilotDamage(
                pilot.Id,
                0,
                source.Id,
                new(ship.Id, defense, result.Remaining),
                damage,
                result.Applied,
                1,
                source.TypeId,
                false,
                replacement,
                loss?.Insurance
            )
            {
                InventoryChanges = loss?.Changes ?? [],
            };
            world.AfterCommit(() => damageEvents.Publish(notification));
            return true;
        }
    }

    internal static void CareerNpcShipLoss(WorldAction world, CatalogDbContext catalog, Pilot pilot, long? attackerId)
    {
        if (
            world is not EconomyTransaction
            || attackerId is not long id
            || world.Set<WorldNpc>().Find(id) is not { } attacker
            || attacker.AnchorKind != NpcAnchorKind.Mission
        )
        {
            return;
        }
        var mission = world.Set<SavedMission>().Find(attacker.EncounterId - MissionRules.FirstSiteId);
        if (
            mission is not { State: MissionState.Accepted, ObjectiveComplete: false }
            || mission.PilotId != pilot.Id
            || CareerMissionCatalog.Find(mission.MissionId) is not { CareerShipLoss: true }
            || mission.Wave < 1
        )
        {
            return;
        }
        var profile = MissionCatalog.Find(catalog, mission.MissionId);
        if (pilot.ActiveShip.TypeId == profile.CareerGift?.TypeId)
        {
            mission.ObjectiveComplete = true;
        }
    }

    private static bool TryFailCareerRescue(WorldAction world, CatalogDbContext catalog, SavedMission mission, WorldNpc npc, long now)
    {
        var definition = CareerMissionCatalog.Find(mission.MissionId);
        if (definition?.CareerEffect is { } lesson)
        {
            if (!mission.ObjectiveComplete && npc.TypeId == lesson.TypeId)
            {
                Close(world, mission, MissionState.Failed, now);
            }
            return true;
        }
        if (definition?.CareerRescue != true)
        {
            return definition?.CareerSacrifice == true;
        }
        if (
            mission.ObjectiveComplete
            || world
                .Set<WorldNpc>()
                .Any(n => n.EncounterId == npc.EncounterId && n.Id != npc.Id && n.DestroyedAt == null && n.RetiredAt == null)
        )
        {
            return true;
        }
        if (mission.Wave + 1 < definition.Waves.Length)
        {
            Spawn(world, catalog, mission, mission.Wave + 1, now);
        }
        else
        {
            Close(world, mission, MissionState.Failed, now);
        }
        return true;
    }

    internal static SavedMission? CareerEffectTarget(WorldAction world, Pilot pilot, WorldNpc npc, DogmaEffect effect)
    {
        if (
            world is not EconomyTransaction
            || npc.AnchorKind != NpcAnchorKind.Mission
            || npc.RetiredAt is not null
            || npc.DestroyedAt is not null
        )
        {
            return null;
        }
        var row = world.Set<SavedMission>().Find(npc.EncounterId - MissionRules.FirstSiteId);
        return
            row is { State: MissionState.Accepted, ObjectiveComplete: false }
            && row.PilotId == pilot.Id
            && CareerMissionCatalog.Find(row.MissionId)?.CareerEffect is { } lesson
            && lesson.TypeId == npc.TypeId
            && lesson.Effect == effect
            ? row
            : null;
    }

    internal static bool CompleteCareerEffect(WorldAction world, Pilot pilot, WorldNpc npc, DogmaEffect effect, long now)
    {
        if (CareerEffectTarget(world, pilot, npc, effect) is not { } mission)
        {
            return false;
        }
        mission.ObjectiveComplete = true;
        npc.RetiredAt = now;
        return true;
    }
}
