using Eden.Data.World.Characters;
using Eden.Data.World.Combat;
using Eden.Gameplay.Combat;
using Eden.Gameplay.Docking;
using Eden.Infrastructure.Registration;
using Eden.World.Catalog;
using Eden.World.Characters;
using Eden.World.Docking;
using Eden.World.Runtime;
using Eden.World.Runtime.Combat;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;

namespace Eden.World.Combat;

[Injectable(InjectionType.Singleton)]
internal sealed class PilotCrimewatchStore(WorldDatabase database, TimeProvider clock) : IPilotCrimewatch
{
    private long Now => clock.GetUtcNow().UtcDateTime.ToFileTimeUtc();

    internal static void Reconcile(WorldDbContext world, CatalogDbContext catalog, long now)
    {
        // A crash cannot turn a saved combat timer into immediate disappearance.
        // Keep original deadlines: repeated restarts must never renew aggression.
        using var transaction = world.Database.BeginTransaction();
        var timers = world.Set<SavedCrimewatch>().AsNoTracking().Where(s => s.PvpUntil > now || s.NpcUntil > now).ToArray();
        foreach (var state in timers)
        {
            var pilot = world.Pilots.Include(p => p.ActiveShip).ThenInclude(s => s.Position).SingleOrDefault(p => p.Id == state.PilotId);
            if (
                pilot?.ActiveShip.Position is null
                || world.Set<SavedLogoff>().Any(s => s.PilotId == state.PilotId)
                || world.Set<ShipLoss>().Any(s => s.ShipId == pilot.ActiveShipId)
            )
            {
                continue;
            }
            world.Add(
                new SavedLogoff
                {
                    PilotId = pilot.Id,
                    ShipId = pilot.ActiveShipId,
                    SystemId = CrimewatchPersistence.System(catalog, pilot.ActiveShip),
                    ExpiresAt = Math.Max(state.PvpUntil, state.NpcUntil),
                }
            );
        }
        world.SaveChanges();
        transaction.Commit();
    }

    public CrimewatchSnapshot Read(long pilotId, int systemId)
    {
        var state = LiveReadModels.Find(database.Path) is { } resident
            ? resident.Crimewatch(pilotId, systemId, () => ReadSql(pilotId, systemId))
            : ReadSql(pilotId, systemId);
        if (LiveCombatRuntime.Find(database.Path)?.NpcTimer(pilotId) is { } npc)
        {
            state = state with
            {
                Timers = state.Timers with
                {
                    Npc = new CombatTimer(
                        Math.Max(state.Timers.Npc.ActiveUntil, npc.ActiveUntil),
                        Math.Max(state.Timers.Npc.ExpiresAt, npc.ExpiresAt)
                    ),
                },
            };
        }
        return LiveCombatRuntime.Find(database.Path)?.DroneCrimewatch(pilotId, systemId, state) ?? state;
    }

    private CrimewatchSnapshot ReadSql(long pilotId, int systemId)
    {
        // This typed snapshot merges the current resident NPC timer in Read().
        // Direct SQL crimewatch callers still require normal projection.
        using WorldDbContext world = new(database.Path) { ReadsResidentNpcTimer = true };
        // The resident owner serializes this complete read snapshot. File-backed
        // worlds retain their SQL transaction; mutations use the normal bridge.
        using var ownership = LiveWorld.Find(database.Path)?.Enter();
        using var transaction = ownership is null ? world.Database.BeginTransaction() : null;
        var pilot = world.Pilots.Single(p => p.Id == pilotId);
        var timers = world.Set<SavedCrimewatch>().Find(pilotId)?.Timers() ?? CrimewatchTimers.Empty;
        var engagements = world
            .Set<SavedEngagement>()
            .AsNoTracking()
            .Where(e => e.LowerPilotId == pilotId || e.HigherPilotId == pilotId)
            .OrderBy(e => e.LowerPilotId)
            .ThenBy(e => e.HigherPilotId)
            .AsEnumerable()
            .Select(e => new LimitedEngagement(e.LowerPilotId == pilotId ? e.HigherPilotId : e.LowerPilotId, e.ActiveUntil, e.ExpiresAt))
            .ToArray();
        var flags = world
            .Set<SavedCrimewatch>()
            .AsNoTracking()
            .Where(s => s.SystemId == systemId && (s.CriminalUntil > 0 || s.SuspectUntil > 0))
            .OrderBy(s => s.PilotId)
            .AsEnumerable()
            .Select(s => new FlaggedPilot(s.PilotId, s.Timers()))
            .ToArray();
        var safety = pilot.SafetyLevel;
        using CatalogDbContext catalog = new(database.CatalogPath);
        if (
            world.Set<Account>().Find(pilot.AccountId)!.CloneGrade == AccountCloneGrade.Alpha
            && CrimewatchPersistence.Security(catalog, systemId, false) >= CrimewatchRules.HighSecurity
        )
        {
            safety = ShipSafetyLevel.Full;
        }
        return new(safety, timers, engagements, flags);
    }

