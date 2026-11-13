using Eden.Data.World.Characters;
using Eden.Data.World.Combat;
using Eden.Data.World.Combat.Npcs;
using Eden.Data.World.Drones;
using Eden.Data.World.Inventory;
using Eden.Gameplay.Agents;
using Eden.Gameplay.Combat;
using Eden.Gameplay.Docking;
using Eden.Gameplay.Dogma;
using Eden.Gameplay.Drones;
using Eden.Gameplay.Flight;
using Eden.Gameplay.Inventory;
using Eden.Infrastructure.Registration;
using Eden.World.Catalog;
using Eden.World.Characters;
using Eden.World.Combat.Npcs;
using Eden.World.Docking;
using Eden.World.Drones;
using Eden.World.Fittings;
using Eden.World.Inventory;
using Eden.World.Runtime.Economy;
using Eden.World.Skills;
using Eden.World.Storage;
using Eden.World.Thermal;

namespace Eden.World.Combat;

[Injectable]
internal sealed class SmartbombStore(
    WorldDatabase database,
    SqliteWorld lookup,
    PilotLossSettlement losses,
    IPilotDamageEvents damageEvents,
    SecurityStatusSignal securityStatus
) : IPilotSmartbombs
{
    public SmartbombPulse Pulse(long pilotId, long shipId, long moduleId, SmartbombCandidates candidates, long now)
    {
        using WorldActionScope transaction = new(database, select: world => Participants(world, pilotId, shipId, candidates));
        var world = transaction.World;
        using CatalogDbContext catalog = new(database.CatalogPath);
        var pilot = world.Pilots.Find(pilotId);
        var item = world.Set<OwnedItem>().Find(moduleId);
        if (
            pilot?.ActiveShipId != shipId
            || pilot.ActiveShip.Position is not { } saved
            || item is null
            || item.OwnerId != pilotId
            || item.LocationId != shipId
            || !item.Singleton
            || !item.Online
            || !InventoryDefinitions.IsHighSlot(item.FlagId)
            || !SmartbombCatalog.Supported(catalog, item.TypeId, catalog.RequireItem(item.TypeId).GroupId)
        )
        {
            throw new InvalidDataException("Use an online fitted smartbomb on your active ship in space.");
        }
        world.RequireUncloaked(shipId);
        PilotSkillReader.CheckRequirements(world, catalog, pilotId, item.TypeId);
        NaniteRepairPersistence.RequireAvailable(world, catalog, moduleId, now);
        var cooldown = world.Set<ModuleCooldown>().Find(moduleId);
        if (cooldown is not null && now < cooldown.ReadyAt)
        {
            throw new InvalidDataException("The smartbomb is still cycling.");
        }
        int system = CrimewatchPersistence.System(catalog, pilot.ActiveShip);
        double security = CrimewatchPersistence.Security(catalog, system);
        bool alpha = world.Set<Account>().Find(pilot.AccountId)!.CloneGrade == AccountCloneGrade.Alpha;
        SmartbombRules.CheckSafety(security, pilot.SafetyLevel, alpha);
        var calculation = PilotOverloadStore.Selected(world, database.Path, pilotId, moduleId)
            ? FittingCalculation.Read(world, catalog, pilot, overloadedModules: new HashSet<long> { moduleId })
            : null;
        var attributes = PilotFittingStore.ReadModuleAttributes(
            world,
            catalog,
            pilotId,
            PilotInventoryStore.Read(catalog, item),
            calculation
        );
        var module = SmartbombRules.Define(moduleId, item.TypeId, attributes);
        SpacePosition origin = new(saved.X, saved.Y, saved.Z);
        double sourceRadius = catalog.RequireItem(pilot.ActiveShip.TypeId).Radius ?? 0;
        double protectedRange = Math.Max(SmartbombRules.MinimumProtectedDistance, module.Range);
        if (
            SystemObjects
                .Geometry(catalog, system)
                .Concat(candidates.ProtectedObjects)
                .Any(body =>
                    (
                        body.CategoryId == (int)InventoryCategory.Station
                        || body.GroupId is SmartbombRules.StargateGroup or MissionGateRules.AccelerationGateGroup
                    ) && SmartbombRules.InRange(origin, sourceRadius, body.Position, body.Radius, protectedRange)
                )
        )
        {
            throw new InvalidDataException($"Move more than {protectedRange:0} m from stations and gates before activating a smartbomb.");
        }
        var ship = PilotFittingStore.ShipAttributes(world, catalog, pilot, calculation: calculation);
        if (PilotHealthStore.Read(world, shipId, ShipHealth.DefenseFrom(ship), now).Remaining.Destroyed)
        {
            throw new InvalidDataException("A destroyed ship cannot use smartbombs.");
        }

        var victims = new SortedDictionary<long, Pilot>();
        var aggression = new SortedDictionary<long, (Pilot Pilot, AggressionDecision Decision)>();
        foreach (var candidate in candidates.Ships.DistinctBy(candidate => candidate.Id))
        {
            var victim = FindPilot(world, catalog, candidate.Id, shipId, system, now);
            if (victim is not null && Near(candidate.Position, catalog.RequireItem(victim.ActiveShip.TypeId).Radius ?? 0))
            {
                victims.Add(candidate.Id, victim);
                AddAggression(victim);
            }
        }
        List<(WorldDrone Row, DroneContact Contact)> drones = [];
        foreach (long id in candidates.Drones.Distinct().Order())
        {
            if (
                world.Set<WorldDrone>().Find(id) is not { } row
                || row.SystemId != system
                || world.Set<OwnedItem>().Find(id) is not { } droneItem
                || !Near(new(row.X, row.Y, row.Z), catalog.RequireItem(droneItem.TypeId).Radius ?? 0)
            )
            {
                continue;
            }
            var contact = PilotDroneStore.Contact(world, catalog, row);
            if (contact.Health.Remaining.Destroyed)
            {
                continue;
            }
            if (
                row.PilotId != pilotId
                && row.ControllerShipId != 0
                && world.Pilots.Find(row.PilotId) is { } owner
                && FindPilot(world, catalog, owner.ActiveShipId, shipId, system, now) is { } present
            )
            {
                AddAggression(present);
            }
            drones.Add((row, contact));
        }

        long end = checked(now + (long)Math.Ceiling(module.DurationMilliseconds * TimeSpan.TicksPerMillisecond));
        double capacitor = ShipResources.SpendCapacitor(world, shipId, ship, module.CapacitorNeed, now);
        cooldown ??= world.Set<ModuleCooldown>().Add(new() { ModuleId = moduleId }).Entity;
        cooldown.Kind = ModuleCooldownKind.Cycle;
        cooldown.ReadyAt = end;
        var heat = calculation is null ? null : ShipHeatPersistence.BeginPaidCycle(world, catalog, calculation, moduleId, now, end);

        var timers = CrimewatchPersistence.State(world, pilotId);
        timers.SystemId = system;
        var state = timers.Timers();
        timers.Apply(state with { Weapons = CrimewatchRules.Extend(state.Weapons, end, CrimewatchRules.WeaponsDuration) });

        // Resolve every legality check before changing any victim's identity.
        foreach (var (victim, decision) in aggression.Values)
        {
            CrimewatchPersistence.Player(world, catalog, pilotId, victim.Id, system, decision, now, end);
        }
        var attacker = PilotInteractionHistoryStore.Player(world, pilot, shipId, item.TypeId);
        List<SmartbombHit> hits = [];
        List<PilotDamage> notifications = [];
        var destroyedNpc = false;
        foreach (long id in candidates.Npcs.Distinct().Order())
        {
            if (
                world.Set<WorldNpc>().Find(id) is not { DestroyedAt: null, RetiredAt: null } npc
                || npc.SystemId != system
                || npc.Navigation == NpcNavigationState.Warping
                || !Near(NpcMotion.Position(npc), catalog.RequireItem(npc.TypeId).Radius ?? 0)
            )
            {
                continue;
            }
            var npcAttributes = PlayerEwarStore.NpcAttributes(world, id, catalog.FittingAttributes(npc.TypeId));
            DroneCombatPersistence.Module(world, shipId, id, false, now);
            var result = NpcDamagePersistence.Apply(world, catalog, npc, pilot, shipId, npcAttributes, module.Damage, now, item.TypeId);
            if (result.Reward is not null)
            {
                world.SaveChanges();
            }
            hits.Add(new(result.Health, result.Applied, result.Reward is null ? null : NpcDamagePersistence.Read(result.Reward)));
            destroyedNpc |= result.Destroyed;
        }
        foreach (var (row, contact) in drones)
        {
            hits.Add(DamageDrone(world, row, contact, module.Damage));
        }
        foreach (var (id, victim) in victims)
        {
            var defense = ShipHealth.DefenseFrom(PilotFittingStore.ShipAttributes(world, catalog, victim));
            var result = PilotHealthStore.Apply(world, id, defense, module.Damage, now);
            DroneCombatPersistence.Module(world, shipId, id, true, now);
            DroneCombatPersistence.Incoming(world, id, shipId, now);
            PilotInteractionHistoryStore.Damage(world, id, attacker, result.Applied);
            var loss = result.Remaining.Destroyed
                ? losses.Apply(world, catalog, victim, id, system, victim.ActiveShip.Position!, now, attacker)
                : null;
            DockedPilot? replacement = null;
            if (loss is not null)
            {
                world.SaveChanges();
                replacement = lookup.FindPilot(world, victim.AccountId, victim.Id);
            }
            ShipHealth health = new(id, defense, result.Remaining);
            hits.Add(new(health, result.Applied));
            notifications.Add(
                new(victim.Id, pilotId, shipId, health, module.Damage, result.Applied, 1, item.TypeId, false, replacement, loss?.Insurance)
                {
                    InventoryChanges = loss?.Changes ?? [],
                }
            );
        }
        bool intercepted = DamageMissiles(world, catalog, candidates.Missiles, system, now, module, Near);
        world.SaveChanges();
        transaction.Commit();
        transaction.Dispose();
        if (heat is not null)
        {
            WorldConnections.PublishHeat(world, pilotId, shipId, heat);
        }
        if (destroyedNpc)
        {
            securityStatus.Publish();
        }
        foreach (var notification in notifications)
        {
            damageEvents.Publish(notification);
        }
        return new(module, end, capacitor, hits, intercepted);

        bool Near(SpacePosition position, double radius) => SmartbombRules.InRange(origin, sourceRadius, position, radius, module.Range);

        void AddAggression(Pilot target)
        {
            if (!aggression.ContainsKey(target.Id))
            {
                aggression.Add(target.Id, (target, CrimewatchPersistence.CheckPlayer(world, catalog, pilot, target, system, now)));
            }
        }
    }

    private TransactionParticipants Participants(WorldAction world, long pilot, long ship, SmartbombCandidates candidates)
    {
        var ships = candidates.Ships.Select(s => s.Id).Append(ship).ToHashSet();
        var pilots = new HashSet<long> { pilot };
        foreach (long id in candidates.Drones)
        {
            if (world.Set<WorldDrone>().Find(id) is { } drone)
            {
                pilots.Add(drone.PilotId);
                if (drone.ControllerShipId != 0)
                {
                    ships.Add(drone.ControllerShipId);
                }
            }
        }
        var npcs = candidates
            .Npcs.SelectMany(id => NpcHostility.Participants(world, id))
            .Concat(
                ships.Where(id => id != 0 && id != ship).SelectMany(id => ShipLossPersistence.LossNpcParticipants(world, database.Path, id))
            );
        return new(pilots: pilots, ships: ships, resources: npcs.Select(id => new SimulationResource(SharedSimulationResource.Npc, id)));
    }

    private static Pilot? FindPilot(WorldAction world, CatalogDbContext catalog, long ship, long source, int system, long now)
    {
        if (ship == source)
        {
            return null;
        }
        var victim = world.Filter<Pilot>(p => p.ActiveShipId == ship).SingleOrDefault();
        return
            victim?.ActiveShip.Position is not null
            && CrimewatchPersistence.System(catalog, victim.ActiveShip) == system
            && (world.Set<SavedLogoff>().Find(victim.Id) is not { } logoff || logoff.ExpiresAt > now)
            ? victim
            : null;
    }

    private static SmartbombHit DamageDrone(WorldAction world, WorldDrone row, DroneContact contact, DamageVector damage)
    {
        var defense = contact.Profile.Defense;
        var result = CombatDamage.Apply(contact.Health.Remaining, defense, damage);
        row.Shield = result.Remaining.Shield;
        row.ArmorDamage = defense.Armor - result.Remaining.Armor;
        row.StructureDamage = defense.Structure - result.Remaining.Structure;
        if (result.Remaining.Destroyed)
        {
            row.SystemId = null;
            row.ControllerShipId = 0;
            row.TargetId = 0;
            DroneDelegationPersistence.Write(row, null);
            var item = world.Set<OwnedItem>().Find(row.ItemId)!;
            item.LocationId = (int)InventoryLocation.Graveyard;
            item.FlagId = (int)InventoryFlag.AutoFit;
        }
        return new(new(row.ItemId, defense, result.Remaining), result.Applied);
    }

    private static bool DamageMissiles(
        WorldAction world,
        CatalogDbContext catalog,
        IReadOnlyList<long> candidates,
        int system,
        long now,
        Smartbomb module,
        Func<SpacePosition, double, bool> near
    )
    {
        bool intercepted = false;
        foreach (long id in candidates.Distinct().Order())
        {
            if (world.Set<WorldMissile>().Find(id) is not { Status: MissileFlightStatus.Flying } row || row.SystemId != system)
            {
                continue;
            }
            var payload = row.Payload();
            if (!near(payload.Flight.Position, payload.Flight.Drive.Radius))
            {
                continue;
            }
            double hp = catalog.FittingAttributes(row.ChargeTypeId).GetValueOrDefault(DogmaAttributeId.StructureHitPoints);
            if (!double.IsFinite(hp) || hp <= 0)
            {
                continue;
            }
            // Eden already represents each bank member as its own projectile.
            // A sublethal pulse leaves missile hit points and warhead damage intact.
            if (module.Damage.Total > hp)
            {
                row.Status = MissileFlightStatus.Expired;
                row.EndedAt = now;
                row.Save(payload with { Flight = payload.Flight with { Status = MissileFlightStatus.Expired } });
                intercepted = true;
            }
        }
        return intercepted;
    }
}
