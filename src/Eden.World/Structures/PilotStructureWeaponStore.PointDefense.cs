using Eden.Data.World.Characters;
using Eden.Data.World.Combat;
using Eden.Data.World.Combat.Npcs;
using Eden.Data.World.Drones;
using Eden.Data.World.Inventory;
using Eden.Data.World.Structures;
using Eden.Gameplay.Ammunition;
using Eden.Gameplay.Combat;
using Eden.Gameplay.Docking;
using Eden.Gameplay.Dogma;
using Eden.Gameplay.Flight;
using Eden.Gameplay.Inventory;
using Eden.Gameplay.Structures;
using Eden.World.Catalog;
using Eden.World.Characters;
using Eden.World.Combat;
using Eden.World.Combat.Npcs;
using Eden.World.Drones;
using Eden.World.FactionWarfare;
using Eden.World.Fittings;
using Eden.World.Runtime.Economy;
using Eden.World.Storage;

namespace Eden.World.Structures;

internal sealed partial class PilotStructureWeaponStore
{
    private sealed record PointDefenseCandidates(int System, long[] Ships, long[] Npcs, long[] Drones, long[] Structures);

    private sealed record PointDefenseResult(
        StructureWeaponActivation Activation,
        IReadOnlyList<PilotDamage> Notifications,
        bool DestroyedNpc
    );

    private PointDefenseCandidates FindPointDefenseCandidates(
        WorldAction world,
        CatalogDbContext catalog,
        long sourceId,
        long module,
        long now
    )
    {
        var source = world.Set<SavedStructure>().Find(sourceId)!;
        int system = source.SystemId;
        if (CrimewatchPersistence.Security(catalog, system) >= StructureRules.HighSecurityBoundary)
        {
            throw new InvalidDataException("Standup point defense cannot operate in high-security space.");
        }
        var fit = StructureEquipmentPersistence.Read(world, catalog, source);
        double range = fit.Equipment.GetValueOrDefault(module)?.GetValueOrDefault(DogmaAttributeId.EmpFieldRange) ?? 0;
        var origin = StructureCombatTargets.Position(source);
        double radius = StructureCatalog.Require(catalog, source.TypeId).Radius;
        bool Near(SpacePosition point, int type) =>
            SmartbombRules.InRange(origin, radius, point, catalog.RequireItem(type).Radius ?? 0, range);
        return new(
            system,
            presence
                .NearbyShips(system, origin)
                .Concat(world.Filter<SavedLogoff>(row => row.SystemId == system && row.ExpiresAt > now).Select(row => row.ShipId))
                .Distinct()
                .Where(id =>
                    world.Ships.Find(id) is { } ship
                    && PointDefensePosition(world, system, id, now) is { } point
                    && Near(point, ship.TypeId)
                )
                .Order()
                .ToArray(),
            world
                .Filter<WorldNpc>(row => row.SystemId == system)
                .AsEnumerable()
                .Where(row => row.DestroyedAt is null && row.RetiredAt is null && row.Navigation != NpcNavigationState.Warping)
                .Where(row => Near(NpcMotion.Position(row), row.TypeId))
                .Select(row => row.Id)
                .Order()
                .ToArray(),
            world
                .Filter<WorldDrone>(row => row.SystemId == system)
                .AsEnumerable()
                .Where(row => world.Set<OwnedItem>().Find(row.ItemId) is { } item && Near(new(row.X, row.Y, row.Z), item.TypeId))
                .Select(row => row.ItemId)
                .Order()
                .ToArray(),
            world
                .Filter<SavedStructure>(row => row.SystemId == system)
                .AsEnumerable()
                .Where(row => row.Id != sourceId && Near(StructureCombatTargets.Position(row), row.TypeId))
                .Select(row => row.Id)
                .Order()
                .ToArray()
        );
    }

    private SpacePosition? PointDefensePosition(WorldAction world, int system, long id, long now)
    {
        if (presence.IsAreaProtected(system, id) || world.TetherProtected(id, now) || world.Positions.Find(id) is not { } position)
        {
            return null;
        }
        if (presence.FindArea(system, id) is { } live)
        {
            return live.Position;
        }
        return
            position.SystemId == system
            && world.Ships.Find(id) is { } ship
            && world.Set<SavedLogoff>().Find(ship.OwnerId) is { } logoff
            && logoff.SystemId == system
            && logoff.ShipId == id
            && logoff.ExpiresAt > now
            ? new(position.X, position.Y, position.Z)
            : null;
    }

