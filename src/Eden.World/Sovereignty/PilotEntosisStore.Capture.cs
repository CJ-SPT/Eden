using Eden.Data.World.Alliances;
using Eden.Data.World.Combat;
using Eden.Data.World.Fittings;
using Eden.Data.World.Inventory;
using Eden.Data.World.Sovereignty;
using Eden.Data.World.Structures;
using Eden.Gameplay.Sovereignty;
using Eden.Gameplay.Structures;
using Eden.World.Catalog;
using Eden.World.Runtime.Economy;

namespace Eden.World.Sovereignty;

internal sealed partial class PilotEntosisStore
{
    public void Advance(int systemId, long now)
    {
        if (!database.Catalog.Systems.TryGetValue(systemId, out var definition))
        {
            return;
        }
        if (
            !Live.Economy.Inspect(world =>
                world.Set<SavedSovereigntyHub>().Find(systemId) is not null
                || world.Filter<SavedSovereigntyActivityEvent>(row => row.SystemId == systemId).Any()
                || world.Filter<SavedSovereigntyConflict>(row => row.ConstellationId == definition.ConstellationId).Any()
            )
        )
        {
            return;
        }
        using var action = Live.Economy.Begin(new(systems: [systemId]));
        using CatalogDbContext catalog = new(database.CatalogPath);
        Advance(action, catalog, systemId, now);
        action.Commit();
    }

    private void Advance(WorldAction world, CatalogDbContext catalog, int system, long now)
    {
        AdvanceWarfare(world, catalog, system, now);
        var hub = world.Set<SavedSovereigntyHub>().Find(system);
        if (hub is null || now < hub.UpdatedAt)
        {
            return;
        }
        bool owned = world.Set<SavedSovereigntyClaim>().Find(system) is not null;
        var cycles = world.Filter<SavedEntosisCycle>(row => row.SystemId == system && row.HubId == hub.HubId).ToArray();
        if (owned)
        {
            Retire(world, cycles, now);
            return;
        }
        if (world.Set<SavedStructure>().Find(hub.HubId) is not { State: not StructureState.Destroyed } structure)
        {
            return;
        }
        foreach (var cycle in cycles)
        {
            if (
                cycle.Contributing
                && (!_scoringLeases.Contains(cycle.ModuleId) || !Valid(world, catalog, cycle, Target(world, system, structure.Id)!))
            )
            {
                cycle.Contributing = false;
                cycle.Stopping = true;
                world.AfterCommit(() => _scoringLeases.Remove(cycle.ModuleId));
            }
        }
        var contributions = cycles
            .Where(row => row.Contributing)
            .Select(row => new EntosisContribution(row.AllianceId, Math.Max(row.StartedAt, row.WarmupUntil), row.EndsAt))
            .ToArray();
        var progress = EntosisRules.Advance(
            new(hub.ControllingAllianceId, hub.ProgressTicks, hub.UpdatedAt),
            contributions,
            now,
            hub.CaptureDuration
        );
        if (progress.CompletedAt is long capturedAt)
        {
            var winner = cycles
                .Where(row =>
                    row.Contributing
                    && row.AllianceId == progress.ControllingAllianceId
                    && row.WarmupUntil <= capturedAt
                    && row.EndsAt >= capturedAt
                )
                .OrderBy(row => row.StartedAt)
                .ThenBy(row => row.ModuleId)
                .First();
            world.Add(
                new SavedSovereigntyClaim
                {
                    SystemId = system,
                    HubId = hub.HubId,
                    CorporationId = winner.CorporationId,
                    AllianceId = winner.AllianceId,
                    ClaimedAt = capturedAt,
                }
            );
            EnsureConflict(world, catalog, world.Set<SavedSovereigntyClaim>().Find(system)!, capturedAt);
            world.AfterCommit(() => Interlocked.Increment(ref _viewRevision));
            structure.CorporationId = winner.CorporationId;
            structure.StateStartedAt = capturedAt;
            // Owned-hub campaigns are introduced by the territorial-conflict milestone.
            structure.StateEndsAt = null;
            foreach (var cycle in cycles)
            {
                cycle.Stopping = true;
                world.AfterCommit(() => _scoringLeases.Remove(cycle.ModuleId));
            }
        }
        hub.ControllingAllianceId = progress.ControllingAllianceId;
        hub.ProgressTicks = progress.ProgressTicks;
        hub.UpdatedAt = progress.UpdatedAt;
        hub.ScoringAllianceId = progress.ScoringAllianceId;
        hub.Rate = progress.Rate;
        hub.Blocked = progress.Blocked;
        Retire(world, cycles, now);
    }

