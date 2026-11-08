using System.Text.Json;
using Eden.Data.Catalog;
using Eden.Data.World.Combat;
using Eden.Data.World.Combat.Npcs;
using Eden.Gameplay.Combat;
using Eden.Gameplay.Docking;
using Eden.Gameplay.Flight;
using Eden.Infrastructure.Registration;
using Eden.World.Catalog;
using Eden.World.Fittings;
using Eden.World.Runtime;
using Eden.World.Runtime.Combat;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;

namespace Eden.World.Combat.Npcs;

[Injectable(InjectionType.Singleton)]
internal sealed class NpcEwarRoll : INpcEwarRoll
{
    private const int Resolution = 1_000_000;

    public double Next() => System.Security.Cryptography.RandomNumberGenerator.GetInt32(Resolution) / (double)Resolution;
}

[Injectable]
internal sealed class NpcEwarStore(WorldDatabase database, INpcEwarRoll rolls, NpcProfileCatalog profiles) : INpcElectronicWarfare
{
    public IReadOnlyList<NpcEwarContact> Incoming(long shipId) =>
        LiveReadModels.Find(database.Path)?.NpcEwarIncoming(shipId) ?? ReadIncoming(shipId);

    private IReadOnlyList<NpcEwarContact> ReadIncoming(long shipId)
    {
        using WorldDbContext world = new(database.Path);
        return Active(world, shipId);
    }

    internal static IReadOnlyList<NpcEwarContact> Active(WorldDbContext world, long shipId) =>
        world
            .Set<WorldNpcEwar>()
            .Where(e => e.TargetShipId == shipId && e.AppliedJson != null)
            .AsNoTracking()
            .AsEnumerable()
            .Select(e => e.Contact()!)
            .ToArray();

    public void Cancel(long shipId)
    {
        if (LiveReadModels.Find(database.Path) is { } reads && !reads.HasNpcEwar(shipId))
        {
            return;
        }
        using WorldDbContext world = new(database.Path);
        world.Set<WorldNpcEwar>().Where(e => e.TargetShipId == shipId).ExecuteDelete();
    }

    public IReadOnlyList<NpcEwarContact> Advance(long pilotId, long shipId, long now)
    {
        // Take the resident owner before the shared movement gate. Bot execution
        // already owns the world when it requests these same views.
        using var ownership = LiveWorld.Find(database.Path)?.Enter();
        lock (database.MotionLock)
        {
            var sources = LiveCombatRuntime.Find(database.Path)?.EwarSources(shipId, now);
            if (sources is { Count: 0 } && LiveReadModels.Find(database.Path) is { } reads && !reads.HasNpcEwar(shipId))
            {
                return [];
            }
            return AdvanceSql(pilotId, shipId, now, sources);
        }
    }

