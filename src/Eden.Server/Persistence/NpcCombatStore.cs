using System.Text.Json;
using Eden.Protocol;
using Eden.Protocol.Combat;
using Eden.Protocol.Docking;
using Eden.Server.Catalog;
using Microsoft.EntityFrameworkCore;

namespace Eden.Server.Persistence;

[Injectable]
internal sealed class NpcCombatStore(WorldDatabase database, IPilotFitting fitting, SqliteWorld lookup, IShipLootRoll lootRoll) : INpcCombat
{
    public IReadOnlyList<long> Aggressors(long shipId)
    {
        using WorldDbContext world = new(database.Path);
        return world.Set<NpcAggression>().Where(a => a.TargetShipId == shipId).OrderBy(a => a.NpcId).Select(a => a.NpcId).ToArray();
    }

    public void Disengage(long shipId, long? npcId = null)
    {
        using WorldDbContext world = new(database.Path);
        world.Set<NpcAggression>().Where(a => a.TargetShipId == shipId && (npcId == null || a.NpcId == npcId)).ExecuteDelete();
    }

    public NpcShot? Fire(long pilotId, long shipId, long npcId, SpacePosition velocity, long now, double roll)
    {
        if (!double.IsFinite(roll) || roll is < 0 or >= 1 || !double.IsFinite(velocity.DistanceTo(SpacePosition.Zero)))
        {
            throw new InvalidDataException("Invalid NPC weapon accuracy input.");
        }
        using WorldDbContext world = new(database.Path);
        using var transaction = world.Database.BeginTransaction();
        using CatalogDbContext catalog = new(database.CatalogPath);
        var aggression = world.Set<NpcAggression>().Find(npcId);
        if (aggression is null || aggression.TargetShipId != shipId) { return null; }
        var pilot = world.Pilots.Include(p => p.ActiveShip).ThenInclude(s => s.Position).Single(p => p.Id == pilotId);
        var npc = world.Set<WorldNpc>().Find(npcId);
        var hull = catalog.Set<ItemType>().Single(t => t.Id == pilot.ActiveShip.TypeId);
        var source = npc is null ? null : catalog.Set<ItemType>().Single(t => t.Id == npc.TypeId);
        var attributes = npc is null ? new Dictionary<DogmaAttributeId, double>() : fitting.Attributes(npc.TypeId);
        var position = pilot.ActiveShip.Position;
        int system = position?.SystemId ?? catalog.Set<Station>().Single(s => s.Id == pilot.ActiveShip.StationId).SolarSystemId;
        var separation = npc is null || position is null ? SpacePosition.Zero : new(position.X - npc.X, position.Y - npc.Y, position.Z - npc.Z);
        double range = Math.Max(0, separation.DistanceTo(SpacePosition.Zero) - (hull.Radius ?? 0) - (source?.Radius ?? 0));
        if (pilot.ActiveShipId != shipId || position is null || hull.GroupId == FleetDefinitions.CapsuleGroup ||
            npc is null || npc.DestroyedAt.HasValue || npc.SystemId != system || range > attributes.GetValueOrDefault(DogmaAttributeId.EntityAttackRange))
        {
            world.Remove(aggression);
            world.SaveChanges();
            transaction.Commit();
            return null;
        }
        if (now < aggression.ReadyAt) { return null; }
        double duration = attributes.GetValueOrDefault(DogmaAttributeId.WeaponCycleTime);
        if (attributes.Values.Any(v => !double.IsFinite(v)) || duration <= 0)
        {
            throw new InvalidDataException("Invalid NPC weapon cycle attributes.");
        }
        var target = fitting.ShipAttributes(pilotId);
        var defense = ShipHealth.DefenseFrom(target);
        DamageVector damage = new(attributes.GetValueOrDefault(DogmaAttributeId.EmDamage), attributes.GetValueOrDefault(DogmaAttributeId.ExplosiveDamage),
            attributes.GetValueOrDefault(DogmaAttributeId.KineticDamage), attributes.GetValueOrDefault(DogmaAttributeId.ThermalDamage));
        damage = damage.Scale(attributes.GetValueOrDefault(DogmaAttributeId.DamageMultiplier));
        double chance = CombatDamage.HitChance(range, CombatDamage.AngularVelocity(separation, velocity), target.GetValueOrDefault(DogmaAttributeId.SignatureRadius),
            attributes.GetValueOrDefault(DogmaAttributeId.MaxRange), attributes.GetValueOrDefault(DogmaAttributeId.AccuracyFalloff),
            attributes.GetValueOrDefault(DogmaAttributeId.TrackingSpeed), attributes.GetValueOrDefault(DogmaAttributeId.WeaponSignatureResolution));
        double quality = CombatDamage.ShotQuality(chance, roll);
        var result = PilotHealthStore.Apply(world, shipId, defense, damage.Scale(quality), now);
        aggression.LastShotAt = now;
        aggression.ReadyAt = checked(now + (long)Math.Ceiling(duration * TimeSpan.TicksPerMillisecond));
        NpcWeaponContact weapon = new(shipId, checked((int)attributes.GetValueOrDefault(DogmaAttributeId.EntityTurretType)), now, duration);
        if (result.Remaining.Destroyed)
        {
            var old = pilot.ActiveShip;
            var moduleIds = world.Set<OwnedItem>().Where(i => i.LocationId == shipId && i.OwnerId == pilotId).Select(i => i.Id).ToArray();
            var items = world.Set<OwnedItem>().Where(i => i.OwnerId == pilotId && (i.LocationId == shipId || moduleIds.Contains(i.LocationId))).OrderBy(i => i.Id).ToArray();
            string manifest = JsonSerializer.Serialize(items);
            var survivors = items.Where(_ => lootRoll.Survives()).Select(i => i.Id).ToHashSet();
            var capsuleType = catalog.Set<ItemType>().SingleOrDefault(t => t.Id == FleetDefinitions.CapsuleType && t.GroupId == FleetDefinitions.CapsuleGroup)
                ?? throw new InvalidDataException("The catalog has no supported capsule.");
            var capsule = world.Ships.Add(new()
            {
                Id = PilotInventoryStore.NextId(world),
                OwnerId = pilotId,
                TypeId = capsuleType.Id,
                StationId = old.StationId,
                Name = capsuleType.Name,
                Position = new() { SystemId = system, X = position.X, Y = position.Y, Z = position.Z },
            }).Entity;
            world.Set<ShipLoss>().Add(new()
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
            });
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
        return new(npc.Id, npc.TypeId, weapon, damage, result.Applied, quality, new(shipId, defense, result.Remaining),
            result.Remaining.Destroyed ? lookup.FindPilot(pilot.AccountId, pilotId)! : null);
    }
}
