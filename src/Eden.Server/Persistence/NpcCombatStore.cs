using System.Text.Json;
using Eden.Gameplay.Combat;
using Eden.Gameplay.Docking;
using Eden.Gameplay.Dogma;
using Eden.Gameplay.Flight;
using Eden.Gameplay.Inventory;
using Eden.Protocol;
using Eden.Protocol.Combat;
using Eden.Protocol.Docking;
using Eden.Server.Catalog;
using Microsoft.EntityFrameworkCore;

namespace Eden.Server.Persistence;

[Injectable]
internal sealed class NpcCombatStore(WorldDatabase database, SqliteWorld lookup, IShipLootRoll lootRoll) : INpcCombat
{
    public void Acquire(IReadOnlyList<NpcTargetCandidate> candidates, long now)
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
            .Select(p => (Pilot: p, Hull: catalog.Set<ItemType>().Single(t => t.Id == p.ActiveShip.TypeId)))
            .Where(t =>
                t.Hull.GroupId != FleetDefinitions.CapsuleGroup
                && (
                    t.Pilot.ActiveShip.Position!.SystemId
                    ?? catalog.Set<Station>().Single(s => s.Id == t.Pilot.ActiveShip.StationId).SolarSystemId
                ) == NpcEncounterDefinitions.PerimeterSystem
            )
            .ToArray();
        if (targets.Length == 0)
        {
            return;
        }
        var occupied = world.Set<NpcAggression>().Select(a => a.NpcId).ToArray();
        var npcs = world
            .Set<WorldNpc>()
            .Where(n =>
                n.SystemId == NpcEncounterDefinitions.PerimeterSystem
                && n.DestroyedAt == null
                && n.Navigation == NpcNavigationState.Idle
                && !occupied.Contains(n.Id)
            )
            .OrderBy(n => n.Id)
            .ToArray();
        foreach (var npc in npcs)
        {
            var attributes = PilotFittingStore.ReadAttributes(catalog, npc.TypeId);
            if (!attributes.TryGetValue(DogmaAttributeId.EntityAttackDelayMinimum, out double delay))
            {
                continue;
            }
            var type = catalog.Set<ItemType>().Single(t => t.Id == npc.TypeId);
            var home = NpcMotion.Home(catalog.Set<Celestial>().Single(b => b.Id == npc.BeltId));
            var target = targets
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
                    t.Range <= NpcEncounterDefinitions.DetectionRange && t.Position.DistanceTo(home) <= NpcEncounterDefinitions.ChaseLeash
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
        using WorldDbContext world = new(database.Path);
        return world.Set<NpcAggression>().Where(a => a.TargetShipId == shipId).OrderBy(a => a.NpcId).Select(a => a.NpcId).ToArray();
    }

    public void Disengage(long shipId, long? npcId = null)
    {
        using WorldDbContext world = new(database.Path);
        using var transaction = world.Database.BeginTransaction();
        var aggression = world.Set<NpcAggression>().Where(a => a.TargetShipId == shipId && (npcId == null || a.NpcId == npcId)).ToArray();
        var ids = aggression.Select(a => a.NpcId).ToArray();
        foreach (var npc in world.Set<WorldNpc>().Where(n => ids.Contains(n.Id) && n.DestroyedAt == null))
        {
            NpcMotion.Return(npc);
        }
        world.RemoveRange(aggression);
        world.SaveChanges();
        transaction.Commit();
    }

