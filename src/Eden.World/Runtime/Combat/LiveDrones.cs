using Eden.Data.World.Capsuleers;
using Eden.Data.World.Drones;
using Eden.Gameplay.Characters;
using Eden.Gameplay.Combat;
using Eden.Gameplay.Dogma;
using Eden.Gameplay.Drones;
using Eden.Gameplay.Flight;
using Eden.World.Catalog;
using Eden.World.Combat;
using Eden.World.Drones;
using Eden.World.Fittings;
using Eden.World.Skills;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;

namespace Eden.World.Runtime.Combat;

internal sealed partial class LiveCombatRuntime
{
    private sealed record DronePilot(int Count, double Bandwidth, double Range, DroneSettings Settings, IReadOnlySet<int> Qualified);

    private readonly ResidentMap<long, LiveDrone> _liveDrones = [];
    private readonly ResidentMap<int, long[]> _droneSystems = [];
    private readonly ResidentMap<long, DronePilot> _dronePilots = [];
    private readonly ResidentMap<long, int[]> _dronePilotSystems = [];
    private readonly ResidentSet<long> _dirtyDrones = [];

    internal bool? HasControlledDrones(long pilot, long ship)
    {
        return Execute(() => HasControlledDronesOwned(pilot, ship), nameof(HasControlledDrones));
    }

    private bool? HasControlledDronesOwned(long pilot, long ship)
    {
        using var ownership = owner.Enter();
        return CanRun ? DroneControllers().TryGetValue(ship, out long controller) && controller == pilot : null;
    }

    internal IReadOnlyList<DroneContact>? Drones(int system, long now)
    {
        return Execute(() => DronesOwned(system, now), nameof(Drones));
    }

    private IReadOnlyList<DroneContact>? DronesOwned(int system, long now)
    {
        using var ownership = owner.Enter();
        if (!CanRun)
        {
            return null;
        }
        Trim();
        return SystemDrones(system).Select(d => Eden.Gameplay.Flight.FighterMobilityRules.Apply(d.Contact, now)).ToArray();
    }

