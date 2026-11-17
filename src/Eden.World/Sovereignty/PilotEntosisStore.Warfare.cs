using Eden.Data.World.Alliances;
using Eden.Data.World.Sovereignty;
using Eden.Data.World.Structures;
using Eden.Gameplay.Flight;
using Eden.Gameplay.Sovereignty;
using Eden.Gameplay.Structures;
using Eden.World.Alliances;
using Eden.World.Catalog;
using Eden.World.Runtime.Economy;
using Eden.World.Storage;

namespace Eden.World.Sovereignty;

internal sealed partial class PilotEntosisStore
{
    private sealed record EntosisTarget(long Id, int TypeId, int SystemId, SpacePosition Position);

    public void AdvanceAll()
    {
        if (!Available)
        {
            return;
        }
        if (!Live.Economy.Inspect(world => world.Set<SavedSovereigntyClaim>().Any() || world.Set<SavedSovereigntyActivityEvent>().Any()))
        {
            return;
        }
        long now = Now;
        using var action = Live.Economy.Begin(TransactionParticipants.None);
        using CatalogDbContext catalog = new(database.CatalogPath);
        foreach (var claim in action.Set<SavedSovereigntyClaim>())
        {
            EnsureConflict(action, catalog, claim, now);
        }
        int[] representatives = action
            .Set<SavedSovereigntyConflict>()
            .GroupBy(row => row.ConstellationId)
            .Select(group => group.First().SystemId)
            .Concat(action.Set<SavedSovereigntyActivityEvent>().Select(row => row.SystemId))
            .Distinct()
            .ToArray();
        foreach (int system in representatives)
        {
            AdvanceWarfare(action, catalog, system, now);
        }
        action.Commit();
    }

    private static int Alliance(WorldAction world, long pilotId) =>
        world.Set<SavedAllianceMember>().Find(world.Pilots.Find(pilotId)!.CorporationId)?.AllianceId
        ?? throw new InvalidDataException("Your corporation is not in an alliance.");

    private static AlliancePrimeTime Prime(WorldAction world, int alliance, long now)
    {
        var saved = world.Set<SavedSovereigntyPrime>().Find(alliance);
        return SovereigntyWarfareRules.Prime(
            saved is null
                ? new(SovereigntyWarfareRules.DefaultPrimeHour, null, null)
                : new(saved.CurrentHour, saved.NewHour, saved.ValidAfter),
            now
        );
    }

    public AlliancePrimeTime PrimeTime(long pilotId) => Live.Economy.Inspect(world => Prime(world, Alliance(world, pilotId), Now));

    public void SetPrimeHour(long pilotId, int hour)
    {
        if (hour is < 0 or > 23)
        {
            throw new InvalidDataException("Choose a UTC prime hour between zero and 23.");
        }
        long now = Now;
        using var action = Live.Economy.Begin(new(pilots: [pilotId]));
        int alliance = AllianceStore.Executor(action, pilotId).Id;
        var current = Prime(action, alliance, now);
        if (current.PreviousHour is not null)
        {
            throw new InvalidDataException("Wait for the overlapping vulnerability transition to finish before changing prime time again.");
        }
        using CatalogDbContext catalog = new(database.CatalogPath);
        foreach (var claim in action.Filter<SavedSovereigntyClaim>(row => row.AllianceId == alliance).ToArray())
        {
            EnsureConflict(action, catalog, claim, now);
            AdvanceWarfare(action, catalog, claim.SystemId, now);
        }
        var saved =
            action.Set<SavedSovereigntyPrime>().Find(alliance) ?? action.Add(new SavedSovereigntyPrime { AllianceId = alliance }).Entity;
        saved.CurrentHour = current.CurrentHour;
        saved.NewHour = hour == current.CurrentHour ? null : hour;
        saved.ValidAfter = saved.NewHour is null ? null : checked(now + SovereigntyWarfareRules.PrimeChangeDelay);
        action.Commit();
    }