    public NpcShot? Fire(long pilotId, long shipId, long npcId, SpacePosition velocity, long now, double roll, int destinyStamp)
    {
        if (!double.IsFinite(roll) || roll is < 0 or >= 1 || !double.IsFinite(velocity.DistanceTo(SpacePosition.Zero)))
        {
            throw new InvalidDataException("Invalid NPC weapon accuracy input.");
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
        var hull = catalog.Set<ItemType>().Single(t => t.Id == pilot.ActiveShip.TypeId);
        var source = npc is null ? null : catalog.Set<ItemType>().Single(t => t.Id == npc.TypeId);
        var attributes = npc is null ? new Dictionary<DogmaAttributeId, double>() : PilotFittingStore.ReadAttributes(catalog, npc.TypeId);
        var position = pilot.ActiveShip.Position;
        int system = position?.SystemId ?? catalog.Set<Station>().Single(s => s.Id == pilot.ActiveShip.StationId).SolarSystemId;
        if (
            pilot.ActiveShipId != shipId
            || position is null
            || hull.GroupId == FleetDefinitions.CapsuleGroup
            || npc is null
            || npc.DestroyedAt.HasValue
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
        var home = NpcMotion.Home(catalog.Set<Celestial>().Single(b => b.Id == npc.BeltId));
        var targetPosition = new SpacePosition(position.X, position.Y, position.Z);
        if (
            targetPosition.DistanceTo(home) > NpcEncounterDefinitions.ChaseLeash
            || NpcMotion.Position(npc).DistanceTo(home) > NpcEncounterDefinitions.ChaseLeash
        )
        {
            NpcMotion.Return(npc);
            world.Remove(aggression);
            world.SaveChanges();
            transaction.Commit();
            return null;
        }
        if (!NpcMotion.Engaged(npc) && now < aggression.ReadyAt)
        {
            return null;
        }
        NpcMotion.Pursue(npc, targetPosition, hull.Radius ?? 0, source!, attributes, now, destinyStamp);
        var separation = targetPosition - NpcMotion.Position(npc);
        double range = Math.Max(0, separation.DistanceTo(SpacePosition.Zero) - (hull.Radius ?? 0) - (source!.Radius ?? 0));
        if (range > attributes.GetValueOrDefault(DogmaAttributeId.EntityAttackRange) || now < aggression.ReadyAt)
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
        aggression.ReadyAt = checked(now + (long)Math.Ceiling(duration * TimeSpan.TicksPerMillisecond));
        NpcWeaponContact weapon = new(shipId, checked((int)attributes.GetValueOrDefault(DogmaAttributeId.EntityTurretType)), now, duration);
        if (result.Remaining.Destroyed)
        {
            var old = pilot.ActiveShip;
            var moduleIds = world.Set<OwnedItem>().Where(i => i.LocationId == shipId && i.OwnerId == pilotId).Select(i => i.Id).ToArray();
            var items = world
                .Set<OwnedItem>()
                .Where(i => i.OwnerId == pilotId && (i.LocationId == shipId || moduleIds.Contains(i.LocationId)))
                .OrderBy(i => i.Id)
                .ToArray();
            string manifest = JsonSerializer.Serialize(items);
            var survivors = items.Where(_ => lootRoll.Survives()).Select(i => i.Id).ToHashSet();
            var capsuleType =
                catalog
                    .Set<ItemType>()
                    .SingleOrDefault(t => t.Id == FleetDefinitions.CapsuleType && t.GroupId == FleetDefinitions.CapsuleGroup)
                ?? throw new InvalidDataException("The catalog has no supported capsule.");
            var capsule = world
                .Ships.Add(
                    new()
                    {
                        Id = InventoryPersistence.NextId(world),
                        OwnerId = pilotId,
                        TypeId = capsuleType.Id,
                        StationId = old.StationId,
                        Name = capsuleType.Name,
                        Position = new()
                        {
                            SystemId = system,
                            X = position.X,
                            Y = position.Y,
                            Z = position.Z,
                        },
                    }
                )
                .Entity;
            world
                .Set<ShipLoss>()
                .Add(
                    new()
                    {
                        ShipId = shipId,
                        PilotId = pilotId,
                        NpcId = npcId,
                        CapsuleId = capsule.Id,
                        TypeId = old.TypeId,
                        SystemId = system,
                        Time = now,
                        Name = old.Name,
                        ItemsJson = manifest,
                        DroppedItemIdsJson = JsonSerializer.Serialize(survivors.Order()),
                        X = position.X,
                        Y = position.Y,
                        Z = position.Z,
                    }
                );
            WreckStore.CreatePlayer(world, catalog, old, pilot, system, now, items, survivors);
            pilot.ActiveShip = capsule;
            pilot.ActiveShipId = capsule.Id;
            // Satisfy the active-ship FK before deleting the old hull. Both saves
            // belong to this transaction: a failed capsule/loss cannot consume it.
            world.SaveChanges();
            world.Remove(old);
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
            result.Remaining.Destroyed ? lookup.FindPilot(pilot.AccountId, pilotId)! : null
        );
    }
}