    private void Retire(WorldAction world, IReadOnlyList<SavedEntosisCycle> cycles, long now)
    {
        foreach (var cycle in cycles.Where(row => row.EndsAt <= now && (row.Stopping || row.Repeats == 0)))
        {
            world.Remove(cycle);
            world.AfterCommit(() => _scoringLeases.Remove(cycle.ModuleId));
        }
    }

    private bool Valid(WorldAction world, CatalogDbContext catalog, SavedEntosisCycle cycle, EntosisTarget target)
    {
        var pilot = world.Pilots.Find(cycle.PilotId);
        var item = world.Set<OwnedItem>().Find(cycle.ModuleId);
        if (
            pilot?.ActiveShipId != cycle.ShipId
            || pilot.CorporationId != cycle.CorporationId
            || pilot.ActiveShip.Position is not { } position
            || position.SystemId != cycle.SystemId
            || item is not { Online: true, Singleton: true }
            || item.TypeId != cycle.TypeId
            || item.LocationId != cycle.ShipId
            || item.OwnerId != cycle.PilotId
            || !locks.Contains(cycle.ShipId, cycle.HubId)
            || (world.Set<SavedAllianceMember>().Find(cycle.CorporationId)?.AllianceId ?? 0) != cycle.AllianceId
            || cycle.AllianceId > 0 && world.Set<SavedAlliance>().Find(cycle.AllianceId) is not { ClosedAt: null }
            || world.Set<ShipLoss>().Find(cycle.ShipId) is not null
        )
        {
            return false;
        }
        double edge =
            new Eden.Gameplay.Flight.SpacePosition(position.X, position.Y, position.Z).DistanceTo(target.Position)
            - (catalog.FittingType(pilot.ActiveShip.TypeId)?.Radius ?? 0)
            - (catalog.FittingType(target.TypeId)?.Radius ?? 0);
        return double.IsFinite(edge) && edge <= cycle.Range;
    }

    public void Stop(long pilotId, long moduleId, long now)
    {
        using var action = Live.Economy.Begin(new(pilots: [pilotId]));
        if (action.Set<SavedEntosisCycle>().Find(moduleId) is { } cycle && cycle.PilotId == pilotId)
        {
            using CatalogDbContext catalog = new(database.CatalogPath);
            Advance(action, catalog, cycle.SystemId, now);
            cycle.Stopping = true;
        }
        action.Commit();
    }

    public void Disconnect(long pilotId, long now)
    {
        using var action = Live.Economy.Begin(new(pilots: [pilotId]));
        using CatalogDbContext catalog = new(database.CatalogPath);
        var cycles = action.Filter<SavedEntosisCycle>(row => row.PilotId == pilotId).ToArray();
        foreach (int system in cycles.Select(row => row.SystemId).Distinct())
        {
            Advance(action, catalog, system, now);
        }
        foreach (var cycle in cycles)
        {
            cycle.Stopping = true;
            cycle.Contributing = false;
            action.AfterCommit(() => _scoringLeases.Remove(cycle.ModuleId));
            if (cycle.EndsAt <= now)
            {
                action.Remove(cycle);
            }
        }
        action.Commit();
    }
}
