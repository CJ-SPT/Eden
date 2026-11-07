using Eden.Data.Drones;
using Eden.Gameplay.Combat;
using Eden.Gameplay.Dogma;
using Eden.Gameplay.Drones;
using Eden.Gameplay.Flight;
using Eden.World.Catalog;
using Eden.World.Drones;
using Eden.World.Fittings;
using Eden.World.Skills;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;

namespace Eden.World.Runtime.Combat;

internal sealed partial class LiveCombatRuntime
{
    private sealed record DronePilot(int Count, double Bandwidth, double Range, DroneSettings Settings, IReadOnlySet<int> Qualified);

    private readonly Dictionary<long, LiveDrone> _liveDrones = [];
    private readonly Dictionary<int, long[]> _droneSystems = [];
    private readonly Dictionary<long, DronePilot> _dronePilots = [];
    private readonly Dictionary<long, int[]> _dronePilotSystems = [];
    private readonly HashSet<long> _dirtyDrones = [];

    internal bool? HasControlledDrones(long pilot, long ship)
    {
        using var ownership = owner.Enter();
        return CanRun ? DroneControllers().TryGetValue(ship, out long controller) && controller == pilot : null;
    }

    internal IReadOnlyList<DroneContact>? Drones(int system, long now)
    {
        using var ownership = owner.Enter();
        if (!CanRun)
        {
            return null;
        }
        Trim();
        return SystemDrones(system).Select(d => d.Contact).ToArray();
    }

    private LiveDrone[] SystemDrones(int system)
    {
        if (!_droneSystems.TryGetValue(system, out var ids))
        {
            var rows = Loading(() =>
            {
                using WorldDbContext world = new(database.Path);
                using CatalogDbContext catalog = new(database.CatalogPath);
                catalog.Database.OpenConnection();
                return world
                    .Set<WorldDrone>()
                    .Where(d => d.SystemId == system)
                    .AsEnumerable()
                    .Select(d => new LiveDrone(
                        PilotDroneStore.Contact(world, catalog, d),
                        d.UpdatedAt,
                        d.ReadyAt,
                        d.CommandedAt,
                        d.ArmorDamage,
                        d.StructureDamage
                    ))
                    .ToArray();
            });
            foreach (var row in rows)
            {
                _liveDrones[row.Contact.Id] = row;
            }
            ids = rows.Select(d => d.Contact.Id).ToArray();
            _droneSystems[system] = ids;
        }
        return ids.Select(id => _liveDrones[id]).ToArray();
    }

    private DronePilot DroneInputs(long pilotId, LiveDrone[] drones)
    {
        if (!_dronePilots.TryGetValue(pilotId, out var inputs))
        {
            inputs = Loading(() =>
            {
                using WorldDbContext world = new(database.Path);
                using CatalogDbContext catalog = new(database.CatalogPath);
                catalog.Database.OpenConnection();
                var pilot = world.Pilots.Include(p => p.ActiveShip).Single(p => p.Id == pilotId);
                var skills = DroneCatalog.Skills(world, catalog, pilotId);
                var fit = FittingCalculation.Read(world, catalog, pilot);
                var saved = world.Set<WorldDroneSettings>().Find(pilotId);
                HashSet<int> qualified = [];
                foreach (int type in drones.Select(d => d.Contact.Profile.TypeId).Distinct())
                {
                    try
                    {
                        PilotSkillReader.CheckRequirements(world, catalog, pilotId, type);
                        qualified.Add(type);
                    }
                    catch (InvalidDataException)
                    {
                        // An invalidated qualification releases the same selected drones as the SQL path.
                    }
                }
                return new DronePilot(
                    Math.Min(DroneRules.MaximumDrones, skills.GetValueOrDefault(DroneSkill.Drones)),
                    fit.Graph.Read(pilot.ActiveShipId, DogmaAttributeId.DroneBandwidth),
                    DroneRules.ControlRange(skills) + fit.Graph.Read(pilotId, DogmaAttributeId.DroneControlDistance),
                    saved is null ? new() : new(saved.Aggressive, saved.FocusFire),
                    qualified
                );
            });
            _dronePilots[pilotId] = inputs;
        }
        return inputs;
    }

