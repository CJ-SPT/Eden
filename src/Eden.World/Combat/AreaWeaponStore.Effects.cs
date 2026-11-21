using System.Text.Json;
using Eden.Data.World.Characters;
using Eden.Data.World.Combat;
using Eden.Data.World.Combat.Npcs;
using Eden.Data.World.Inventory;
using Eden.Data.World.Propulsion;
using Eden.Gameplay.Combat;
using Eden.Gameplay.Dogma;
using Eden.Gameplay.Flight;
using Eden.Gameplay.Inventory;
using Eden.Gameplay.Propulsion;
using Eden.World.Catalog;
using Eden.World.Characters;
using Eden.World.Combat.Npcs;
using Eden.World.Drones;
using Eden.World.Fittings;
using Eden.World.Runtime.Economy;
using Eden.World.Storage;

namespace Eden.World.Combat;

internal sealed partial class AreaWeaponStore
{
    private void Project(
        WorldAction world,
        CatalogDbContext catalog,
        SavedAreaWeapon row,
        AreaWeaponFlight flight,
        long now,
        List<long> broken
    )
    {
        var source = world.Pilots.Find(row.PilotId);
        var item = world.Set<OwnedItem>().Find(row.ModuleId);
        if (
            source?.ActiveShipId != row.SourceShipId
            || source.ActiveShip.Position is null
            || CrimewatchPersistence.System(catalog, source.ActiveShip) != row.SystemId
            || item is not { Online: true }
            || item.LocationId != row.SourceShipId
            || world.Set<ShipLoss>().Find(row.SourceShipId) is not null
        )
        {
            RemoveField(world, row);
            return;
        }
        var module = flight.Module;
        if (module.Effect == DogmaEffect.ProjectorBubble)
        {
            if (
                !world
                    .Filter<SavedInterdictionField>(field => field.ModuleId == row.ModuleId && field.Kind == InterdictionKind.Projector)
                    .Any()
            )
            {
                world.Add(
                    new SavedInterdictionField
                    {
                        Id = world.Next("SavedInterdictionField", InterdictionRules.FirstProbeId),
                        PilotId = row.PilotId,
                        SourceShipId = row.SourceShipId,
                        ModuleId = row.ModuleId,
                        TypeId = module.TypeId,
                        SystemId = row.SystemId,
                        Kind = InterdictionKind.Projector,
                        X = flight.Point.X,
                        Y = flight.Point.Y,
                        Z = flight.Point.Z,
                        Radius = module.Radius,
                        HitPoints = 1,
                        StartedAt = now,
                        ExpiresAt = flight.ResolvesAt,
                    }
                );
            }
            return;
        }
        var targets = Targets(world, catalog, row.SystemId, now)
            .Where(target => target.Id != row.SourceShipId && target.Position.DistanceTo(flight.Point) <= module.Radius + target.Radius)
            .ToArray();
        var ids = targets.Select(target => target.Id).ToHashSet();
        foreach (var old in world.Filter<WorldPlayerEwar>(effect => effect.ModuleId == row.ModuleId).ToArray())
        {
            if (!ids.Contains(old.TargetId))
            {
                world.Remove(old);
            }
        }
        foreach (var target in targets)
        {
            if (world.Filter<WorldPlayerEwar>(effect => effect.ModuleId == row.ModuleId && effect.TargetId == target.Id).Any())
            {
                continue;
            }
            Aggression(world, catalog, source, row.SourceShipId, module.TypeId, target, row.SystemId, now, flight.ResolvesAt);
            if (module.Effect == DogmaEffect.ProjectorNeutralizer)
            {
                Drain(
                    world,
                    target,
                    module.Power.CapacitorDrain
                        * AreaWeaponRules.SignatureFactor(
                            target.Attributes.GetValueOrDefault(DogmaAttributeId.SignatureRadius),
                            catalog.FittingAttributes(module.TypeId)[DogmaAttributeId.ProjectorSignatureResolution]
                        ),
                    now
                );
                continue;
            }
            NpcEwarKind kind = module.Effect switch
            {
                DogmaEffect.ProjectorEcm => NpcEwarKind.Jamming,
                DogmaEffect.ProjectorWeb => NpcEwarKind.Webifying,
                DogmaEffect.ProjectorDampener => NpcEwarKind.SensorDampening,
                DogmaEffect.ProjectorPainter => NpcEwarKind.TargetPainting,
                _ => NpcEwarKind.TrackingDisruption,
            };
            NpcEwarPower? power = kind == NpcEwarKind.Jamming && !Jam(module, target.Attributes) ? null : module.Power;
            ElectronicWarfareModule adapter = new(
                module.ItemId,
                module.TypeId,
                module.Effect,
                kind,
                (flight.ResolvesAt - now) / (double)TimeSpan.TicksPerMillisecond,
                0,
                module.Radius,
                0,
                module.Power
            );
            ElectronicWarfareContact contact = new(row.PilotId, row.SourceShipId, adapter, Identity(target), power, now, flight.ResolvesAt);
            world.Add(
                new WorldPlayerEwar
                {
                    ModuleId = row.ModuleId,
                    TargetId = target.Id,
                    PilotId = row.PilotId,
                    SourceShipId = row.SourceShipId,
                    SystemId = row.SystemId,
                    TargetKind = contact.Target.Kind,
                    ContactJson = JsonSerializer.Serialize(contact),
                }
            );
            if (kind == NpcEwarKind.Jamming && power is not null)
            {
                Break(world, target, broken);
            }
        }
    }

