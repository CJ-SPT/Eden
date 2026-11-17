using Eden.Data.World.Sovereignty;
using Eden.Gameplay.Sovereignty;
using Eden.World.Alliances;
using Eden.World.Catalog;
using Eden.World.Runtime.Economy;
using Eden.World.Storage;

namespace Eden.World.Sovereignty;

internal sealed partial class PilotEntosisStore
{
    public SovereigntyDevelopment Development(int systemId)
    {
        long now = Now;
        Advance(systemId, now);
        return Live.Economy.Inspect(world => SovereigntyDevelopmentPersistence.Read(world, systemId, now));
    }

    public IReadOnlyList<SovereigntyDevelopment> AllDevelopment()
    {
        AdvanceAll();
        long now = Now;
        return Live.Economy.Inspect(world =>
            (IReadOnlyList<SovereigntyDevelopment>)
                world
                    .Set<SavedSovereigntyClaim>()
                    .Select(row => row.SystemId)
                    .Concat(world.Set<SavedSovereigntyActivity>().Select(row => row.SystemId))
                    .Distinct()
                    .Order()
                    .Select(system => SovereigntyDevelopmentPersistence.Read(world, system, now))
                    .ToArray()
        );
    }

    private void AdvanceAlliance(WorldAction world, CatalogDbContext catalog, int alliance, long now)
    {
        int[] systems = world.Filter<SavedSovereigntyClaim>(row => row.AllianceId == alliance).Select(row => row.SystemId).ToArray();
        foreach (int system in systems)
        {
            AdvanceWarfare(world, catalog, system, now);
        }
        SovereigntyDevelopmentPersistence.ReconcileCapital(world, alliance, now);
    }

    public SovereigntyCapital Capital(long pilotId)
    {
        using var action = Live.Economy.Begin(new(pilots: [pilotId]));
        using CatalogDbContext catalog = new(database.CatalogPath);
        int alliance = Alliance(action, pilotId);
        AdvanceAlliance(action, catalog, alliance, Now);
        var result = SovereigntyDevelopmentPersistence.Capital(action, alliance);
        action.Commit();
        return result;
    }

    public void SetCapital(long pilotId, int systemId)
    {
        using var action = Live.Economy.Begin(new(pilots: [pilotId]));
        using CatalogDbContext catalog = new(database.CatalogPath);
        int alliance = AllianceStore.Executor(action, pilotId).Id;
        long now = Now;
        AdvanceAlliance(action, catalog, alliance, now);
        if (action.Set<SavedSovereigntyClaim>().Find(systemId)?.AllianceId != alliance)
        {
            throw new InvalidDataException("Choose a system currently claimed by your alliance.");
        }
        var row = action.Set<SavedSovereigntyCapital>().Find(alliance)!;
        row.NewSystemId = systemId == row.CurrentSystemId ? null : systemId;
        row.ValidAfter = row.NewSystemId is null ? null : checked(now + SovereigntyDevelopmentRules.CapitalDelay);
        action.AfterCommit(() => Interlocked.Increment(ref _viewRevision));
        action.Commit();
    }

    public void CancelCapital(long pilotId)
    {
        using var action = Live.Economy.Begin(new(pilots: [pilotId]));
        using CatalogDbContext catalog = new(database.CatalogPath);
        int alliance = AllianceStore.Executor(action, pilotId).Id;
        AdvanceAlliance(action, catalog, alliance, Now);
        if (action.Set<SavedSovereigntyCapital>().Find(alliance) is { } row)
        {
            row.NewSystemId = null;
            row.ValidAfter = null;
        }
        action.AfterCommit(() => Interlocked.Increment(ref _viewRevision));
        action.Commit();
    }

    private static void InitializeWindow(SavedSovereigntyConflict row, int hour, long now, double multiplier)
    {
        var window = SovereigntyWarfareRules.Window(hour, now, (decimal)multiplier);
        row.WindowCenter = window.StartsAt + (window.EndsAt - window.StartsAt) / 2;
        row.WindowAdm = multiplier;
        row.FollowingAdm = multiplier;
        row.ScheduledPrimeHour = hour;
        if (row.Value == 1 && row.Phase == SovereigntyPhase.Secure)
        {
            row.WindowStart = window.StartsAt;
            row.WindowEnd = window.EndsAt;
        }
        row.NextWindowLockAt = row.WindowCenter > now ? row.WindowCenter : checked(row.WindowCenter + TimeSpan.TicksPerDay);
    }