    internal DroneUpdate? AdvanceDrones(long pilotId, long now, int stamp, Func<IReadOnlyDictionary<long, double>, DroneUpdate> settle)
    {
        using var ownership = owner.Enter();
        if (!CanRun)
        {
            return null;
        }
        Trim();
        var ship = Ship(pilotId, now);
        if (!_dronePilotSystems.TryGetValue(pilotId, out var systems))
        {
            systems = Loading(() =>
            {
                using WorldDbContext world = new(database.Path);
                return world
                    .Set<WorldDrone>()
                    .Where(d => d.PilotId == pilotId && d.ControllerShipId != 0 && d.SystemId != null)
                    .Select(d => d.SystemId!.Value)
                    .Distinct()
                    .ToArray();
            });
            _dronePilotSystems[pilotId] = systems;
        }
        var drones = systems
            .SelectMany(SystemDrones)
            .Where(d => d.Contact.PilotId == pilotId && d.Contact.ShipId != 0)
            .OrderBy(d => d.Contact.Id)
            .ToArray();
        var inputs = DroneInputs(pilotId, drones);
        List<LiveDrone> staged = [];
        Dictionary<long, LiveCombatNpc> targets = [];
        List<DroneHit> hits = [];
        Dictionary<long, double> rolls = [];
        DroneUpdate Settle()
        {
            Flush();
            return settle(rolls);
        }
        int count = 0;
        double bandwidth = 0;
        bool released = false;
        foreach (var original in drones)
        {
            var drone = original;
            var contact = drone.Contact;
            if (
                ship.Position is not { } position
                || !DroneRules.SupportsHull(ship.TypeId)
                || contact.ShipId != ship.ShipId
                || contact.SystemId != ship.SystemId
                || position.DistanceTo(contact.Position) > DroneRules.ShutdownDistance
                || !inputs.Qualified.Contains(contact.Profile.TypeId)
                || count >= inputs.Count
                || bandwidth + contact.Profile.Bandwidth > inputs.Bandwidth
            )
            {
                staged.Add(LiveDroneRules.Release(drone));
                released = true;
                continue;
            }
            count++;
            bandwidth += contact.Profile.Bandwidth;
            if (now <= drone.UpdatedAt)
            {
                staged.Add(drone);
                continue;
            }
            if (contact.Activity == DroneActivity.Idle && inputs.Settings.Aggressive)
            {
                var candidates = Aggression()
                    .Values.Where(a => a.ShipId == ship.ShipId && a.LastShotAt > drone.CommandedAt)
                    .Select(a => targets.GetValueOrDefault(a.NpcId) ?? Npc(a.NpcId, now))
                    .OfType<LiveCombatNpc>()
                    .Where(n =>
                        n.Available
                        && n.Transit is null
                        && n.SystemId == ship.SystemId
                        && n.Navigation != NpcNavigationState.Warping
                        && position.DistanceTo(n.Position) <= inputs.Range
                    )
                    .OrderBy(n => n.Id)
                    .ToArray();
                if (candidates.Length != 0)
                {
                    contact = contact with
                    {
                        Activity = DroneActivity.Fighting,
                        TargetId = candidates[inputs.Settings.FocusFire ? 0 : (int)(contact.Id % candidates.Length)].Id,
                    };
                    drone = drone with { Contact = contact };
                }
            }
            var npc =
                contact.Activity == DroneActivity.Fighting
                    ? targets.GetValueOrDefault(contact.TargetId) ?? Npc(contact.TargetId, now)
                    : null;
            if (
                npc is not null
                && (
                    !npc.Available
                    || npc.SystemId != ship.SystemId
                    || npc.Transit is not null
                    || npc.Navigation == NpcNavigationState.Warping
                )
            )
            {
                npc = null;
            }
            // Mining alerts and inventory return retain the existing transaction boundary.
            if (npc is not null && NpcPopulationRules.IsMining(npc.Role))
            {
                return Settle();
            }
            double roll = Draw();
            rolls[contact.Id] = roll;
            var step = LiveDroneRules.Advance(drone, ship, npc, now, stamp, roll);
            if (step.Recall)
            {
                return Settle();
            }
            staged.Add(step.Drone);
            released |= step.Drone.Contact.ShipId == 0;
            if (step.Damage is not { } damage || npc is null)
            {
                continue;
            }
            var impact = LiveDroneRules.Hit(npc, ship, step.Drone.Contact, damage, now);
            var next = impact.Target;
            if (!next.Available)
            {
                // No staged drone or paid volley has been published. SQL replays this interval atomically.
                return Settle();
            }
            targets[npc.Id] = RepairNpc(next, now);
            hits.Add(
                new(
                    contact.Id,
                    contact.Profile.TypeId,
                    npc.Id,
                    impact.Applied,
                    damage,
                    new(npc.Id, next.Health.Defense, next.Health.Remaining),
                    damage.Total / contact.Profile.Damage.Total
                )
            );
            ship = ship with
            {
                NpcAggression = CrimewatchRules.Extend(ship.NpcAggression ?? new(), step.Drone.ReadyAt, CrimewatchRules.NpcDuration),
            };
        }
        foreach (var target in targets.Values)
        {
            Hostility(target, ship.ShipId, now);
            _npcs[target.Id] = RepairNpc(target, now);
            _dirtyNpcs.Add(target.Id);
        }
        foreach (var drone in staged)
        {
            if (_liveDrones[drone.Contact.Id] != drone)
            {
                _liveDrones[drone.Contact.Id] = drone;
                _dirtyDrones.Add(drone.Contact.Id);
            }
        }
        if (hits.Count != 0)
        {
            _ships[pilotId] = ship;
            _dirtyShips.Add(pilotId);
        }
        if (released)
        {
            foreach (long controller in drones.Select(d => d.Contact.ShipId).Distinct())
            {
                if (staged.All(d => d.Contact.ShipId != controller))
                {
                    DroneControllers().Remove(controller);
                }
            }
        }
        if (_dirtyDrones.Count != 0)
        {
            owner.ObjectsChanged();
        }
        return new([], hits, released);
    }

