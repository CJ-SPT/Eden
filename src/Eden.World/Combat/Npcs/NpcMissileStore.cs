using System.Text.Json;
using Eden.Data.Catalog;
using Eden.Data.World.Characters;
using Eden.Data.World.Combat;
using Eden.Data.World.Combat.Npcs;
using Eden.Gameplay.Combat;
using Eden.Gameplay.Docking;
using Eden.Gameplay.Dogma;
using Eden.Gameplay.Flight;
using Eden.Infrastructure.Registration;
using Eden.World.Catalog;
using Eden.World.Characters;
using Eden.World.Combat.Weapons;
using Eden.World.Docking;
using Eden.World.Fittings;
using Eden.World.Runtime;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;

namespace Eden.World.Combat.Npcs;

[Injectable]
internal sealed class NpcMissileStore(WorldDatabase database, SqliteWorld lookup, IShipLootRoll lootRoll) : INpcMissiles
{
    private const int HistorySeconds = 120;

    internal static bool Launch(
        WorldDbContext world,
        CatalogDbContext catalog,
        WorldNpc npc,
        NpcAggression aggression,
        IReadOnlyDictionary<DogmaAttributeId, double> attributes,
        Pilot pilot,
        SpacePosition velocity,
        long now,
        int stamp
    )
    {
        int chargeId = checked((int)attributes.GetValueOrDefault(DogmaAttributeId.EntityMissileType));
        if (
            NpcPopulationRules.IsCivilian(npc.Role)
            || npc.Navigation == NpcNavigationState.Warping
            || chargeId == 0
            || now < aggression.MissileReadyAt
        )
        {
            return false;
        }
        double cycle = attributes.GetValueOrDefault(DogmaAttributeId.EntityMissileCycle);
        if (!double.IsFinite(cycle) || cycle <= 0)
        {
            throw new InvalidDataException("Invalid NPC missile launch duration.");
        }
        var charge = PilotFittingStore.ReadAttributes(catalog, chargeId);
        var drive = NpcMissileRules.Drive(MissileChargeCatalog.Drive(catalog, chargeId, charge), attributes);
        var warhead = NpcMissileRules.Warhead(charge, attributes);
        var position = pilot.ActiveShip.Position!;
        SpacePosition target = new(position.X, position.Y, position.Z);
        if (target.DistanceTo(NpcMotion.Position(npc)) > drive.NominalRange)
        {
            return false;
        }
        var source = catalog.FittingType(npc.TypeId)!;
        var hull = catalog.FittingType(pilot.ActiveShip.TypeId)!;
        var flight = MissileFlightRules.Launch(
            drive,
            new(
                NpcMotion.Position(npc),
                NpcMotion.Velocity(npc),
                (float)attributes.GetValueOrDefault(DogmaAttributeId.MaxVelocity),
                new(0, 0, -1)
            ),
            new(target, velocity, (float)(hull.Radius ?? 0)),
            charge.GetValueOrDefault(DogmaAttributeId.AimedMissileLaunch) != 0
        );
        long boundary = now - now % TimeSpan.TicksPerSecond;
        CrimewatchPersistence.Npc(world, pilot.Id, npc.SystemId, checked(now + (long)Math.Ceiling(cycle * TimeSpan.TicksPerMillisecond)));
        world.Add(
            new WorldMissile
            {
                SourceKind = MissileSourceKind.Npc,
                TargetKind = MissileTargetKind.Pilot,
                SourceTypeId = source.Id,
                PilotId = pilot.Id,
                SourceShipId = npc.Id,
                // NPC launchers have no player inventory module; the source ship is
                // the stable native launch identity, retained in the wire contact.
                ModuleId = npc.Id,
                ChargeTypeId = chargeId,
                GroupId = catalog.FittingType(chargeId)!.GroupId,
                SystemId = npc.SystemId,
                TargetId = pilot.ActiveShipId,
                TargetRadius = (float)(hull.Radius ?? 0),
                CorporationId = npc.CorporationId,
                LaunchStamp = stamp,
                LaunchedAt = boundary,
                UpdatedAt = boundary,
                ExpiresAt = checked(boundary + (long)Math.Ceiling(drive.LifetimeMilliseconds * TimeSpan.TicksPerMillisecond)),
                Snapshot = JsonSerializer.Serialize(new MissilePayload(flight, warhead), MissileJson.Default.MissilePayload),
            }
        );
        aggression.MissileReadyAt = checked(now + (long)Math.Ceiling(cycle * TimeSpan.TicksPerMillisecond));
        Eden.World.Drones.DroneCombatPersistence.Incoming(world, pilot.ActiveShipId, npc.Id, now);
        return true;
    }