    private static VulnerabilityWindow PlannedWindow(SavedSovereigntyConflict row, long now)
    {
        long half = (long)(SovereigntyWarfareRules.BaseVulnerabilityDuration / row.WindowAdm) / 2;
        return now < row.WindowCenter + half
            ? new(row.WindowCenter - half, row.WindowCenter + half)
            : new(
                row.WindowCenter + TimeSpan.TicksPerDay - (long)(SovereigntyWarfareRules.BaseVulnerabilityDuration / row.FollowingAdm) / 2,
                row.WindowCenter + TimeSpan.TicksPerDay + (long)(SovereigntyWarfareRules.BaseVulnerabilityDuration / row.FollowingAdm) / 2
            );
    }

    private static void LockWindows(WorldAction world, SavedSovereigntyConflict row, long through, double multiplier)
    {
        if (row.NextWindowLockAt <= through)
        {
            // The first midpoint freezes tomorrow without moving today's center. Later locks promote that frozen window.
            // The caller splits at every ADM change, so intervening daily locks can share this interval's multiplier.
            long count = (through - row.NextWindowLockAt) / TimeSpan.TicksPerDay + 1;
            bool initial = row.NextWindowLockAt == row.WindowCenter;
            long shifts = count - (initial ? 1 : 0);
            if (shifts > 0)
            {
                row.WindowAdm = shifts == 1 ? row.FollowingAdm : multiplier;
                row.WindowCenter = checked(row.WindowCenter + shifts * TimeSpan.TicksPerDay);
            }
            row.FollowingAdm = multiplier;
            row.NextWindowLockAt = checked(row.NextWindowLockAt + count * TimeSpan.TicksPerDay);
        }
        int hour = Prime(world, row.DefenderAllianceId, through).CurrentHour;
        if (hour != row.ScheduledPrimeHour)
        {
            // Changing prime time retains the already frozen length through its handover.
            var window = PlannedWindow(row, through);
            double frozen = SovereigntyWarfareRules.BaseVulnerabilityDuration / (double)(window.EndsAt - window.StartsAt);
            InitializeWindow(row, hour, through, frozen);
        }
    }

    private static long NextDevelopmentBoundary(WorldAction world, SavedSovereigntyConflict row, long from, long now)
    {
        long next = now;
        void Include(long? at)
        {
            if (at is long value && value > from && value < next)
            {
                next = value;
            }
        }
        var state = SovereigntyDevelopmentPersistence.Activity(world, row.SystemId, from);
        Include(SovereigntyDevelopmentRules.NextLevelChange(state, from));
        Include(state.LastMilitaryAt + SovereigntyDevelopmentRules.TrendDuration);
        Include(state.LastIndustrialAt + SovereigntyDevelopmentRules.TrendDuration);
        if (world.Set<SavedSovereigntyClaim>().Find(row.SystemId) is { } claim)
        {
            Include(SovereigntyRules.NextStrategicChange(claim.ClaimedAt, from));
        }
        if (world.Set<SavedSovereigntyCapital>().Find(row.DefenderAllianceId) is { } capital)
        {
            Include(capital.ValidAfter);
            Include(capital.CurrentSince);
        }
        foreach (var change in world.Filter<SavedSovereigntyCapitalChange>(change => change.AllianceId == row.DefenderAllianceId))
        {
            Include(change.At);
        }
        if (world.Set<SavedSovereigntyPrime>().Find(row.DefenderAllianceId) is { } prime)
        {
            Include(prime.ValidAfter);
            if (prime.ValidAfter is long at)
            {
                Include(at + SovereigntyWarfareRules.TransitionOverlap);
            }
        }
        return next;
    }