    public IReadOnlyList<SovereigntyHub> AllianceHubs(long pilotId)
    {
        int alliance = Live.Economy.Inspect(world => Alliance(world, pilotId));
        int[] systems = Live.Economy.Inspect(world =>
            world.Filter<SavedSovereigntyClaim>(row => row.AllianceId == alliance).Select(row => row.SystemId).ToArray()
        );
        foreach (int system in systems)
        {
            Advance(system, Now);
        }
        return Live.Economy.Inspect(world =>
            (IReadOnlyList<SovereigntyHub>)systems.Select(system => Snapshot(world, system, Now)).OfType<SovereigntyHub>().ToArray()
        );
    }

    private static SavedSovereigntyConflict EnsureConflict(
        WorldAction world,
        CatalogDbContext catalog,
        SavedSovereigntyClaim claim,
        long now
    )
    {
        if (world.Set<SavedSovereigntyConflict>().Find(claim.SystemId) is { } existing)
        {
            return existing;
        }
        SovereigntyDevelopmentPersistence.ReconcileCapital(world, claim.AllianceId, now);
        double adm = (double)SovereigntyDevelopmentPersistence.Read(world, claim.SystemId, now).DefenseMultiplier;
        var created = new SavedSovereigntyConflict
        {
            SystemId = claim.SystemId,
            HubId = claim.HubId,
            ConstellationId = catalog.RequireSystem(claim.SystemId).ConstellationId,
            DefenderAllianceId = claim.AllianceId,
            Value = 1,
            UpdatedAt = now,
            DevelopmentAt = now,
            AppliedAdm = adm,
        };
        InitializeWindow(created, Prime(world, claim.AllianceId, now).CurrentHour, now, adm);
        return world.Add(created).Entity;
    }

    private static EntosisTarget? Target(WorldAction world, int system, long id)
    {
        if (world.Set<SavedSovereigntyNode>().Find(id) is { } node && node.SystemId == system)
        {
            return new(id, SovereigntyWarfareRules.CommandNodeTypeId, system, new(node.X, node.Y, node.Z));
        }
        return
            world.Set<SavedSovereigntyHub>().Find(system)?.HubId == id
            && world.Set<SavedStructure>().Find(id) is { State: not StructureState.Destroyed } hub
            ? new(id, hub.TypeId, system, new(hub.X, hub.Y, hub.Z))
            : null;
    }

    private static bool Admits(WorldAction world, EntosisTarget target, long now)
    {
        if (target.TypeId == SovereigntyWarfareRules.CommandNodeTypeId)
        {
            var node = world.Set<SavedSovereigntyNode>().Find(target.Id);
            return node is not null
                && world.Set<SavedSovereigntyConflict>().Find(node.SourceSystemId) is { Phase: SovereigntyPhase.Campaign };
        }
        if (world.Set<SavedSovereigntyClaim>().Find(target.SystemId) is null)
        {
            return true;
        }
        var conflict = world.Set<SavedSovereigntyConflict>().Find(target.SystemId);
        return conflict is { Phase: SovereigntyPhase.Secure }
            && now >= conflict.SecureUntil
            && (conflict.Value < 1 || SovereigntyWarfareRules.Open(Window(world, conflict, now), now));
    }

    private static VulnerabilityWindow Window(WorldAction world, SavedSovereigntyConflict conflict, long now) =>
        ResolveWindow(world, conflict, now);

    private static VulnerabilityWindow ResolveWindow(WorldAction world, SavedSovereigntyConflict row, long now)
    {
        var current = PlannedWindow(row, now);
        var prime = Prime(world, row.DefenderAllianceId, now);
        if (prime.PreviousHour is int oldHour)
        {
            decimal multiplier = (decimal)(SovereigntyWarfareRules.BaseVulnerabilityDuration / (double)(current.EndsAt - current.StartsAt));
            var old = SovereigntyWarfareRules.Window(oldHour, now, Math.Clamp(multiplier, 1m, 6m));
            if (SovereigntyWarfareRules.Open(old, now))
            {
                return SovereigntyWarfareRules.Open(current, now)
                    ? new(Math.Min(old.StartsAt, current.StartsAt), Math.Max(old.EndsAt, current.EndsAt))
                    : old;
            }
        }
        return current;
    }