    private bool FireAtDrone(
        LiveCombatShip ship,
        LiveCombatNpc npc,
        LiveAggression aggression,
        long now,
        ref double roll,
        out NpcShot? shot
    )
    {
        shot = null;
        var attributes = npc.Attributes;
        var drone = SystemDrones(npc.SystemId)
            .Where(d => d.Contact.ShipId == ship.ShipId && d.Contact.TargetId == npc.Id && d.Contact.Activity == DroneActivity.Fighting)
            .Where(d => PlayerEwarRules.CanTarget(npc.IncomingElectronicWarfare, d.Contact.Id))
            .OrderBy(d => d.Contact.Id)
            .FirstOrDefault(d =>
                d.Contact.Position.DistanceTo(npc.Position) - d.Contact.Profile.Radius - npc.Radius
                <= attributes.GetValueOrDefault(DogmaAttributeId.EntityAttackRange)
            );
        if (drone is null)
        {
            return true;
        }
        if (roll >= DroneRules.NpcRetaliationChance)
        {
            roll = DroneRules.OwnerAccuracyRoll(roll);
            return true;
        }
        var contact = drone.Contact;
        var weapon = DroneNpcCombat.Weapon(attributes);
        if (!double.IsFinite(weapon.CycleMilliseconds) || weapon.CycleMilliseconds <= 0)
        {
            throw new InvalidDataException("Invalid NPC weapon cycle attributes.");
        }
        var signature = DroneSignature(contact.Profile.TypeId);
        var retaliation = DroneRules.Retaliation(contact, npc.Position, npc.Velocity, signature, weapon, roll);
        if (retaliation.Result.Remaining.Destroyed)
        {
            return false;
        }
        _liveDrones[contact.Id] = drone with
        {
            Contact = contact with { Health = contact.Health with { Remaining = retaliation.Result.Remaining } },
            ArmorDamage = contact.Profile.Defense.Armor - retaliation.Result.Remaining.Armor,
            StructureDamage = contact.Profile.Defense.Structure - retaliation.Result.Remaining.Structure,
        };
        _dirtyDrones.Add(contact.Id);
        var fired = new NpcWeaponContact(
            contact.Id,
            (int)attributes.GetValueOrDefault(DogmaAttributeId.EntityTurretType),
            now,
            weapon.CycleMilliseconds
        );
        _aggressions![npc.Id] = aggression with
        {
            ReadyAt = checked(now + (long)Math.Ceiling(weapon.CycleMilliseconds * TimeSpan.TicksPerMillisecond)),
            LastShotAt = now,
            LastTargetId = contact.Id,
        };
        _dirtyAggressions.Add(npc.Id);
        owner.ObjectsChanged();
        shot = new(
            npc.Id,
            npc.TypeId,
            fired,
            weapon.Damage,
            retaliation.Result.Applied,
            retaliation.Quality,
            new(contact.Id, contact.Profile.Defense, retaliation.Result.Remaining),
            null
        );
        return true;
    }

    private readonly Dictionary<int, double> _droneSignatures = [];

    private double DroneSignature(int type)
    {
        if (!_droneSignatures.TryGetValue(type, out double signature))
        {
            signature = Loading(() =>
            {
                using CatalogDbContext catalog = new(database.CatalogPath);
                return catalog.FittingAttributes(type).GetValueOrDefault(DogmaAttributeId.SignatureRadius);
            });
            _droneSignatures[type] = signature;
        }
        return signature;
    }

    internal static void ProjectDrones(WorldDbContext world, IReadOnlyList<LiveDrone> drones)
    {
        foreach (var drone in drones)
        {
            var contact = drone.Contact;
            world.Database.ExecuteSql(
                $"""
                UPDATE WorldDrone SET ControllerShipId={contact.ShipId},Activity={(int)contact.Activity},TargetId={contact.TargetId},
                    X={contact.Position.X},Y={contact.Position.Y},Z={contact.Position.Z},
                    VelocityX={contact.Velocity.X},VelocityY={contact.Velocity.Y},VelocityZ={contact.Velocity.Z},
                    SpeedLimit={contact.SpeedLimit},Orbiting={contact.Orbiting},Shield={contact.Health.Remaining.Shield},
                    ArmorDamage={drone.ArmorDamage},StructureDamage={drone.StructureDamage},UpdatedAt={drone.UpdatedAt},
                    ReadyAt={drone.ReadyAt},CommandedAt={drone.CommandedAt},ShotAt={contact.ShotAt},ShotTargetId={contact.ShotTargetId}
                WHERE ItemId={contact.Id}
                """
            );
        }
    }

    private void ClearDrones()
    {
        _droneSignatures.Clear();
        _liveDrones.Clear();
        _droneSystems.Clear();
        _dronePilots.Clear();
        _dronePilotSystems.Clear();
    }
}