    private LiveDrone[] SystemDrones(int system)
    {
        if (!_droneSystems.TryGetValue(system, out var ids))
        {
            var rows = Loading(() =>
            {
                return owner.Economy.Read(world => LoadDrones(world, system), new(systems: [system]));
            });
            foreach (var row in rows)
            {
                if (!_dirtyDrones.Contains(row.Contact.Id))
                {
                    _liveDrones[row.Contact.Id] = row;
                }
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
                return owner.Economy.Read(world => ReadDroneInputs(world, pilotId, drones), new(pilots: [pilotId]));
            });
            _dronePilots[pilotId] = inputs;
        }
        return inputs;
    }

    internal DroneUpdate? AdvanceDrones(
        long pilotId,
        long now,
        int stamp,
        IPilotCombatPresence presence,
        IPilotDamageEvents damageEvents,
        Eden.Gameplay.Fleets.ICooperativeFleets fleets,
        Func<IReadOnlyDictionary<long, double>, IReadOnlyCollection<long>, DroneUpdate> settle
    )
    {
        return Execute(() => AdvanceDronesOwned(pilotId, now, stamp, presence, damageEvents, fleets, settle), nameof(AdvanceDrones));
    }

    private DroneUpdate? AdvanceDronesOwned(
        long pilotId,
        long now,
        int stamp,
        IPilotCombatPresence presence,
        IPilotDamageEvents damageEvents,
        Eden.Gameplay.Fleets.ICooperativeFleets fleets,
        Func<IReadOnlyDictionary<long, double>, IReadOnlyCollection<long>, DroneUpdate> settle
    )
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
                return owner.Economy.Inspect(world =>
                    world
                        .Set<WorldDrone>()
                        .Where(d => d.PilotId == pilotId && d.ControllerShipId != 0 && d.SystemId != null)
                        .Select(d => d.SystemId!.Value)
                        .Distinct()
                        .ToArray()
                );
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
        ResidentMap<long, LiveCombatNpc> targets = [];
        ResidentMap<long, LiveCombatShip> pilotTargets = [];
        List<DronePlayerShot> pilotShots = [];
        List<DroneHit> hits = [];
        ResidentMap<long, double> rolls = [];
        DroneUpdate Settle()
        {
            var reactive = inputs.Settings.Aggressive
                ? Aggression()
                    .Values.Where(row => row.ShipId == ship.ShipId)
                    .Select(row => row.NpcId)
                    .Concat(PlayerAttacks(ship.ShipId, now).Where(row => row.ExpiresAt > now).Select(row => row.SourceShipId))
                : [];
            var delegated = drones
                .Select(drone => drone.Contact.Delegation)
                .OfType<DroneDelegation>()
                .SelectMany(delegation =>
                {
                    var triggers = DroneTriggers(delegation.ShipId);
                    return new[] { delegation.ShipId, triggers.Offensive.OtherId, triggers.Incoming.OtherId };
                });
            long[] endpoints = drones
                .Select(drone => drone.Contact.TargetId)
                .Concat(reactive)
                .Concat(delegated)
                .Where(id => id > 0)
                .Distinct()
                .ToArray();
            return settle(rolls, endpoints);
        }
        if (
            IndustryProtected(ship.ShipId, now)
            || drones.Any(drone => drone.Contact.Profile.Utility is not null || drone.Contact.Profile.FighterGroup != 0)
        )
        {
            // Utility cycles mutate the same resident economy generation as
            // their targets and inventory. This does not open the saved world.
            return Settle();
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
                || ship.Health.At(now).Destroyed
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
            var anchor = AdvanceDelegation(drone, ship, presence, fleets, now, out drone);
            contact = drone.Contact;
            if (now <= drone.UpdatedAt)
            {
                staged.Add(drone);
                continue;
            }
            if (contact.Delegation is null && contact.Activity == DroneActivity.Idle && inputs.Settings.Aggressive)
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
                    .Select(n => n.Id)
                    .Concat(DroneAutomaticTargets(ship, presence, drone.CommandedAt, inputs.Range, now))
                    .Order()
                    .ToArray();
                if (candidates.Length != 0)
                {
                    contact = contact with
                    {
                        Activity = DroneActivity.Fighting,
                        TargetId = candidates[inputs.Settings.FocusFire ? 0 : (int)(contact.Id % candidates.Length)],
                    };
                    drone = drone with { Contact = contact };
                }
            }
            if (contact.Activity == DroneActivity.Fighting && owner.Economy.PlayerEwarTargetVersion(contact.Id) != 0)
            {
                var electronicWarfare =
                    owner.Reads?.PlayerEwarIncoming(contact.Id)
                    ?? owner.Economy.Inspect(world => PlayerEwarStore.Active(world, contact.Id));
                if (!PlayerEwarRules.CanTarget(electronicWarfare, contact.TargetId))
                {
                    contact = contact with { Activity = DroneActivity.Idle, TargetId = 0 };
                    drone = drone with { Contact = contact };
                }
            }
            if (contact.Activity == DroneActivity.Fighting && owner.Economy.Structure(contact.TargetId) is not null)
            {
                // Structure HP, reinforcement and custody share the resident inventory transaction.
                return Settle();
            }
            var player = contact.Activity == DroneActivity.Fighting ? DronePlayerFor(contact.TargetId) : null;
            var npc =
                contact.Activity == DroneActivity.Fighting && player is null
                    ? targets.GetValueOrDefault(contact.TargetId) ?? Npc(contact.TargetId, now)
                    : null;
            if (
                npc is not null
                && (
                    !npc.Available
                    || npc.SystemId != ship.SystemId
                    || npc.Transit is not null
                    || npc.Navigation == NpcNavigationState.Warping
                    || contact.Delegation is not null && position.DistanceTo(npc.Position) > inputs.Range
                )
            )
            {
                npc = null;
            }
            // Mission waves, mining alerts and inventory return retain the existing transaction boundary.
            if (
                npc is not null
                && (
                    npc.RequiresMissionDamageSettlement
                    || NpcPopulationRules.IsMining(npc.Role)
                    || npc.Role is NpcRole.Concord or NpcRole.Sentry or NpcRole.MilitiaHub or NpcRole.MilitiaObjective
                )
            )
            {
                return Settle();
            }
            double roll = Draw();
            rolls[contact.Id] = roll;
            var source = player is null ? null : DronePlayerFor(ship.ShipId);
            var playerContact = player is null ? null : DronePresence(player, presence, ship.SystemId, now);
            var decision =
                source is null || player is null || player.PilotId == pilotId ? null : DroneLegality(source, player, ship.SystemId, now);
            if (decision is { Flag: not CrimewatchFlag.None })
            {
                // Settle the illegal shot and its security consequences in one resident action.
                return Settle();
            }
            var playerShip =
                playerContact is null || decision is null
                    ? null
                    : pilotTargets.GetValueOrDefault(player!.PilotId) ?? Ship(player!.PilotId, now).SettleRepairs(now);
            if (playerShip?.Health.At(now).Destroyed == true)
            {
                playerShip = null;
            }
            if (contact.Delegation is not null && playerContact is not null && position.DistanceTo(playerContact.Position) > inputs.Range)
            {
                playerShip = null;
            }
            DroneTarget? target =
                npc is not null ? new(npc.Id, npc.Position, npc.Velocity, npc.Radius, npc.SignatureRadius)
                : playerShip is not null
                    ? new(playerShip.ShipId, playerContact!.Position, playerContact.Velocity, playerShip.Radius, playerShip.SignatureRadius)
                : null;
            var step = LiveDroneRules.AdvanceTarget(drone, ship, target, now, stamp, roll, anchor);
            if (step.Recall)
            {
                return Settle();
            }
            staged.Add(step.Drone);
            released |= step.Drone.Contact.ShipId == 0;
            if (step.Damage is not { } damage)
            {
                continue;
            }
            if (playerShip is not null)
            {
                var applied = CombatDamage.Apply(playerShip.Health.At(now), playerShip.Health.Defense, damage, playerShip.ShipId, now);
                if (applied.Remaining.Destroyed)
                {
                    return Settle();
                }
                CombatParty party = new(ship.ShipId, ship.PilotId, ship.CorporationId, null, ship.TypeId, contact.Profile.TypeId, null, 0);
                var health = playerShip.Health with
                {
                    Remaining = applied.Remaining,
                    UpdatedAt = Math.Max(now, playerShip.Health.UpdatedAt),
                    Revision = playerShip.Health.Revision + 1,
                    ArmorDamage = playerShip.Health.Defense.Armor - applied.Remaining.Armor,
                    StructureDamage = playerShip.Health.Defense.Structure - applied.Remaining.Structure,
                };
                pilotTargets[playerShip.PilotId] = playerShip with
                {
                    Health = health,
                    Contributions = InteractionHistoryRules.AddDamage(playerShip.Contributions, party, applied.Applied),
                };
                DroneHit hit = new(
                    contact.Id,
                    contact.Profile.TypeId,
                    playerShip.ShipId,
                    applied.Applied,
                    damage,
                    new(playerShip.ShipId, health.Defense, health.Remaining),
                    damage.Total / contact.Profile.Damage.Total
                );
                hits.Add(hit);
                pilotShots.Add(new(source!, player!, decision!, step.Drone.ReadyAt, hit));
                continue;
            }
            if (npc is null)
            {
                continue;
            }
            var impact = LiveDroneRules.Hit(npc, ship, step.Drone.Contact, damage, now);
            var next = impact.Target;
            if (!next.Available)
            {
                // No staged drone or paid volley has been published. Settlement replays this interval atomically.
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
            SetNpc(RepairNpc(target, now));
            _dirtyNpcs.Add(target.Id);
        }
        foreach (var target in pilotTargets.Values)
        {
            _ships[target.PilotId] = target;
            _dirtyShips.Add(target.PilotId);
        }
        foreach (var shot in pilotShots)
        {
            RecordDroneAttack(shot.Source, shot.Victim, ship.SystemId, shot.Decision, now, shot.End);
            RecordDroneIncoming(shot.Victim.ShipId, ship.ShipId, now);
        }
        foreach (var drone in staged)
        {
            if (_liveDrones[drone.Contact.Id] != drone)
            {
                owner.Economy.MissileHistory(drone.Contact.Id)?.Observe(now, drone.Contact.Position, drone.Contact.Velocity);
                _liveDrones[drone.Contact.Id] = drone;
                _dirtyDrones.Add(drone.Contact.Id);
            }
        }
        if (hits.Any(h => targets.ContainsKey(h.TargetId)))
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
        foreach (var shot in pilotShots)
        {
            var hit = shot.Hit;
            damageEvents.Publish(
                new(
                    shot.Victim.PilotId,
                    pilotId,
                    ship.ShipId,
                    hit.Health,
                    hit.Damage,
                    hit.Applied,
                    hit.Quality,
                    hit.TypeId,
                    false,
                    null,
                    null
                )
            );
        }
        return new([], hits, released);
    }

    private sealed record DronePlayerShot(DronePlayer Source, DronePlayer Victim, AggressionDecision Decision, long End, DroneHit Hit);

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
        var contact = Eden.Gameplay.Flight.FighterMobilityRules.Apply(drone.Contact, now);
        var weapon = DroneNpcCombat.Weapon(attributes);
        if (!double.IsFinite(weapon.CycleMilliseconds) || weapon.CycleMilliseconds <= 0)
        {
            throw new InvalidDataException("Invalid NPC weapon cycle attributes.");
        }
        var signature =
            DroneSignature(contact.Profile.TypeId)
            * contact.MobilitySignatureMultiplier
            * Eden.Gameplay.Exploration.AbyssalDroneEnvironment.SignatureMultiplier(contact);
        var retaliation = DroneRules.Retaliation(contact, npc.Position, npc.Velocity, signature, weapon, roll, npc.Radius, now);
        if (retaliation.Result.Remaining.Destroyed)
        {
            return false;
        }
        _liveDrones[contact.Id] = drone with
        {
            Contact = drone.Contact with { Health = drone.Contact.Health with { Remaining = retaliation.Result.Remaining } },
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

    private readonly ResidentMap<int, double> _droneSignatures = [];

    private double DroneSignature(int type)
    {
        if (!_droneSignatures.TryGetValue(type, out double signature))
        {
            signature = Loading(() =>
            {
                using CatalogDbContext catalog = new(database.CatalogPath);
                return PilotFittingStore.ReadAttributes(catalog, type).GetValueOrDefault(DogmaAttributeId.SignatureRadius);
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
                    ReadyAt={drone.ReadyAt},CommandedAt={drone.CommandedAt},ShotAt={contact.ShotAt},ShotTargetId={contact.ShotTargetId},
                    DelegationMode={(int)(contact.Delegation?.Mode ?? DroneDelegationMode.None)},
                    DelegatePilotId={contact.Delegation?.PilotId ?? 0},DelegateShipId={contact.Delegation?.ShipId ?? 0},
                    DelegateFleetId={contact.Delegation?.FleetId ?? 0},OwnerMembership={contact.Delegation?.OwnerMembership ?? 0},
                    DelegateMembership={contact.Delegation?.DelegateMembership ?? 0},ReactionRevision={contact.Delegation?.ReactionRevision
                    ?? 0}
                WHERE ItemId={contact.Id}
                """
            );
        }
    }

    private void ObserveDrones(IReadOnlyCollection<CombatRowChange> changes)
    {
        HashSet<long> updated = [];
        foreach (var change in changes)
        {
            if (change.Table == "WorldDroneElectronicWarfare")
            {
                if (_liveDrones.TryGetValue(change.Id, out var electronicTarget))
                {
                    var profile = owner.Economy.Inspect(world =>
                    {
                        using CatalogDbContext catalog = new(database.CatalogPath);
                        var saved = world.Set<WorldDrone>().Find(change.Id);
                        return saved is null ? electronicTarget.Contact.Profile : PilotDroneStore.Contact(world, catalog, saved).Profile;
                    });
                    _liveDrones[change.Id] = electronicTarget with { Contact = electronicTarget.Contact with { Profile = profile } };
                }
                continue;
            }

            var committed = change.Resident && change.Table == "WorldDrone" ? owner.Economy.Peek<WorldDrone>(change.Id) : null;
            var existing = _liveDrones.GetValueOrDefault(change.Id);
            bool sameMembership =
                committed is not null
                && existing is not null
                && committed.PilotId == existing.Contact.PilotId
                && committed.ControllerShipId == existing.Contact.ShipId
                && committed.SystemId == existing.Contact.SystemId;
            if (change.Table == "WorldDrone" && !sameMembership)
            {
                _droneControllers = null;
            }
            if (change.Table is "WorldPilot" or "WorldFitRevision" or "WorldDroneSettings")
            {
                _dronePilots.Remove(change.Id);
                _dronePilotSystems.Remove(change.Id);
            }
            var affected = _liveDrones
                .Values.Where(d =>
                    change.Table switch
                    {
                        "WorldPilot" or "WorldFitRevision" or "WorldDroneSettings" => d.Contact.PilotId == change.Id,
                        "WorldShip" => d.Contact.ShipId == change.Id,
                        "WorldDrone" or "WorldItem" => d.Contact.Id == change.Id,
                        _ => false,
                    }
                )
                .ToArray();
            foreach (var drone in affected)
            {
                if (sameMembership && committed is { } saved)
                {
                    if (!_dirtyDrones.Contains(drone.Contact.Id))
                    {
                        _liveDrones[drone.Contact.Id] = CommittedDrone(drone, saved);
                    }
                    updated.Add(drone.Contact.Id);
                    continue;
                }
                _droneSystems.Remove(drone.Contact.SystemId);
                _dronePilotSystems.Remove(drone.Contact.PilotId);
                _dronePilots.Remove(drone.Contact.PilotId);
                // Projection of another participant must never discard paid drone motion.
                if (!_dirtyDrones.Contains(drone.Contact.Id))
                {
                    _liveDrones.Remove(drone.Contact.Id);
                }
            }
        }
        var ids = changes.Where(c => c.Table == "WorldDrone" && !updated.Contains(c.Id)).Select(c => c.Id).ToArray();
        if (ids.Length > 0)
        {
            if (owner.Economy.IsLoaded && changes.Any(c => c.Table == "WorldDrone" && c.Resident))
            {
                foreach (long id in ids)
                {
                    if (owner.Economy.Peek<WorldDrone>(id) is { } drone)
                    {
                        _dronePilotSystems.Remove(drone.PilotId);
                        if (drone.SystemId is int system)
                        {
                            _droneSystems.Remove(system);
                        }
                    }
                }
                return;
            }
            Loading(() =>
            {
                using WorldDbContext world = new(database.Path) { IsResidentProjection = true };
                foreach (var drone in world.Set<WorldDrone>().Where(d => ids.Contains(d.ItemId)).Select(d => new { d.PilotId, d.SystemId }))
                {
                    _dronePilotSystems.Remove(drone.PilotId);
                    if (drone.SystemId is int system)
                    {
                        _droneSystems.Remove(system);
                    }
                }
                return true;
            });
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
