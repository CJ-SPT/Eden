using Eden.Data.Catalog;
using Eden.Data.World.Characters;
using Eden.Data.World.Combat;
using Eden.Data.World.Combat.Npcs;
using Eden.Gameplay.Combat;
using Eden.Gameplay.Docking;
using Eden.Gameplay.Dogma;
using Eden.Gameplay.Drones;
using Eden.Gameplay.Flight;
using Eden.Gameplay.Insurance;
using Eden.Infrastructure.Registration;
using Eden.World.Catalog;
using Eden.World.Characters;
using Eden.World.Combat.Weapons;
using Eden.World.Docking;
using Eden.World.Drones;
using Eden.World.Fittings;
using Eden.World.Propulsion;
using Eden.World.Runtime.Combat;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;

namespace Eden.World.Combat.Npcs;

[Injectable]
internal sealed class NpcCombatStore(
    WorldDatabase database,
    SqliteWorld lookup,
    IShipLootRoll lootRoll,
    NpcSpawnCatalog? profiles = null,
    NpcProfileCatalog? hullProfiles = null
) : INpcCombat
{
    private double MissileRange(CatalogDbContext catalog, WorldNpc npc, IReadOnlyDictionary<DogmaAttributeId, double> attributes)
    {
        if (profiles?.Profiles.FirstOrDefault(p => p.TypeId == npc.TypeId) is { } profile)
        {
            return profile.MissileRange;
        }
        int chargeId = checked((int)attributes.GetValueOrDefault(DogmaAttributeId.EntityMissileType));
        if (chargeId == 0)
        {
            return 0;
        }
        var charge = PilotFittingStore.ReadAttributes(catalog, chargeId);
        return NpcMissileRules.Drive(MissileChargeCatalog.Drive(catalog, chargeId, charge), attributes).NominalRange;
    }

    public void Acquire(IReadOnlyList<NpcTargetCandidate> candidates, long now)
    {
        using var phase = Eden.Infrastructure.Diagnostics.SessionPerformance.Phase("npc.acquire");
        if (LiveCombatRuntime.Find(database.Path)?.Acquire(candidates, now, () => AcquireSql(candidates, now)) == true)
        {
            return;
        }
        AcquireSql(candidates, now);
    }

    private void AcquireSql(IReadOnlyList<NpcTargetCandidate> candidates, long now)
    {
        if (candidates.Count == 0)
        {
            return;
        }
        using WorldDbContext world = new(database.Path);
        using var transaction = world.Database.BeginTransaction();
        using CatalogDbContext catalog = new(database.CatalogPath);
        var requested = candidates.ToHashSet();
        var pilotIds = requested.Select(c => c.PilotId).ToArray();
        var pilots = world
            .Pilots.Include(p => p.ActiveShip)
                .ThenInclude(s => s.Position)
            .Where(p => pilotIds.Contains(p.Id) && p.ActiveShip.Position != null)
            .ToArray();
        var targets = pilots
            .Where(p => requested.Contains(new(p.Id, p.ActiveShipId)))
            .Select(p => (Pilot: p, Hull: catalog.RequireItem(p.ActiveShip.TypeId)))
            .Where(t => t.Hull.GroupId != FleetDefinitions.CapsuleGroup)
            .ToArray();
        if (targets.Length == 0)
        {
            return;
        }
        var systems = targets
            .Select(t => t.Pilot.ActiveShip.Position!.SystemId ?? catalog.RequireStation(t.Pilot.ActiveShip.StationId).SolarSystemId)
            .Distinct()
            .ToArray();
        var occupied = world
            .Set<NpcAggression>()
            .Where(a => world.Set<WorldNpc>().Any(n => n.Id == a.NpcId && systems.Contains(n.SystemId)))
            .Select(a => a.NpcId)
            .ToArray();
        var npcs = world
            .Set<WorldNpc>()
            .Where(n =>
                systems.Contains(n.SystemId)
                && n.Role == NpcRole.Pirate
                && n.DestroyedAt == null
                && n.RetiredAt == null
                && n.Navigation == NpcNavigationState.Idle
                && !occupied.Contains(n.Id)
            )
            .OrderBy(n => n.Id)
            .ToArray();
        foreach (var npc in npcs)
        {
            var profile = hullProfiles?.Read(catalog, npc.TypeId);
            var attributes = profile?.Attributes ?? PilotFittingStore.ReadAttributes(catalog, npc.TypeId);
            double missileRange = MissileRange(catalog, npc, attributes);
            var type = profile?.Type ?? catalog.RequireItem(npc.TypeId);
            if (
                !attributes.TryGetValue(DogmaAttributeId.EntityAttackDelayMinimum, out double delay)
                && type.GroupId != NpcEncounterDefinitions.SentryGroup
            )
            {
                continue;
            }
            var home = NpcMotion.Home(npc, catalog);
            var target = targets
                .Where(t =>
                    (t.Pilot.ActiveShip.Position!.SystemId ?? catalog.RequireStation(t.Pilot.ActiveShip.StationId).SolarSystemId)
                    == npc.SystemId
                )
                .Select(t =>
                {
                    var saved = t.Pilot.ActiveShip.Position!;
                    SpacePosition position = new(saved.X, saved.Y, saved.Z);
                    return (
                        t.Pilot.ActiveShipId,
                        Position: position,
                        Range: Math.Max(0, position.DistanceTo(NpcMotion.Position(npc)) - (type.Radius ?? 0) - (t.Hull.Radius ?? 0))
                    );
                })
                .Where(t =>
                    t.Range <= NpcPopulationRules.DetectionRange(attributes, missileRange, npc.AcquisitionRange)
                    && t.Position.DistanceTo(home) <= NpcPopulationRules.Leash(attributes, missileRange, npc.AcquisitionRange)
                )
                .OrderBy(t => t.Range)
                .ThenBy(t => t.ActiveShipId)
                .FirstOrDefault();
            if (target.Position is null)
            {
                continue;
            }
            // One NPC retains one target until disengagement. Serializing the
            // selection with its row prevents competing viewers resetting delay.
            world.Set<NpcAggression>().Add(NpcAggression.Start(npc.Id, target.ActiveShipId, delay, now));
        }
        world.SaveChanges();
        transaction.Commit();
    }

    public IReadOnlyList<long> Aggressors(long shipId)
    {
        if (LiveCombatRuntime.Find(database.Path)?.Aggressors(shipId) is { } resident)
        {
            return resident;
        }
        using WorldDbContext world = new(database.Path);
        return world.Set<NpcAggression>().Where(a => a.TargetShipId == shipId).OrderBy(a => a.NpcId).Select(a => a.NpcId).ToArray();
    }

    public void Disengage(long shipId, long? npcId = null)
    {
        using WorldDbContext world = new(database.Path);
        using var transaction = world.Database.BeginTransaction();
        Disengage(world, shipId, npcId);
        transaction.Commit();
    }

    internal static void Disengage(WorldDbContext world, long shipId, long? npcId = null)
    {
        var aggression = world
            .Set<NpcAggression>()
            .Where(a =>
                a.TargetShipId == shipId
                && (npcId == null || a.NpcId == npcId)
                && !world.Set<WorldNpc>().Any(n => n.Id == a.NpcId && n.Role == NpcRole.Concord)
            )
            .ToArray();
        var ids = aggression.Select(a => a.NpcId).ToArray();
        foreach (var npc in world.Set<WorldNpc>().Where(n => ids.Contains(n.Id) && n.DestroyedAt == null && n.RetiredAt == null))
        {
            NpcMotion.Return(npc);
        }
        world.RemoveRange(aggression);
        world
            .Set<WorldMissile>()
            .Where(m =>
                m.SourceKind == MissileSourceKind.Npc
                && m.TargetId == shipId
                && (npcId == null || m.SourceShipId == npcId)
                && m.Status == MissileFlightStatus.Flying
            )
            .ExecuteUpdate(set =>
                set.SetProperty(m => m.Status, MissileFlightStatus.Expired).SetProperty(m => m.EndedAt, m => m.UpdatedAt)
            );
        world.SaveChanges();
    }

    public NpcShot? Fire(long pilotId, long shipId, long npcId, SpacePosition velocity, long now, double roll, int destinyStamp)
    {
        if (!double.IsFinite(roll) || roll is < 0 or >= 1 || !double.IsFinite(velocity.DistanceTo(SpacePosition.Zero)))
        {
            throw new InvalidDataException("Invalid NPC weapon accuracy input.");
        }
        if (
            LiveCombatRuntime.Find(database.Path) is { } runtime
            && runtime.FireNpc(pilotId, shipId, npcId, velocity, now, roll, destinyStamp, out var resident)
        )
        {
            return resident;
        }
        using WorldDbContext world = new(database.Path);
        using var transaction = world.Database.BeginTransaction();
        using CatalogDbContext catalog = new(database.CatalogPath);
        var aggression = world.Set<NpcAggression>().Find(npcId);
        if (aggression is null || aggression.TargetShipId != shipId)
        {
            return null;
        }
        var pilot = world.Pilots.Include(p => p.ActiveShip).ThenInclude(s => s.Position).Single(p => p.Id == pilotId);
        var npc = world.Set<WorldNpc>().Find(npcId);
        if (npc?.Navigation == NpcNavigationState.Warping)
        {
            return null;
        }
        var hull = catalog.RequireItem(pilot.ActiveShip.TypeId);
        var profile = npc is null ? null : hullProfiles?.Read(catalog, npc.TypeId);
        var source = npc is null ? null : profile?.Type ?? catalog.RequireItem(npc.TypeId);
        var attributes = npc is null
            ? new Dictionary<DogmaAttributeId, double>()
            : PilotTackleStore.NpcAttributes(
                world,
                npc.Id,
                profile?.Attributes ?? PilotFittingStore.ReadAttributes(catalog, npc.TypeId),
                MissileRange(catalog, npc, profile?.Attributes ?? PilotFittingStore.ReadAttributes(catalog, npc.TypeId))
            );
        var position = pilot.ActiveShip.Position;
        int system = position?.SystemId ?? catalog.RequireStation(pilot.ActiveShip.StationId).SolarSystemId;
        if (
            pilot.ActiveShipId != shipId
            || position is null
            || hull.GroupId == FleetDefinitions.CapsuleGroup
            || npc is null
            || npc.DestroyedAt.HasValue
            || npc.RetiredAt.HasValue
            || npc.Role == NpcRole.MissionStructure
            || NpcPopulationRules.IsCivilian(npc.Role)
            || npc.SystemId != system
        )
        {
            if (npc is not null && !npc.DestroyedAt.HasValue)
            {
                NpcMotion.Return(npc);
            }
            world.Remove(aggression);
            world.SaveChanges();
            transaction.Commit();
            return null;
        }
        var home = NpcMotion.Home(npc, catalog);
        double leash = NpcPopulationRules.Leash(attributes, MissileRange(catalog, npc, attributes), npc.AcquisitionRange);
        var targetPosition = new SpacePosition(position.X, position.Y, position.Z);
        if (targetPosition.DistanceTo(home) > leash || NpcMotion.Position(npc).DistanceTo(home) > leash)
        {
            NpcMotion.Return(npc);
            world.Remove(aggression);
            world.SaveChanges();
            transaction.Commit();
            return null;
        }
        if (!NpcMotion.Engaged(npc) && now < Math.Min(aggression.ReadyAt, aggression.MissileReadyAt))
        {
            return null;
        }
        if (database.MotionStamps.ContainsKey(system))
        {
            npc.MotionAt = now;
        }
        NpcMotion.Pursue(npc, targetPosition, hull.Radius ?? 0, source!, attributes, now, destinyStamp);
        var separation = targetPosition - NpcMotion.Position(npc);
        double range = Math.Max(0, separation.DistanceTo(SpacePosition.Zero) - (hull.Radius ?? 0) - (source!.Radius ?? 0));
        var playerEwar = PlayerEwarStore.Active(world, npc.Id);
        if (
            !PlayerEwarRules.CanTarget(playerEwar, shipId)
            || attributes.TryGetValue(DogmaAttributeId.MaxTargetRange, out double targetRange) && targetRange > 0 && range > targetRange
        )
        {
            world.SaveChanges();
            transaction.Commit();
            return null;
        }
        NpcMissileStore.Launch(world, catalog, npc, aggression, attributes, pilot, velocity, now, destinyStamp);
        if (
            DroneNpcCombat.TryFire(
                world,
                catalog,
                npc,
                aggression,
                attributes,
                shipId,
                now,
                roll,
                out bool droneTargetAvailable,
                playerEwar
            ) is
            { } droneShot
        )
        {
            world.SaveChanges();
            transaction.Commit();
            return droneShot;
        }
        if (droneTargetAvailable)
        {
            // Target choice and shot quality each need a uniform conditional draw.
            roll = DroneRules.OwnerAccuracyRoll(roll);
        }
        if (
            !NpcPopulationRules.HasDirectWeapon(attributes)
            || range > attributes.GetValueOrDefault(DogmaAttributeId.EntityAttackRange)
            || now < aggression.ReadyAt
        )
        {
            world.SaveChanges();
            transaction.Commit();
            return null;
        }
        double duration = attributes.GetValueOrDefault(DogmaAttributeId.WeaponCycleTime);
        if (attributes.Values.Any(v => !double.IsFinite(v)) || duration <= 0)
        {
            throw new InvalidDataException("Invalid NPC weapon cycle attributes.");
        }
        var target = PilotFittingStore.ShipAttributes(world, catalog, pilotId);
        var defense = ShipHealth.DefenseFrom(target);
        DamageVector damage = new(
            attributes.GetValueOrDefault(DogmaAttributeId.EmDamage),
            attributes.GetValueOrDefault(DogmaAttributeId.ExplosiveDamage),
            attributes.GetValueOrDefault(DogmaAttributeId.KineticDamage),
            attributes.GetValueOrDefault(DogmaAttributeId.ThermalDamage)
        );
        damage = damage.Scale(attributes.GetValueOrDefault(DogmaAttributeId.DamageMultiplier));
        double chance = CombatDamage.HitChance(
            range,
            CombatDamage.AngularVelocity(separation, velocity - NpcMotion.Velocity(npc)),
            target.GetValueOrDefault(DogmaAttributeId.SignatureRadius),
            attributes.GetValueOrDefault(DogmaAttributeId.MaxRange),
            attributes.GetValueOrDefault(DogmaAttributeId.AccuracyFalloff),
            attributes.GetValueOrDefault(DogmaAttributeId.TrackingSpeed),
            attributes.GetValueOrDefault(DogmaAttributeId.WeaponSignatureResolution)
        );
        double quality = CombatDamage.ShotQuality(chance, roll);
        var result = PilotHealthStore.Apply(world, shipId, defense, damage.Scale(quality), now);
        aggression.LastShotAt = now;
        aggression.LastTargetId = shipId;
        Eden.World.Drones.DroneCombatPersistence.Incoming(world, shipId, npcId, now);
        aggression.ReadyAt = checked(now + (long)Math.Ceiling(duration * TimeSpan.TicksPerMillisecond));
        CrimewatchPersistence.Npc(world, pilotId, system, aggression.ReadyAt);
        NpcWeaponContact weapon = new(shipId, checked((int)attributes.GetValueOrDefault(DogmaAttributeId.EntityTurretType)), now, duration);
        PilotInteractionHistoryStore.Damage(world, shipId, PilotInteractionHistoryStore.Npc(npc, weapon.TurretTypeId), result.Applied);
        InsurancePayout? insurance = null;
        if (result.Remaining.Destroyed)
        {
            insurance = ShipLossPersistence.Apply(world, catalog, pilot, npcId, shipId, system, position, now, lootRoll);
        }
        world.SaveChanges();
        transaction.Commit();
        return new(
            npc.Id,
            npc.TypeId,
            weapon,
            damage,
            result.Applied,
            quality,
            new(shipId, defense, result.Remaining),
            result.Remaining.Destroyed ? lookup.FindPilot(pilot.AccountId, pilotId)! : null,
            insurance
        );
    }
}
