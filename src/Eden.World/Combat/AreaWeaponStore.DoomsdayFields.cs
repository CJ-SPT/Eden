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
    public IReadOnlyList<DoomsdayFieldContact> DoomsdayFields(int systemId, long now)
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
                    && pair.Flight.Module.Field is { } field
                    && now < AreaWeaponRules.Deadline(pair.Flight.StartedAt, pair.Flight.Module.Delay + field.DamageDuration)
                )
                .Select(pair =>
                {
                    var (row, flight) = pair;
                    var field = flight.Module.Field!;
                    long active = AreaWeaponRules.Deadline(flight.StartedAt, flight.Module.Delay);
                    long end = AreaWeaponRules.Deadline(active, field.DamageDuration);
                    StaticSpaceObject? beacon = null;
                    if (field.Shape == DoomsdayFieldShape.Cylinder)
                    {
                        var type = catalog.RequireItem(DoomsdayFieldRules.BeaconType);
                        beacon = new StaticSpaceObject(
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
                    }
                    var first = beacon is null ? flight.Point - flight.Origin : null;
                    var second = flight.EndPoint is { } point ? point - flight.Origin : first;
                    long visualAt = field.Shape == DoomsdayFieldShape.Sweep ? active : flight.StartedAt;
                    long visualEnd = field.Shape == DoomsdayFieldShape.Cone ? active : end;
                    return new DoomsdayFieldContact(
                        row.Id,
                        beacon,
                        flight.Origin,
                        row.SourceShipId,
                        row.ModuleId,
                        flight.Module.TypeId,
                        visualAt,
                        visualEnd,
                        first,
                        second
                    );
                })
                .ToArray()
        );
    }

    private void NeutralizeNearDoomsday(
        WorldAction world,
        CatalogDbContext catalog,
        Pilot source,
        SpacePosition center,
        int system,
        int module,
        DoomsdayFieldProfile field,
        long now
    )
    {
        foreach (
            var victim in Targets(
                world,
                catalog,
                system,
                now,
                (position, radius) => position.DistanceTo(center) <= field.NeutralizationRadius + radius
            )
        )
        {
            if (victim.Pilot is null || victim.Id == source.ActiveShipId)
            {
                continue;
            }
            double signature = victim.Attributes.GetValueOrDefault(DogmaAttributeId.SignatureRadius);
            double amount = field.NeutralizationAmount * AreaWeaponRules.SignatureFactor(signature, field.NeutralizationSignature);
            Aggression(world, catalog, source, source.ActiveShipId, module, victim, system, now, now);
            Drain(world, victim, amount, now);
        }
    }

    private bool SettleDoomsdayField(
        WorldAction world,
        CatalogDbContext catalog,
        SavedAreaWeapon row,
        AreaWeaponFlight flight,
        long now,
        List<PilotDamage> notifications
    )
    {
        var charge = flight.Module.Doomsday!;
        var field = flight.Module.Field!;
        long damageStart = AreaWeaponRules.Deadline(flight.StartedAt, flight.Module.Delay);
        long damageEnd = AreaWeaponRules.Deadline(damageStart, field.DamageDuration);
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

        var offset = flight.Point - flight.Origin;
        var center = flight.Origin;
        double length = flight.Module.Range;
        if (flight.EndPoint is { } second)
        {
            double progress = (now - damageStart) / (double)(damageEnd - damageStart);
            offset += (second - flight.Point) * progress;
            var position = source.ActiveShip.Position!;
            center = presence.FindArea(row.SystemId, row.SourceShipId)?.Position ?? new(position.X, position.Y, position.Z);
            length = offset.Length - flight.SourceRadius;
        }
        var axis = offset.Unit;
        var origin = center + axis * flight.SourceRadius;
        bool destroyedNpc = false;
        foreach (
            var target in Targets(
                world,
                catalog,
                row.SystemId,
                now,
                (position, radius) =>
                    field.Shape == DoomsdayFieldShape.Cone
                        ? DoomsdayFieldRules.IntersectsCone(origin, axis, length, position, radius)
                        : DoomsdayFieldRules.Intersects(origin, axis, length, field.Radius, position, radius)
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
            var damage = charge.Damage.Scale(AreaWeaponRules.SignatureFactor(signature, field.Signature));
            Aggression(world, catalog, source, row.SourceShipId, flight.Module.TypeId, target, row.SystemId, now, now);
            if (target.Pilot is { } victim)
            {
                HitPilot(world, catalog, row, source, target, victim, flight.Module.TypeId, damage, now, notifications);
                if (field.DisruptionDuration > 0 && world.Set<ShipLoss>().Find(target.Id) is null)
                {
                    long endsAt = AreaWeaponRules.Deadline(now, field.DisruptionDuration);
                    var disruption =
                        world.Set<SavedLanceDisruption>().Find(target.Id)
                        ?? world.Add(new SavedLanceDisruption { ShipId = target.Id }).Entity;
                    disruption.EndsAt = Math.Max(disruption.EndsAt, endsAt);
                }
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
        long interval = AreaWeaponRules.Deadline(0, field.DamageCycle);
        long next = damageStart + ((now - damageStart) / interval + 1) * interval;
        row.Save(flight with { ResolvesAt = Math.Min(next, damageEnd) });
        return destroyedNpc;
    }
}