    private void AdvanceWarfare(WorldAction world, CatalogDbContext catalog, int system, long now)
    {
        int constellation = catalog.RequireSystem(system).ConstellationId;
        var systems = EntosisCatalog.ConstellationSystems(catalog, constellation);
        foreach (var claim in systems.Select(id => world.Set<SavedSovereigntyClaim>().Find(id)).OfType<SavedSovereigntyClaim>())
        {
            SovereigntyDevelopmentPersistence.ReconcileCapital(world, claim.AllianceId, now);
            var row = EnsureConflict(world, catalog, claim, now);
            if (row.WindowCenter == 0)
            {
                // Existing M3 contests retain their frozen base ADM and earned capture progress.
                InitializeWindow(row, Prime(world, claim.AllianceId, now).CurrentHour, now, row.WindowAdm);
                row.FollowingAdm = (double)SovereigntyDevelopmentPersistence.Read(world, row.SystemId, now).DefenseMultiplier;
                row.DevelopmentAt = now;
            }
        }
        var pending = systems
            .SelectMany(id => world.Filter<SavedSovereigntyActivityEvent>(row => row.SystemId == id))
            .Where(row => row.At <= now)
            .OrderBy(row => row.At)
            .ThenBy(row => row.Id)
            .ToArray();
        int index = 0;
        while (true)
        {
            var conflicts = world.Filter<SavedSovereigntyConflict>(row => row.ConstellationId == constellation).ToArray();
            long from = conflicts.Select(row => row.DevelopmentAt).DefaultIfEmpty(index < pending.Length ? pending[index].At : now).Min();
            if (from > now)
            {
                return;
            }
            long next = conflicts
                .Select(row => NextDevelopmentBoundary(world, row, Math.Max(from, row.DevelopmentAt), now))
                .DefaultIfEmpty(now)
                .Min();
            next = Math.Min(next, NextCampaignBoundary(world, catalog, constellation, next));
            if (index < pending.Length)
            {
                next = Math.Min(next, Math.Max(from, pending[index].At));
            }
            foreach (var row in conflicts.Where(row => row.DevelopmentAt <= next))
            {
                LockWindows(world, row, Math.Max(0, next - 1), row.AppliedAdm);
            }
            var previous = conflicts
                .Where(row => row.DevelopmentAt <= next)
                .ToDictionary(row => row.SystemId, row => SovereigntyDevelopmentPersistence.Read(world, row.SystemId, row.DevelopmentAt));
            AdvanceWarfareStep(world, catalog, system, next);
            bool credited = false;
            while (index < pending.Length && pending[index].At <= next)
            {
                SovereigntyDevelopmentPersistence.Apply(world, pending[index++], next);
                credited = true;
            }
            foreach (var row in world.Filter<SavedSovereigntyConflict>(row => row.ConstellationId == constellation))
            {
                if (row.DevelopmentAt > next)
                {
                    continue;
                }
                SovereigntyDevelopmentPersistence.SettleActivity(world, row.SystemId, next);
                var value = SovereigntyDevelopmentPersistence.Read(world, row.SystemId, next);
                var before = previous.GetValueOrDefault(row.SystemId);
                bool changed =
                    row.AppliedAdm != (double)value.DefenseMultiplier
                    || before?.Indices != value.Indices
                    || before?.IsCapital != value.IsCapital
                    || before?.MilitaryIncreasing != value.MilitaryIncreasing
                    || before?.IndustrialIncreasing != value.IndustrialIncreasing;
                row.AppliedAdm = (double)value.DefenseMultiplier;
                LockWindows(world, row, next, row.AppliedAdm);
                row.DevelopmentAt = next;
                if (row.Phase == SovereigntyPhase.Secure)
                {
                    if (row.Rate < 0)
                    {
                        row.Rate = -1 / row.AppliedAdm;
                    }
                    if (row.Value == 1)
                    {
                        var window = Window(world, row, Math.Max(next, row.SecureUntil));
                        row.WindowStart = window.StartsAt;
                        row.WindowEnd = window.EndsAt;
                    }
                }
                if (changed)
                {
                    world.AfterCommit(() => Interlocked.Increment(ref _viewRevision));
                }
            }
            if (credited)
            {
                world.AfterCommit(() => Interlocked.Increment(ref _viewRevision));
            }
            if (next == now)
            {
                break;
            }
        }
    }
}