    private TransactionParticipants PointDefenseParticipants(
        WorldAction world,
        StructureControlToken token,
        PointDefenseCandidates candidates
    )
    {
        var drones = candidates.Drones.Select(id => world.Set<WorldDrone>().Find(id)!).ToArray();
        var npcs = candidates
            .Npcs.SelectMany(id => NpcHostility.Participants(world, id))
            .Concat(candidates.Ships.SelectMany(id => ShipLossPersistence.LossNpcParticipants(world, database.Path, id)));
        return new(
            systems: [candidates.System],
            pilots: drones.Select(drone => drone.PilotId).Append(token.PilotId).Distinct(),
            ships: candidates.Ships.Concat(drones.Select(drone => drone.ControllerShipId).Where(id => id > 0)).Distinct(),
            resources: npcs.Distinct().Select(id => new SimulationResource(SharedSimulationResource.Npc, id))
        );
    }

    private PointDefenseResult FirePointDefense(
        WorldAction world,
        CatalogDbContext catalog,
        StructureControlToken token,
        SavedStructure source,
        OwnedItem item,
        StructureEquipmentProfile profile,
        StructureFittingState fit,
        PointDefenseCandidates candidates,
        int repeats,
        long now
    )
    {
        var loaded = world.Filter<OwnedItem>(charge => charge.LocationId == item.Id).SingleOrDefault();
        if (
            loaded is null
            || loaded.OwnerId != source.CorporationId
            || !StructureAmmunitionRules.Compatible(profile, StructureChargeCatalog.Require(catalog, loaded.TypeId))
        )
        {
            throw new InvalidDataException("Load compatible flak rounds before firing point defense.");
        }
        var module = StructurePointDefense.Define(fit.Equipment[item.Id], fit.Charges[item.Id]);
        if (loaded.Quantity < module.Rounds)
        {
            throw new InvalidDataException("Load enough flak rounds for this point defense cycle.");
        }
        double cost =
            module.Capacitor * StructureEnergy.CostMultiplier(world, catalog, token.PilotId, StructureWeaponRules.EngineeringSystemsSkill);
        double capacitor = StructureEnergy.Read(world, source.Id, fit.Attributes, now).Charge - cost;
        if (capacitor < 0)
        {
            throw new InvalidDataException("The structure has insufficient capacitor for point defense.");
        }
        long ready = StructureWeaponRules.ReadyAt(module.Duration, now);
        StructureEnergy.Write(world, source.Id, capacitor, now);
        loaded.Quantity -= module.Rounds;
        Magazine magazine = new(item.Id, source.Id, item.FlagId, loaded.TypeId, loaded.Quantity, 0);
        if (loaded.Quantity == 0)
        {
            world.Remove(loaded);
        }
        var gunner = world.Pilots.Find(token.PilotId)!;
        var party = PilotInteractionHistoryStore.Player(world, gunner, source.Id, item.TypeId) with
        {
            ShipTypeId = source.TypeId,
            CorporationId = source.CorporationId,
        };
        bool pvp = false;
        bool destroyedNpc = false;
        List<PilotDamage> notifications = [];
        foreach (long id in candidates.Ships.Concat(candidates.Npcs).Concat(candidates.Drones).Concat(candidates.Structures))
        {
            var target = world.Ships.Find(id) is { } ship
                ? PointDefenseShip(world, catalog, source.SystemId, ship, now)
                : targeting.Find(world, catalog, source.SystemId, id, now);
            if (
                target is null
                || SurfaceDistance(catalog, source, target) > module.Range
                || !CanPulse(world, catalog, source, target, now, out var victim)
            )
            {
                continue;
            }
            switch (target.Kind)
            {
                case StructureTargetKind.Pilot:
                    var attributes = PilotFittingStore.ShipAttributes(world, catalog, victim!, incursionTime: now);
                    var defense = ShipHealth.DefenseFrom(attributes);
                    if (PilotHealthStore.Read(world, id, defense, now).Remaining.Destroyed)
                    {
                        continue;
                    }
                    var result = PilotHealthStore.Apply(world, id, defense, module.Damage, now);
                    PilotInteractionHistoryStore.Damage(world, id, party, result.Applied);
                    CrimewatchPersistence.PlayerImpact(world, token.PilotId, victim!.Id, source.SystemId, now);
                    var loss = result.Remaining.Destroyed
                        ? losses.Apply(world, catalog, victim!, id, source.SystemId, victim!.ActiveShip.Position!, now, party)
                        : null;
                    var replacement = loss is null ? null : lookup.FindPilot(world, victim!.AccountId, victim.Id);
                    notifications.Add(
                        new(
                            victim!.Id,
                            token.PilotId,
                            source.Id,
                            new(id, defense, result.Remaining),
                            module.Damage,
                            result.Applied,
                            1,
                            item.TypeId,
                            false,
                            replacement,
                            loss?.Insurance
                        )
                        {
                            InventoryChanges = loss?.Changes ?? [],
                        }
                    );
                    pvp = true;
                    break;
                case StructureTargetKind.Npc:
                    var npc = world.Set<WorldNpc>().Find(id)!;
                    if (npc.Role is NpcRole.Concord or NpcRole.Sentry || !MilitiaObjectiveStore.CanAttack(world, npc, gunner))
                    {
                        continue;
                    }
                    var npcAttributes = PlayerEwarStore.NpcAttributes(world, id, catalog.FittingAttributes(npc.TypeId));
                    var hit = NpcDamagePersistence.Apply(
                        world,
                        catalog,
                        npc,
                        gunner,
                        source.Id,
                        npcAttributes,
                        module.Damage,
                        now,
                        sourceParty: party
                    );
                    destroyedNpc |= hit.Destroyed;
                    break;
                case StructureTargetKind.Drone:
                    var drone = world.Set<WorldDrone>().Find(id)!;
                    var contact = PilotDroneStore.Contact(world, catalog, drone, now);
                    if (contact.Health.Remaining.Destroyed)
                    {
                        continue;
                    }
                    _ = SmartbombStore.DamageDrone(world, drone, contact, module.Damage, now);
                    if (victim is not null)
                    {
                        StructureCombatTargets.Record(world, source.Id, victim.ActiveShipId, now, ready);
                        CrimewatchPersistence.PlayerImpact(world, token.PilotId, victim.Id, source.SystemId, now);
                    }
                    pvp = true;
                    break;
                case StructureTargetKind.Structure:
                    _ = StructureCombatTargets.Apply(world, catalog, world.Set<SavedStructure>().Find(id)!, module.Damage, now, party);
                    pvp = true;
                    break;
            }
            StructureCombatTargets.Record(world, source.Id, id, now, ready);
        }
        if (pvp)
        {
            var timers = CrimewatchPersistence.State(world, token.PilotId);
            timers.SystemId = source.SystemId;
            timers.Apply(CrimewatchRules.Player(timers.Timers(), ready, CrimewatchFlag.None, true, now));
        }
        bool Near(SpacePosition position, double radius) =>
            SmartbombRules.InRange(
                StructureCombatTargets.Position(source),
                StructureCatalog.Require(catalog, source.TypeId).Radius,
                position,
                radius,
                module.Range
            );
        _ = AreaWeaponStore.DamageProjectiles(world, source.SystemId, module.Damage, now, Near);
        SmartbombStore.DamageInterdictionProbes(world, catalog, source.SystemId, module.Damage, now, Near);
        return new(
            new(
                new(item.Id, 0, item.TypeId, now, ready, module.Duration, repeats, Effect: DogmaEffect.StructurePointDefense),
                null,
                PointDefense: new(magazine, capacitor, module.Range)
            ),
            notifications,
            destroyedNpc
        );
    }

    private StructureTarget? PointDefenseShip(WorldAction world, CatalogDbContext catalog, int system, Ship ship, long now)
    {
        // FindArea admits ordinary cloaked ships to untargeted damage while retaining travel and tether protection.
        return world.Pilots.Find(ship.OwnerId)?.ActiveShipId == ship.Id && PointDefensePosition(world, system, ship.Id, now) is { } point
            ? new(
                ship.Id,
                ship.TypeId,
                StructureTargetKind.Pilot,
                point,
                SpacePosition.Zero,
                catalog.RequireItem(ship.TypeId).Radius ?? 0,
                0
            )
            : null;
    }

    private static bool CanPulse(
        WorldAction world,
        CatalogDbContext catalog,
        SavedStructure source,
        StructureTarget target,
        long now,
        out Pilot? victim
    )
    {
        try
        {
            victim = CheckTarget(world, catalog, source, target, now);
            return true;
        }
        catch (InvalidDataException)
        {
            victim = null;
            return false;
        }
    }
}
