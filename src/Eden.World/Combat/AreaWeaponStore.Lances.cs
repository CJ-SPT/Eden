using Eden.Data.World.Characters;
using Eden.Data.World.Combat;
using Eden.Data.World.Inventory;
using Eden.Gameplay.Combat;
using Eden.Gameplay.Dogma;
using Eden.Gameplay.Flight;
using Eden.World.Catalog;
using Eden.World.Combat.Npcs;
using Eden.World.Drones;
using Eden.World.Runtime;
using Eden.World.Runtime.Economy;
using Eden.World.Storage;

namespace Eden.World.Combat;

internal sealed partial class AreaWeaponStore
{
    public IReadOnlyList<TitanLanceContact> Lances(int systemId, long now)
    {
        if (LiveWorld.Find(database.Path)?.Economy is not { } economy || !economy.HasAreaWeapons(systemId))
        {
            return [];
        }
        using CatalogDbContext catalog = new(database.CatalogPath);
        return economy.Inspect(world =>
            world
                .Filter<SavedAreaWeapon>(row => row.SystemId == systemId)
                .AsEnumerable()
                .Select(row => (Row: row, Flight: row.Flight()))
                .Where(pair =>
                    !pair.Flight.Active
                    && pair.Flight.Module.Lance is { } lance
                    && now < AreaWeaponRules.Deadline(pair.Flight.StartedAt, pair.Flight.Module.Delay + lance.DamageDuration)
                )
                .Select(pair =>
                {
                    var (row, flight) = pair;
                    var type = catalog.RequireItem(TitanLanceRules.BeaconType);
                    StaticSpaceObject beacon = new(
                        row.Id,
                        type.Id,
                        type.GroupId,
                        type.CategoryId,
                        checked((int)row.PilotId),
                        flight.Point,
                        (float)(type.Radius ?? 250)
                    )
                    {
                        Solid = false,
                        Name = type.Name,
                    };
                    long end = AreaWeaponRules.Deadline(flight.StartedAt, flight.Module.Delay + flight.Module.Lance!.DamageDuration);
                    return new TitanLanceContact(
                        beacon,
                        flight.Origin,
                        row.SourceShipId,
                        row.ModuleId,
                        flight.Module.TypeId,
                        flight.StartedAt,
                        end
                    );
                })
                .ToArray()
        );
    }

    private void NeutralizeNearLance(
        WorldAction world,
        CatalogDbContext catalog,
        Pilot source,
        SpacePosition center,
        int system,
        int module,
        TitanLanceProfile lance,
        long now
    )
    {
        foreach (
            var victim in Targets(
                world,
                catalog,
                system,
                now,
                (position, radius) => position.DistanceTo(center) <= lance.NeutralizationRadius + radius
            )
        )
        {
            if (victim.Pilot is null || victim.Id == source.ActiveShipId)
            {
                continue;
            }
            double signature = victim.Attributes.GetValueOrDefault(DogmaAttributeId.SignatureRadius);
            double amount = lance.NeutralizationAmount * AreaWeaponRules.SignatureFactor(signature, lance.NeutralizationSignature);
            Aggression(world, catalog, source, source.ActiveShipId, module, victim, system, now, now);
            Drain(world, victim, amount, now);
        }
    }

    private bool SettleLance(
        WorldAction world,
        CatalogDbContext catalog,
        SavedAreaWeapon row,
        AreaWeaponFlight flight,
        long now,
        List<PilotDamage> notifications
    )
    {
        var charge = flight.Module.Doomsday!;
        var lance = flight.Module.Lance!;
        long damageStart = AreaWeaponRules.Deadline(flight.StartedAt, flight.Module.Delay);
        long damageEnd = AreaWeaponRules.Deadline(damageStart, lance.DamageDuration);
        long lockEnd = AreaWeaponRules.Deadline(flight.StartedAt, charge.DriveLockMilliseconds);
        if (flight.Active || now >= lockEnd)
        {
            world.Remove(row);
            return false;
        }
        if (
            now >= damageEnd
            || world.Pilots.Find(row.PilotId) is not { } source
            || source.ActiveShipId != row.SourceShipId
            || source.ActiveShip.Position is null
            || CrimewatchPersistence.System(catalog, source.ActiveShip) != row.SystemId
            || world.Set<ShipLoss>().Find(row.SourceShipId) is not null
            || world.Set<OwnedItem>().Find(row.ModuleId) is not { Online: true } weapon
            || weapon.LocationId != row.SourceShipId
        )
        {
            row.Save(flight with { Active = true, ResolvesAt = lockEnd });
            return false;
        }

        var axis = (flight.Point - flight.Origin).Unit;
        bool destroyedNpc = false;
        foreach (
            var target in Targets(
                world,
                catalog,
                row.SystemId,
                now,
                (position, radius) => TitanLanceRules.Intersects(flight.Origin, axis, flight.Module.Range, lance.Radius, position, radius)
            )
        )
        {
            if (
                target.Id == row.SourceShipId
                || target.Npc is { } candidate && !Eden.World.FactionWarfare.MilitiaObjectiveStore.CanAttack(world, candidate, source)
            )
            {
                continue;
            }
            double signature = target.Attributes.GetValueOrDefault(DogmaAttributeId.SignatureRadius);
            var damage = charge.Damage.Scale(AreaWeaponRules.SignatureFactor(signature, lance.Signature));
            Aggression(world, catalog, source, row.SourceShipId, flight.Module.TypeId, target, row.SystemId, now, now);
            if (target.Pilot is { } victim)
            {
                HitPilot(world, catalog, row, source, target, victim, flight.Module.TypeId, damage, now, notifications);
            }
            else if (target.Npc is { } npc)
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
        }
        // A late frame samples current occupants once. It cannot infer their earlier positions.
        long interval = AreaWeaponRules.Deadline(0, lance.DamageCycle);
        long next = damageStart + ((now - damageStart) / interval + 1) * interval;
        row.Save(flight with { ResolvesAt = Math.Min(next, damageEnd) });
        return destroyedNpc;
    }
}