    public IReadOnlyList<NpcShot> Advance(long pilotId, long shipId, SpacePosition velocity, long now)
    {
        if (!double.IsFinite(velocity.Length))
        {
            throw new InvalidDataException("Invalid NPC missile target velocity.");
        }
        using var phase = Eden.Infrastructure.Diagnostics.SessionPerformance.Phase("read.npcMissileWork");
        if (LiveReadModels.Find(database.Path) is { } resident && !resident.HasNpcMissileWork(pilotId, now, HistorySeconds))
        {
            return [];
        }
        using WorldDbContext world = new(database.Path);
        using CatalogDbContext catalog = new(database.CatalogPath);
        using var transaction = world.Database.BeginTransaction();
        long oldest = now - HistorySeconds * TimeSpan.TicksPerSecond;
        world.Set<WorldMissile>().Where(m => m.SourceKind == MissileSourceKind.Npc && m.EndedAt < oldest).ExecuteDelete();
        var pilot = world.Pilots.Include(p => p.ActiveShip).ThenInclude(s => s.Position).Single(p => p.Id == pilotId);
        int targetSystem = pilot.ActiveShip.Position?.SystemId ?? catalog.RequireStation(pilot.ActiveShip.StationId).SolarSystemId;
        var rows = world
            .Set<WorldMissile>()
            .Where(m => m.SourceKind == MissileSourceKind.Npc && m.PilotId == pilotId && m.Status == MissileFlightStatus.Flying)
            .OrderBy(m => m.UpdatedAt)
            .ThenBy(m => m.Id)
            .ToArray();
        List<NpcShot> shots = [];
        foreach (var row in rows)
        {
            var npc = world.Set<WorldNpc>().Find(row.SourceShipId);
            var position = pilot.ActiveShip.Position;
            var aggression = world.Set<NpcAggression>().Find(row.SourceShipId);
            if (
                pilot.ActiveShipId != shipId
                || row.TargetId != shipId
                || position is null
                || targetSystem != row.SystemId
                || npc is null
                || npc.SystemId != row.SystemId
                || npc.DestroyedAt.HasValue
                || npc.RetiredAt.HasValue
                || aggression?.TargetShipId != shipId
                || now > row.ExpiresAt + TimeSpan.TicksPerSecond
                || catalog.FittingType(pilot.ActiveShip.TypeId)!.GroupId == FleetDefinitions.CapsuleGroup
            )
            {
                row.Status = MissileFlightStatus.Expired;
                row.EndedAt = now;
            }
        }
        while (true)
        {
            var due = rows.Where(m =>
                    m.Status == MissileFlightStatus.Flying
                    && now - m.UpdatedAt >= Math.Min(TimeSpan.TicksPerSecond, m.ExpiresAt - m.UpdatedAt)
                )
                .OrderBy(m => m.UpdatedAt)
                .ThenBy(m => m.Id)
                .ToArray();
            if (due.Length == 0)
            {
                break;
            }
            long boundary = due[0].UpdatedAt;
            List<PendingStep> steps = [];
            foreach (var row in due.Where(m => m.UpdatedAt == boundary))
            {
                var position = pilot.ActiveShip.Position!;
                double offset = (row.UpdatedAt - now) / (double)TimeSpan.TicksPerSecond;
                var target = new MissileTarget(
                    new SpacePosition(position.X, position.Y, position.Z) + velocity * offset,
                    velocity,
                    row.TargetRadius
                );
                var payload = row.Payload();
                var next = MissileFlightRules.Tick(payload.Flight, target);
                long time = checked(row.LaunchedAt + (long)Math.Round(next.ElapsedSeconds * TimeSpan.TicksPerSecond));
                steps.Add(new(row, payload with { Flight = next }, time));
            }
            // Contact time owns damage order, even when a newer, faster missile
            // reaches the target ahead of an older flight in the same tick.
            foreach (var step in steps.OrderBy(s => s.Time).ThenBy(s => s.Row.Id))
            {
                var (row, payload, time) = step;
                if (pilot.ActiveShipId != shipId)
                {
                    row.Status = MissileFlightStatus.Expired;
                    row.EndedAt = time;
                    continue;
                }
                row.Save(payload);
                row.UpdatedAt = time;
                row.Status = payload.Flight.Status;
                if (row.Status != MissileFlightStatus.Flying)
                {
                    row.EndedAt = time;
                }
                if (row.Status == MissileFlightStatus.Impact)
                {
                    var npc = world.Set<WorldNpc>().Find(row.SourceShipId)!;
                    var position = pilot.ActiveShip.Position!;
                    var attributes = PilotFittingStore.ShipAttributes(world, catalog, pilotId);
                    var defense = ShipHealth.DefenseFrom(attributes);
                    double application = MissileRules.Application(
                        payload.Warhead,
                        attributes.GetValueOrDefault(DogmaAttributeId.SignatureRadius),
                        velocity.Length
                    );
                    var damage = payload.Warhead.Damage.Scale(application);
                    var result = PilotHealthStore.Apply(world, shipId, defense, damage, row.UpdatedAt);
                    PilotInteractionHistoryStore.Damage(
                        world,
                        shipId,
                        PilotInteractionHistoryStore.Npc(npc, row.ChargeTypeId),
                        result.Applied
                    );
                    var insurance = result.Remaining.Destroyed
                        ? ShipLossPersistence.Apply(world, catalog, pilot, npc.Id, shipId, row.SystemId, position, row.UpdatedAt, lootRoll)
                        : null;
                    shots.Add(
                        new(
                            npc.Id,
                            row.SourceTypeId,
                            new(shipId, row.ChargeTypeId, row.UpdatedAt, 0),
                            damage,
                            result.Applied,
                            1,
                            new(shipId, defense, result.Remaining),
                            null,
                            insurance,
                            IsMissile: true
                        )
                    );
                    row.Reported = true;
                    world.SaveChanges();
                }
            }
        }
        world.SaveChanges();
        transaction.Commit();
        if (shots.Count > 0 && shots[^1].Health.Remaining.Destroyed)
        {
            shots[^1] = shots[^1] with { Capsule = lookup.FindPilot(pilot.AccountId, pilotId) };
        }
        return shots;
    }

    private sealed record PendingStep(WorldMissile Row, MissilePayload Payload, long Time);
}
