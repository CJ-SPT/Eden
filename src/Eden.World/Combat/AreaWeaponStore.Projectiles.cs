using Eden.Data.World.Characters;
using Eden.Data.World.Combat;
using Eden.Gameplay.Combat;
using Eden.Gameplay.Dogma;
using Eden.Gameplay.Flight;
using Eden.World.Catalog;
using Eden.World.Runtime.Economy;
using Eden.World.Storage;

namespace Eden.World.Combat;

internal sealed partial class AreaWeaponStore
{
    internal static bool DamageProjectiles(
        WorldAction world,
        int system,
        DamageVector damage,
        long now,
        Func<SpacePosition, double, bool> near
    )
    {
        bool intercepted = false;
        foreach (var row in world.Filter<SavedAreaWeapon>(row => row.SystemId == system).ToArray())
        {
            var flight = row.Flight();
            if (flight.Bomb is not { } bomb || !near(flight.Position(now), 0))
            {
                continue;
            }
            var result = CombatDamage.Apply(flight.Health ?? bomb.Defense.Undamaged, bomb.Defense, damage, row.Id, now);
            if (result.Remaining.Destroyed)
            {
                world.Remove(row);
                intercepted = true;
            }
            else
            {
                row.Save(flight with { Health = result.Remaining });
            }
        }
        return intercepted;
    }

    private SavedAreaWeapon? SelectBomb(
        WorldAction world,
        CatalogDbContext catalog,
        Pilot source,
        SpacePosition origin,
        IReadOnlyDictionary<DogmaAttributeId, double> attributes,
        long now
    )
    {
        double speed = attributes.GetValueOrDefault(DogmaAttributeId.MaxVelocity);
        double lifetime = attributes.GetValueOrDefault(DogmaAttributeId.MissileFlightTime);
        var friendly = fleets.MembersInFleet(source.Id, world.Pilots.Select(pilot => pilot.Id).ToArray());
        var alliances = Live.OwnerDirectory.Snapshot.AllianceMemberships;
        int alliance = alliances.GetValueOrDefault(source.CorporationId);
        var assigned = world
            .Filter<SavedAreaWeapon>(row => row.SystemId == CrimewatchPersistence.System(catalog, source.ActiveShip))
            .Select(row => row.Flight())
            .Where(flight => flight.Module.Kind == AreaWeaponKind.Defender)
            .Select(flight => flight.TargetId)
            .ToHashSet();
        return world
            .Filter<SavedAreaWeapon>(row => row.SystemId == CrimewatchPersistence.System(catalog, source.ActiveShip))
            .Where(row => row.PilotId != source.Id && !assigned.Contains(row.Id))
            .AsEnumerable()
            .Select(row => new
            {
                Row = row,
                Flight = row.Flight(),
                Owner = world.Pilots.Find(row.PilotId),
            })
            .Where(candidate =>
                candidate.Flight.Bomb is not null
                && candidate.Owner?.CorporationId != source.CorporationId
                && !friendly.Contains(candidate.Row.PilotId)
                && (alliance == 0 || candidate.Owner is null || alliances.GetValueOrDefault(candidate.Owner.CorporationId) != alliance)
                && candidate.Flight.ResolvesAt > now
            )
            .Where(candidate => Threatens(candidate.Flight))
            .Where(candidate =>
                AreaWeaponRules.Intercept(origin, candidate.Flight, speed, now) is { } point
                && origin.DistanceTo(point) <= speed * lifetime / 1000
            )
            .OrderBy(candidate => origin.DistanceTo(candidate.Flight.Position(now)))
            .ThenBy(candidate => candidate.Flight.ResolvesAt)
            .ThenBy(candidate => candidate.Row.Id)
            .Select(candidate => candidate.Row)
            .FirstOrDefault();

        bool Threatens(AreaWeaponFlight flight)
        {
            var start = flight.Position(now);
            var segment = flight.Position(flight.ResolvesAt) - start;
            double lengthSquared = segment.Dot(segment);
            double fraction = lengthSquared > 0 ? Math.Clamp((origin - start).Dot(segment) / lengthSquared, 0, 1) : 0;
            return origin.DistanceTo(start + segment * fraction)
                <= flight.Bomb!.Radius + (catalog.RequireItem(source.ActiveShip.TypeId).Radius ?? 0);
        }
    }

    public IReadOnlyList<MissileContact> Projectiles(int systemId, long now) =>
        !Live.Economy.HasAreaWeapons(systemId)
            ? []
            : Live.Economy.Inspect(world =>
            {
                using CatalogDbContext catalog = new(database.CatalogPath);
                return world
                    .Filter<SavedAreaWeapon>(row => row.SystemId == systemId)
                    .AsEnumerable()
                    .Select(row => (Row: row, Flight: row.Flight()))
                    .Where(pair =>
                        pair.Flight.Module.Kind is AreaWeaponKind.Bomb or AreaWeaponKind.Defender && pair.Flight.ResolvesAt > now
                    )
                    .Select(pair =>
                    {
                        int type = pair.Flight.Bomb?.TypeId ?? catalog.RequireItem(DefenderMissileType).Id;
                        var attributes = catalog.FittingAttributes(type);
                        var item = catalog.RequireItem(type);
                        var velocity = pair.Flight.Velocity;
                        MissileDrive drive = new(
                            item.Mass ?? 1,
                            (float)(item.Radius ?? 1),
                            (float)velocity.Length,
                            (float)Math.Max(attributes.GetValueOrDefault(DogmaAttributeId.Agility), MinimumProjectileAgility),
                            (pair.Flight.ResolvesAt - pair.Flight.StartedAt) / (double)TimeSpan.TicksPerMillisecond
                        );
                        return new MissileContact(
                            pair.Row.Id,
                            type,
                            item.GroupId,
                            systemId,
                            pair.Row.PilotId,
                            pair.Row.SourceShipId,
                            [pair.Row.ModuleId],
                            pair.Flight.TargetId,
                            0,
                            world.Pilots.Find(pair.Row.PilotId)?.CorporationId ?? 0,
                            0,
                            new(
                                drive,
                                pair.Flight.Position(now),
                                velocity,
                                pair.Flight.Position(pair.Flight.ResolvesAt),
                                (now - pair.Flight.StartedAt) / (double)TimeSpan.TicksPerSecond,
                                Orphaned: true
                            )
                        );
                    })
                    .ToArray();
            });

    // The pinned catalog has one modern ship defender charge. Its zero dogma
    // agility needs a positive wire inertia; authoritative interception is linear.
    private const int DefenderMissileType = 32782;
    private const double MinimumProjectileAgility = 0.000001;
}