    private static SovereigntyConflict Conflict(SavedSovereigntyConflict row) =>
        new(
            row.SystemId,
            row.HubId,
            row.ConstellationId,
            row.DefenderAllianceId,
            row.Phase,
            new(row.WindowStart, row.WindowEnd),
            row.CampaignAt,
            row.DefenderPoints,
            new(row.Value, row.UpdatedAt, row.ScoringTeam, row.Rate, row.Blocked)
        )
        {
            DefenseMultiplier = (decimal)row.AppliedAdm,
            WindowMultiplier =
                row.WindowEnd > row.WindowStart
                    ? Math.Clamp(
                        (decimal)(SovereigntyWarfareRules.BaseVulnerabilityDuration / (double)(row.WindowEnd - row.WindowStart)),
                        1m,
                        6m
                    )
                    : (decimal)row.WindowAdm,
            CampaignMultiplier = (decimal)row.CampaignAdm,
        };

    private EntosisContribution[] Contributions(WorldAction world, CatalogDbContext catalog, EntosisTarget target, long now)
    {
        var cycles = world.Filter<SavedEntosisCycle>(row => row.SystemId == target.SystemId && row.HubId == target.Id).ToArray();
        foreach (var cycle in cycles)
        {
            if (cycle.Contributing && (!_scoringLeases.Contains(cycle.ModuleId) || !Valid(world, catalog, cycle, target)))
            {
                cycle.Contributing = false;
                cycle.Stopping = true;
                world.AfterCommit(() => _scoringLeases.Remove(cycle.ModuleId));
            }
        }
        // Expired rows must contribute through their paid end before retirement.
        var result = cycles
            .Where(row => row.Contributing)
            .Select(row => new EntosisContribution(row.AllianceId, Math.Max(row.StartedAt, row.WarmupUntil), row.EndsAt))
            .ToArray();
        return result;
    }

    private void StopTarget(WorldAction world, long target)
    {
        foreach (var cycle in world.Filter<SavedEntosisCycle>(row => row.HubId == target))
        {
            cycle.Stopping = true;
            cycle.Contributing = false;
            world.AfterCommit(() => _scoringLeases.Remove(cycle.ModuleId));
        }
    }

    private sealed record ConflictView(
        int System,
        long Hub,
        SovereigntyPhase Phase,
        long WindowStart,
        long WindowEnd,
        long? CampaignAt,
        int Points,
        bool UnderAttack
    );

    private static ConflictView[] Views(WorldAction world, int constellation) =>
        world
            .Filter<SavedSovereigntyConflict>(row => row.ConstellationId == constellation)
            .OrderBy(row => row.SystemId)
            .Select(row => new ConflictView(
                row.SystemId,
                row.HubId,
                row.Phase,
                row.WindowStart,
                row.WindowEnd,
                row.CampaignAt,
                row.DefenderPoints,
                row.Value < 1
            ))
            .ToArray();