    private void HitPilot(
        WorldAction world,
        CatalogDbContext catalog,
        SavedAreaWeapon row,
        Pilot source,
        Target target,
        Pilot victim,
        int weapon,
        DamageVector damage,
        long now,
        List<PilotDamage> notifications
    )
    {
        var defense = ShipHealth.DefenseFrom(target.Attributes);
        var hit = PilotHealthStore.Apply(world, target.Id, defense, damage, now);
        var attacker = PilotInteractionHistoryStore.Player(world, source, row.SourceShipId, weapon);
        PilotInteractionHistoryStore.Damage(world, target.Id, attacker, hit.Applied);
        var loss = hit.Remaining.Destroyed
            ? losses.Apply(world, catalog, victim, target.Id, row.SystemId, victim.ActiveShip.Position!, now, attacker)
            : null;
        world.SaveChanges();
        var replacement = loss is null ? null : lookup.FindPilot(world, victim.AccountId, victim.Id);
        notifications.Add(
            new(
                victim.Id,
                row.PilotId,
                row.SourceShipId,
                new(target.Id, defense, hit.Remaining),
                damage,
                hit.Applied,
                1,
                weapon,
                false,
                replacement,
                loss?.Insurance
            )
            {
                InventoryChanges = loss?.Changes ?? [],
            }
        );
    }

    private bool Explode(
        WorldAction world,
        CatalogDbContext catalog,
        SavedAreaWeapon row,
        AreaWeaponFlight flight,
        long now,
        List<long> broken,
        List<PilotDamage> notifications
    )
    {
        var bomb = flight.Bomb!;
        var center = flight.Position(flight.ResolvesAt);
        var source = world.Pilots.Find(row.PilotId)!;
        bool destroyedNpc = false;
        foreach (
            var target in Targets(world, catalog, row.SystemId, now)
                .Where(target => target.Position.DistanceTo(center) <= bomb.Radius + target.Radius)
        )
        {
            if (target.Npc is { } candidate && !Eden.World.FactionWarfare.MilitiaObjectiveStore.CanAttack(world, candidate, source))
            {
                continue;
            }
            Aggression(world, catalog, source, row.SourceShipId, flight.Module.TypeId, target, row.SystemId, now, now);
            double signature = target.Attributes.GetValueOrDefault(DogmaAttributeId.SignatureRadius);
            double factor = AreaWeaponRules.SignatureFactor(signature, bomb.SignatureResolution);
            var damage = bomb.Damage.Scale(factor);
            if (bomb.Power.CapacitorDrain > 0)
            {
                Drain(world, target, bomb.Power.CapacitorDrain * factor, now);
            }
            if (bomb.GroupId == AreaWeaponRules.LockbreakerBombGroup && Jam(flight.Module with { Power = bomb.Power }, target.Attributes))
            {
                Break(world, target, broken);
            }
            if (target.Npc is { } npc)
            {
                var hit = NpcDamagePersistence.Apply(
                    world,
                    catalog,
                    npc,
                    source,
                    row.SourceShipId,
                    target.Attributes,
                    damage,
                    now,
                    flight.Module.TypeId
                );
                destroyedNpc |= hit.Destroyed;
            }
            else if (target.Drone is { } drone)
            {
                SmartbombStore.DamageDrone(world, drone, PilotDroneStore.Contact(world, catalog, drone), damage, now);
            }
            else if (target.Pilot is { } victim)
            {
                HitPilot(world, catalog, row, source, target, victim, flight.Module.TypeId, damage, now, notifications);
            }
        }
        foreach (var other in world.Filter<SavedAreaWeapon>(other => other.SystemId == row.SystemId && other.Id != row.Id).ToArray())
        {
            var next = other.Flight();
            if (next.Bomb is { } profile && next.Position(flight.ResolvesAt).DistanceTo(center) <= bomb.Radius)
            {
                var hit = CombatDamage.Apply(
                    next.Health ?? profile.Defense.Undamaged,
                    profile.Defense,
                    bomb.Damage.Scale(AreaWeaponRules.SignatureFactor(profile.SignatureRadius, bomb.SignatureResolution)),
                    other.Id,
                    now
                );
                if (hit.Remaining.Destroyed)
                {
                    world.Remove(other);
                }
                else
                {
                    other.Save(next with { Health = hit.Remaining });
                }
            }
        }
        return destroyedNpc;
    }

    private static void Drain(WorldAction world, Target target, double amount, long now)
    {
        amount *= target.Attributes.GetValueOrDefault(DogmaAttributeId.EnergyWarfareResistance, 1);
        if (target.Pilot is not null)
        {
            double charge = ShipResources.Capacitor(world, target.Id, target.Attributes, now);
            ShipResources.SpendCapacitor(world, target.Id, target.Attributes, Math.Min(charge, amount), now);
        }
        else if (target.Npc is not null)
        {
            var saved = world.Set<WorldNpcEnergy>().Find(target.Id);
            var pool = NpcCapacitorRules.Read(NpcCapacitorRules.Profile(target.Attributes), saved?.State(), now);
            saved ??= world.Add(new WorldNpcEnergy { NpcId = target.Id }).Entity;
            saved.Charge = Math.Max(0, pool.Charge - amount);
            saved.UpdatedAt = now;
        }
    }
}