    public void CheckTravel(long pilotId, CrimewatchTravel action, int? destinationSystemId = null)
    {
        using WorldDbContext world = new(database.Path);
        using var transaction = world.Database.BeginTransaction();
        using CatalogDbContext catalog = new(database.CatalogPath);
        var pilot = world.Pilots.Include(p => p.ActiveShip).ThenInclude(s => s.Position).Single(p => p.Id == pilotId);
        CrimewatchPersistence.CheckTravel(world, catalog, pilot, action, Now, destinationSystemId);
    }

    public void Connect(long pilotId)
    {
        using WorldDbContext world = new(database.Path);
        world.Set<SavedLogoff>().Where(s => s.PilotId == pilotId).ExecuteDelete();
    }

    public void Disconnect(long pilotId, long shipId)
    {
        using WorldDbContext world = new(database.Path);
        using var transaction = world.Database.BeginTransaction();
        using CatalogDbContext catalog = new(database.CatalogPath);
        var pilot = world.Pilots.Include(p => p.ActiveShip).ThenInclude(s => s.Position).Single(p => p.Id == pilotId);
        if (pilot.ActiveShipId != shipId || pilot.ActiveShip.Position is null || world.Set<ShipLoss>().Any(s => s.ShipId == shipId))
        {
            return;
        }
        var timers = world.Set<SavedCrimewatch>().Find(pilotId)?.Timers() ?? CrimewatchTimers.Empty;
        var row = world.Set<SavedLogoff>().Find(pilotId) ?? world.Add(new SavedLogoff { PilotId = pilotId }).Entity;
        row.ShipId = shipId;
        row.SystemId = CrimewatchPersistence.System(catalog, pilot.ActiveShip);
        row.ExpiresAt = Math.Max(row.ExpiresAt, CrimewatchRules.LogoffDeadline(timers, Now));
        world.SaveChanges();
        transaction.Commit();
    }

    public IReadOnlyList<LoggedOffPilot> LoggedOff(int systemId)
    {
        var saved = LiveReadModels.Find(database.Path) is { } resident
            ? resident.LoggedOff(systemId, () => LoggedOffSql(systemId))
            : LoggedOffSql(systemId);
        var combat = LiveCombatRuntime.Find(database.Path);
        return saved
            .Select(s => s with { ExpiresAt = combat?.DroneLogoffDeadline(s.Pilot.PilotId, s.ExpiresAt) ?? s.ExpiresAt })
            .Where(s => s.ExpiresAt > Now)
            .ToArray();
    }

    private IReadOnlyList<LoggedOffPilot> LoggedOffSql(int system)
    {
        using WorldDbContext world = new(database.Path);
        var rows = world.Set<SavedLogoff>().AsNoTracking().Where(s => s.SystemId == system && s.ExpiresAt > Now).ToArray();
        List<LoggedOffPilot> result = [];
        foreach (var row in rows)
        {
            var owner = world.Pilots.SingleOrDefault(p => p.Id == row.PilotId && p.ActiveShipId == row.ShipId);
            if (
                owner is not null
                && new SqliteWorld(database, clock).FindPilot(owner.AccountId, owner.Id) is { Position: not null } pilot
                && pilot.SystemId == system
                && !world.Set<ShipLoss>().Any(s => s.ShipId == row.ShipId)
            )
            {
                result.Add(new(pilot, row.ExpiresAt));
            }
        }
        return result;
    }
}