    private void AdvanceWarfareStep(WorldAction world, CatalogDbContext catalog, int system, long now)
    {
        if (world.Set<SavedSovereigntyClaim>().Find(system) is { } claim)
        {
            EnsureConflict(world, catalog, claim, now);
        }
        int constellation = catalog.RequireSystem(system).ConstellationId;
        var before = Views(world, constellation);
        foreach (
            var conflict in world
                .Filter<SavedSovereigntyConflict>(row => row.ConstellationId == constellation)
                .OrderBy(row => row.SystemId)
                .ToArray()
        )
        {
            if (now < conflict.UpdatedAt)
            {
                continue;
            }
            bool wasDisrupted = conflict.Value < 1;
            var window = Window(world, conflict, Math.Max(now, conflict.SecureUntil));
            if (conflict.Phase != SovereigntyPhase.Secure || conflict.Value == 1)
            {
                conflict.WindowStart = window.StartsAt;
                conflict.WindowEnd = window.EndsAt;
            }
            if (conflict.Phase == SovereigntyPhase.Secure)
            {
                var target = Target(world, conflict.SystemId, conflict.HubId);
                if (target is null || now < conflict.SecureUntil)
                {
                    continue;
                }
                var links = Contributions(world, catalog, target, now);
                if (conflict.Value == 1 && links.Length == 0)
                {
                    continue;
                }
                var profile = EntosisCatalog.Defense(catalog, false);
                // Use the window containing the last progress timestamp for delayed callbacks.
                var previousWindow = Window(world, conflict, conflict.UpdatedAt);
                var progress = SovereigntyWarfareRules.Advance(
                    new(conflict.Value, conflict.UpdatedAt),
                    links,
                    now,
                    profile.Duration,
                    profile.Regeneration,
                    conflict.DefenderAllianceId,
                    false,
                    SovereigntyWarfareRules.Open(previousWindow, conflict.UpdatedAt) ? previousWindow.EndsAt : 0,
                    (decimal)conflict.AppliedAdm
                );
                Apply(conflict, progress);
                if (progress.Value < 1 && !wasDisrupted)
                {
                    conflict.WindowStart = previousWindow.StartsAt;
                    conflict.WindowEnd = previousWindow.EndsAt;
                }
                if (progress.Value == 1)
                {
                    conflict.WindowStart = window.StartsAt;
                    conflict.WindowEnd = window.EndsAt;
                }
                Retire(world, world.Filter<SavedEntosisCycle>(row => row.HubId == target.Id).ToArray(), now);
                if (progress.CompletedAt is long at && progress.Value == 0)
                {
                    conflict.Phase = SovereigntyPhase.Reinforced;
                    conflict.CampaignAdm = conflict.AppliedAdm;
                    conflict.CampaignAt = SovereigntyWarfareRules.CampaignDeadline(
                        at,
                        Prime(world, conflict.DefenderAllianceId, at).CurrentHour,
                        conflict.HubId,
                        (decimal)conflict.CampaignAdm
                    );
                    conflict.DefenderPoints = SovereigntyWarfareRules.InitialDefenderPoints;
                    conflict.Rate = 0;
                    StopTarget(world, conflict.HubId);
                }
            }
        }
        AdvanceConstellationCampaigns(world, catalog, constellation, now);
        if (!before.SequenceEqual(Views(world, constellation)))
        {
            world.AfterCommit(() => Interlocked.Increment(ref _viewRevision));
        }
    }

    private static void Apply(SavedSovereigntyConflict row, TerritoryProgress progress)
    {
        row.Value = progress.Value;
        row.UpdatedAt = progress.UpdatedAt;
        row.ScoringTeam = progress.ScoringTeam;
        row.Rate = progress.Rate;
        row.Blocked = progress.Blocked;
    }

    public IReadOnlyList<StaticSpaceObject> CommandNodes(int systemId)
    {
        Advance(systemId, Now);
        return Live.Economy.Inspect(world =>
        {
            if (!world.Filter<SavedSovereigntyNode>(row => row.SystemId == systemId).Any())
            {
                return Array.Empty<StaticSpaceObject>();
            }
            using CatalogDbContext catalog = new(database.CatalogPath);
            var type = catalog.FittingType(SovereigntyWarfareRules.CommandNodeTypeId)!;
            long duration = EntosisCatalog.Defense(catalog, true).Duration;
            return (IReadOnlyList<StaticSpaceObject>)
                world
                    .Filter<SavedSovereigntyNode>(row => row.SystemId == systemId)
                    .OrderBy(row => row.Id)
                    .Select(row => new StaticSpaceObject(
                        row.Id,
                        SovereigntyWarfareRules.CommandNodeTypeId,
                        type.GroupId,
                        type.CategoryId,
                        row.CorporationId,
                        new(row.X, row.Y, row.Z),
                        (float)(type.Radius ?? 0)
                    )
                    {
                        Name = $"{catalog.RequireSystem(row.SourceSystemId).Name} Sovereignty Hub Command Node {row.Id}",
                        Structure = new(StructureState.ShieldVulnerable, row.UpdatedAt, null, null, false),
                        CommandNode = new(
                            row.Id,
                            row.SystemId,
                            row.SourceSystemId,
                            row.HubId,
                            row.CorporationId,
                            row.AllianceId,
                            new(row.X, row.Y, row.Z),
                            new(row.Value, row.UpdatedAt, row.ScoringTeam, row.Rate, row.Blocked),
                            duration
                        ),
                        Solid = false,
                    })
                    .ToArray();
        });
    }
}