    private IReadOnlyList<NpcEwarContact> AdvanceSql(long pilotId, long shipId, long now, IReadOnlyList<NpcEwarSource>? sources)
    {
        using WorldDbContext world = new(database.Path);
        using var transaction = world.Database.BeginTransaction();
        using CatalogDbContext catalog = new(database.CatalogPath);
        var saved = world.Set<WorldNpcEwar>().Where(e => e.TargetShipId == shipId).ToArray();
        var pilot = world.Pilots.Include(p => p.ActiveShip).ThenInclude(s => s.Position).Single(p => p.Id == pilotId);
        var type = catalog.RequireItem(pilot.ActiveShip.TypeId);
        List<WorldNpcEwar> current = [];
        if (
            pilot.ActiveShipId == shipId
            && pilot.ActiveShip.Position is { } position
            && type.GroupId != FleetDefinitions.CapsuleGroup
            && !world.Set<ShipLoss>().Any(l => l.ShipId == shipId)
        )
        {
            int system = position.SystemId ?? catalog.RequireStation(pilot.ActiveShip.StationId).SolarSystemId;
            var target = new SpacePosition(position.X, position.Y, position.Z);
            var attributes = PilotFittingStore.ShipAttributes(world, catalog, pilotId);
            sources ??= Sources(world, catalog, shipId);
            foreach (var source in sources.OrderBy(s => s.Id))
            {
                if (
                    source.SystemId != system
                    || target.DistanceTo(source.Home) > source.Leash
                    || source.Position.DistanceTo(source.Home) > source.Leash
                )
                {
                    continue;
                }
                var playerEffects = PlayerEwarStore.Active(world, source.Id);
                if (!PlayerEwarRules.CanTarget(playerEffects, shipId))
                {
                    continue;
                }
                if (playerEffects.Any(e => e.Module.Kind == NpcEwarKind.SensorDampening && e.Applied is not null))
                {
                    var sourceAttributes = PlayerEwarRules.NpcAttributes(
                        PilotFittingStore.ReadAttributes(catalog, source.TypeId),
                        playerEffects
                    );
                    double distance = Math.Max(0, target.DistanceTo(source.Position) - (type.Radius ?? 0) - source.Radius);
                    if (distance > sourceAttributes.GetValueOrDefault(Eden.Gameplay.Dogma.DogmaAttributeId.MaxTargetRange))
                    {
                        continue;
                    }
                }
                foreach (var ability in source.Abilities)
                {
                    var row =
                        saved.SingleOrDefault(e => e.NpcId == source.Id && e.Kind == ability.Kind)
                        ?? world.Set<WorldNpcEwar>().Find(source.Id, ability.Kind);
                    if (row is not null && row.TargetShipId != shipId)
                    {
                        row.TargetShipId = shipId;
                        row.ReadyAt = 0;
                        row.AppliedJson = null;
                    }
                    if (row is null && now < source.ReactionAt)
                    {
                        continue;
                    }
                    if (row is null)
                    {
                        row = new()
                        {
                            NpcId = source.Id,
                            Kind = ability.Kind,
                            TypeId = source.TypeId,
                            TargetShipId = shipId,
                        };
                        world.Add(row);
                    }
                    current.Add(row);
                    if (now < row.ReadyAt)
                    {
                        continue;
                    }
                    double distance = Math.Max(0, target.DistanceTo(source.Position) - (type.Radius ?? 0) - source.Radius);
                    var applied = NpcEwarRules.Attempt(ability, distance, attributes, rolls.Next());
                    if (distance <= NpcEncounterDefinitions.VisibilityRange && NpcEwarRules.RangeFactor(ability, distance) > 0)
                    {
                        Eden.World.Drones.DroneCombatPersistence.Incoming(world, shipId, source.Id, now);
                    }
                    if (applied?.CapacitorDrain is > 0)
                    {
                        double charge = ShipResources.Capacitor(world, shipId, attributes, now);
                        double drain = Math.Min(charge, applied.CapacitorDrain);
                        ShipResources.SpendCapacitor(world, shipId, attributes, drain, now);
                        applied = applied with { CapacitorDrain = drain };
                    }
                    row.AbilityJson = JsonSerializer.Serialize(ability);
                    row.AppliedJson = applied is null ? null : JsonSerializer.Serialize(applied);
                    row.StartedAt = now;
                    row.ReadyAt = checked(now + (long)Math.Ceiling(ability.DurationMilliseconds * TimeSpan.TicksPerMillisecond));
                    if (applied is not null)
                    {
                        CrimewatchPersistence.Npc(world, pilotId, system, row.ReadyAt);
                    }
                }
            }
        }
        world.RemoveRange(saved.Except(current));
        world.SaveChanges();
        transaction.Commit();
        return current.Select(e => e.Contact()).OfType<NpcEwarContact>().ToArray();
    }

    private IReadOnlyList<NpcEwarSource> Sources(WorldDbContext world, CatalogDbContext catalog, long shipId)
    {
        var aggression = world.Set<NpcAggression>().Where(a => a.TargetShipId == shipId).ToDictionary(a => a.NpcId);
        var ids = aggression.Keys.ToArray();
        var npcs = world
            .Set<WorldNpc>()
            .Where(n =>
                ids.Contains(n.Id)
                && n.DestroyedAt == null
                && n.RetiredAt == null
                && n.Navigation != NpcNavigationState.Warping
                && n.Role != NpcRole.Convoy
                && n.Role != NpcRole.MiningMiner
                && n.Role != NpcRole.MiningHauler
            )
            .ToArray();
        return npcs.Select(n =>
            {
                var profile = profiles.Read(catalog, n.TypeId);
                return new NpcEwarSource(
                    n.Id,
                    n.TypeId,
                    n.SystemId,
                    NpcMotion.Position(n),
                    NpcMotion.Home(n, catalog),
                    profile.Type.Radius ?? 0,
                    NpcPopulationRules.Leash(profile.Attributes, acquisitionRange: n.AcquisitionRange),
                    aggression[n.Id].ReadyAt,
                    profile.ElectronicWarfare
                );
            })
            .ToArray();
    }
}
