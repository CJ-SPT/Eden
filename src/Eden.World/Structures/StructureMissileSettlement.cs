using System.Text.Json;
using Eden.Data.World.Combat;
using Eden.Gameplay.Combat;
using Eden.Gameplay.Dogma;
using Eden.Gameplay.Flight;
using Eden.World.Catalog;
using Eden.World.Combat;
using Eden.World.Runtime.Economy;

namespace Eden.World.Structures;

// Earlier physical contacts must settle before a structure read, repair deadline or a later paid attack.
internal static class StructureMissileSettlement
{
    internal static void Advance(WorldAction world, CatalogDbContext catalog, int system, long now)
    {
        if (!world.HasStructureMissiles(system) || !world.BeginStructureMissileAdvance(system, now))
        {
            return;
        }
        var rows = world
            .Filter<WorldMissile>(m =>
                m.SystemId == system
                && m.SourceKind == MissileSourceKind.Pilot
                && m.TargetKind == MissileTargetKind.Structure
                && m.Status == MissileFlightStatus.Flying
            )
            .ToArray();
        foreach (var row in rows)
        {
            var source = world.Pilots.Find(row.PilotId);
            if (
                source?.ActiveShipId != row.SourceShipId
                || source.ActiveShip.Position is not { } position
                || (position.SystemId ?? catalog.RequireStation(source.ActiveShip.StationId).SolarSystemId) != system
                || world.Set<ShipLoss>().Any(l => l.ShipId == row.SourceShipId)
                || now > row.ExpiresAt + TimeSpan.TicksPerSecond
            )
            {
                row.Status = MissileFlightStatus.Expired;
                row.EndedAt = now;
            }
        }
        MissilePayload Step(WorldMissile row)
        {
            var structure = StructureCombatTargets.Find(world, row.TargetId);
            MissileTarget? target =
                structure is not null
                && structure.SystemId == system
                && StructureCombatTargets.Available(world, catalog, structure, row.UpdatedAt)
                    ? new(StructureCombatTargets.Position(structure), SpacePosition.Zero, row.TargetRadius)
                    : null;
            return MissileMotionHistory.Step(world, row, row.Payload(), target, now);
        }
        foreach (var (row, payload, time) in MissileMotionHistory.DueSteps(rows, now, Step, contactsBeforeBoundary: true))
        {
            row.Save(payload);
            row.UpdatedAt = time;
            row.Status = payload.Flight.Status;
            if (row.Status != MissileFlightStatus.Flying)
            {
                row.EndedAt = time;
            }
            if (
                row.Status != MissileFlightStatus.Impact
                || StructureCombatTargets.Find(world, row.TargetId) is not { } structure
                || !StructureCombatTargets.Available(world, catalog, structure, time)
            )
            {
                continue;
            }
            var attributes = catalog.FittingAttributes(structure.TypeId);
            double application = MissileRules.Application(
                payload.Warhead,
                attributes.GetValueOrDefault(DogmaAttributeId.SignatureRadius),
                0
            );
            var damage = payload.Warhead.Damage.Scale(application);
            var result = StructureCombatTargets.Apply(world, catalog, structure, damage, time);
            MissileImpact outcome = new(
                row.Id,
                row.PilotId,
                row.SourceShipId,
                row.TargetId,
                row.LauncherTypeId,
                row.ChargeTypeId,
                result.Health,
                damage,
                result.Applied,
                result.Destroyed,
                null
            );
            row.Outcome = JsonSerializer.Serialize(outcome, MissileJson.Default.MissileImpact);
            row.Reported = false;
        }
    }
}
