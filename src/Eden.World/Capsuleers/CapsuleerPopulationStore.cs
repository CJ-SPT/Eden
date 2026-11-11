using Eden.Data.World.Capsuleers;
using Eden.Data.World.Characters;
using Eden.Gameplay.Capsuleers;
using Eden.Infrastructure.Registration;
using Eden.World.Catalog;
using Eden.World.Runtime;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;

namespace Eden.World.Capsuleers;

[Injectable(InjectionType.Singleton)]
internal sealed class CapsuleerPopulationStore(WorldDatabase database) : ICapsuleerPopulation
{
    private readonly Lock _gate = new();
    private Metadata? _metadata;

    private sealed record Metadata(long Revision, CapsuleerPopulationSummary Summary, IReadOnlyList<CapsuleerSystemSummary> Systems);

    internal const int PopulationVersion = 1;

    internal static void Configure(ModelBuilder model)
    {
        model.Entity<SavedCapsuleerPopulation>().ToTable("WorldCapsuleerPopulation").HasKey(c => c.SystemId);
        var agents = model.Entity<SavedCapsuleerAgent>();
        agents.ToTable("WorldCapsuleerAgent").HasKey(a => a.PilotId);
        agents.HasOne<Pilot>().WithMany().HasForeignKey(a => a.PilotId);
        agents.HasOne<SavedCapsuleerPopulation>().WithMany().HasForeignKey(a => a.CommunitySystemId);
        agents
            .HasIndex(a => new
            {
                a.CommunitySystemId,
                a.Career,
                a.Slot,
            })
            .IsUnique();
        var suspended = model.Entity<SavedCapsuleerSuspension>();
        suspended.ToTable("WorldCapsuleerSuspension").HasKey(s => s.PilotId);
        suspended.HasOne<SavedCapsuleerAgent>().WithMany().HasForeignKey(s => s.PilotId);
        var jobs = model.Entity<SavedCapsuleerJob>();
        jobs.ToTable("WorldCapsuleerJob").HasKey(j => j.PilotId);
        jobs.HasOne<SavedCapsuleerAgent>().WithMany().HasForeignKey(j => j.PilotId);
        jobs.HasIndex(j => new
        {
            j.DueTicks,
            j.PilotId,
            j.Sequence,
        });
        model.Entity<SavedCapsuleerClock>().ToTable("WorldCapsuleerClock").HasKey(c => c.Id);
    }

    internal static void Initialize(WorldDbContext world)
    {
        world.Database.ExecuteSqlRaw(
            """
            CREATE TABLE IF NOT EXISTS WorldCapsuleerPopulation(
                SystemId INTEGER PRIMARY KEY, HomeStationId INTEGER NOT NULL,
                Seed INTEGER NOT NULL, Version INTEGER NOT NULL CHECK(Version>0));
            CREATE TABLE IF NOT EXISTS WorldCapsuleerAgent(
                PilotId INTEGER PRIMARY KEY REFERENCES WorldPilot(Id),
                CommunitySystemId INTEGER NOT NULL REFERENCES WorldCapsuleerPopulation(SystemId),
                Career INTEGER NOT NULL CHECK(Career IN (1,2)), FounderShipId INTEGER NOT NULL,
                FounderModuleId INTEGER NOT NULL, StartingCents INTEGER NOT NULL CHECK(StartingCents>=0),
                Slot INTEGER NOT NULL DEFAULT 0 CHECK(Slot>=0));
            CREATE TABLE IF NOT EXISTS WorldCapsuleerSuspension(
                PilotId INTEGER PRIMARY KEY REFERENCES WorldCapsuleerAgent(PilotId),
                StartedTicks INTEGER NOT NULL CHECK(StartedTicks>=0));
            CREATE TABLE IF NOT EXISTS WorldCapsuleerJob(
                PilotId INTEGER PRIMARY KEY REFERENCES WorldCapsuleerAgent(PilotId),
                Revision INTEGER NOT NULL CHECK(Revision>=0), Sequence INTEGER NOT NULL CHECK(Sequence>=0),
                DueTicks INTEGER NOT NULL CHECK(DueTicks>=0), Action INTEGER NOT NULL CHECK(Action BETWEEN 1 AND 7));
            CREATE INDEX IF NOT EXISTS IX_WorldCapsuleerJob_DueTicks_PilotId_Sequence
                ON WorldCapsuleerJob(DueTicks,PilotId,Sequence);
            CREATE TABLE IF NOT EXISTS WorldCapsuleerClock(
                Id INTEGER PRIMARY KEY CHECK(Id=1), ElapsedTicks INTEGER NOT NULL CHECK(ElapsedTicks>=0));
            INSERT OR IGNORE INTO WorldCapsuleerClock(Id,ElapsedTicks) VALUES (1,0);
            """
        );
        if (!world.Database.SqlQueryRaw<string>("SELECT name AS Value FROM pragma_table_info('WorldCapsuleerAgent')").Contains("Slot"))
        {
            world.Database.ExecuteSqlRaw("ALTER TABLE WorldCapsuleerAgent ADD COLUMN Slot INTEGER NOT NULL DEFAULT 0 CHECK(Slot>=0)");
        }
        world.Database.ExecuteSqlRaw(
            """
            DROP INDEX IF EXISTS IX_WorldCapsuleerAgent_CommunitySystemId_Career;
            CREATE UNIQUE INDEX IF NOT EXISTS IX_WorldCapsuleerAgent_CommunitySystemId_Career_Slot
                ON WorldCapsuleerAgent(CommunitySystemId,Career,Slot);
            """
        );
    }

    // Called inside startup/character-creation transactions. This records grants already
    // made by the legacy founders; it never provisions assets or copies paid work.
    internal static void Reconcile(WorldDbContext world, CatalogDbContext catalog)
    {
        var miner = world.Set<SavedCapsuleerCommunity>().AsNoTracking().SingleOrDefault(c => c.Id == 1);
        if (miner?.PilotId is not long pilot)
        {
            return;
        }
        int system = catalog.RequireStation(miner.HomeStationId).SolarSystemId;
        world.Database.ExecuteSql(
            $"""
            INSERT INTO WorldCapsuleerPopulation(SystemId,HomeStationId,Seed,Version)
            VALUES ({system},{miner.HomeStationId},{miner.Seed},{PopulationVersion}) ON CONFLICT(SystemId) DO NOTHING
            """
        );
        AddAgent(world, new(pilot, system, miner.Career, miner.FounderShipId, miner.FounderModuleId, miner.StartingCents));
        if (world.Set<SavedCapsuleerHauler>().AsNoTracking().SingleOrDefault(h => h.Id == 1) is { } hauler)
        {
            AddAgent(world, new(hauler.PilotId, system, CapsuleerCareer.Hauling, hauler.FounderShipId, 0, hauler.StartingCents));
        }
    }

    internal static void AddAgent(WorldDbContext world, CapsuleerAgent agent) =>
        world.Database.ExecuteSql(
            $"""
            INSERT INTO WorldCapsuleerAgent(PilotId,CommunitySystemId,Career,FounderShipId,FounderModuleId,StartingCents,Slot)
            VALUES ({agent.PilotId},{agent.CommunitySystemId},{agent.Career},{agent.FounderShipId},
                {agent.FounderModuleId},{agent.StartingCents},{agent.Slot}) ON CONFLICT(PilotId) DO NOTHING
            """
        );

    internal static void ScheduleFounders(WorldDbContext world)
    {
        long elapsed = world.Set<SavedCapsuleerClock>().Single().ElapsedTicks;
        var existing = world.Set<SavedCapsuleerJob>().Select(j => j.PilotId).ToHashSet();
        foreach (var agent in world.Set<SavedCapsuleerAgent>().AsNoTracking().OrderBy(a => a.PilotId).ToArray())
        {
            if (existing.Contains(agent.PilotId))
            {
                continue;
            }
            CapsuleerSchedule schedule = new(
                checked(elapsed + TimeSpan.TicksPerSecond),
                agent.Career == CapsuleerCareer.Mining ? CapsuleerAction.Prepare : CapsuleerAction.Trade
            );
            bool inSpace = world.Pilots.Where(p => p.Id == agent.PilotId).Any(p => p.ActiveShip.Position != null);
            if (agent.Career == CapsuleerCareer.Mining)
            {
                var row = world.Set<SavedCapsuleerCommunity>().Single(c => c.PilotId == agent.PilotId);
                if (MinerCheckpoint.Read(row.Progress) is { } work)
                {
                    work = work with
                    {
                        ActiveTicks = elapsed,
                        ClockOffsetTicks = checked(elapsed - (long)work.Stamp * TimeSpan.TicksPerSecond),
                        DueTicks = CapsuleerScheduleRules.MigrateDeadline(elapsed, work.Stamp, work.DueStamp),
                    };
                    new MinerCheckpoint(agent.PilotId, work).Apply(world);
                    schedule = CapsuleerScheduleRules.Next(work, inSpace);
                }
            }
            else
            {
                var row = world.Set<SavedCapsuleerHauler>().Single(h => h.PilotId == agent.PilotId);
                if (HaulerCheckpoint.Read(row.Progress) is { } work)
                {
                    work = work with
                    {
                        ActiveTicks = elapsed,
                        ClockOffsetTicks = checked(elapsed - (long)work.Stamp * TimeSpan.TicksPerSecond),
                        DueTicks = CapsuleerScheduleRules.MigrateDeadline(elapsed, work.Stamp, work.DueStamp),
                    };
                    new HaulerCheckpoint(agent.PilotId, work).Apply(world);
                    schedule = CapsuleerScheduleRules.Next(work, inSpace);
                }
            }
            CapsuleerJobCheckpoint.Apply(world, new(agent.PilotId, 0, 0, schedule.DueTicks, schedule.Action));
        }
    }

    public IReadOnlyList<CapsuleerCommunitySummary> Communities()
    {
        using WorldDbContext world = new(database.Path);
        return world
            .Set<SavedCapsuleerPopulation>()
            .OrderBy(c => c.SystemId)
            .Select(c => new CapsuleerCommunitySummary(c.SystemId, c.HomeStationId, c.Seed, c.Version))
            .ToArray();
    }

    public IReadOnlyList<CapsuleerAgent> Agents(int communitySystemId)
    {
        using WorldDbContext world = new(database.Path);
        return world
            .Set<SavedCapsuleerAgent>()
            .Where(a => a.CommunitySystemId == communitySystemId)
            .OrderBy(a => a.PilotId)
            .Select(a => new CapsuleerAgent(
                a.PilotId,
                a.CommunitySystemId,
                a.Career,
                a.FounderShipId,
                a.FounderModuleId,
                a.StartingCents,
                a.Slot
            ))
            .ToArray();
    }

    public IReadOnlyList<CapsuleerSystemSummary> Systems() => ReadMetadata().Systems;

    public CapsuleerPopulationSummary Summary() => ReadMetadata().Summary;

    private Metadata ReadMetadata()
    {
        return LiveWorld.Execute(database.Path, () => ReadMetadataOwned(), nameof(ReadMetadata));
    }

    private Metadata ReadMetadataOwned()
    {
        var live = LiveWorld.Find(database.Path);
        using var owner = live?.Enter();
        lock (_gate)
        {
            if (live is not null && _metadata?.Revision == live.CapsuleerRevision)
            {
                return _metadata;
            }
            using WorldDbContext world = new(database.Path) { Participants = TransactionParticipants.None };
            bool incremental = live is not null && _metadata is not null && _metadata.Revision >= live.CapsuleerMetadataReset;
            int[] changedSystems = incremental
                ? live!.CapsuleerSystemRevisions.Where(p => p.Value > _metadata!.Revision).Select(p => p.Key).ToArray()
                : [];
            var communities = world
                .Set<SavedCapsuleerPopulation>()
                .Where(c => !incremental || changedSystems.Contains(c.SystemId))
                .Select(c => c.SystemId)
                .ToHashSet();
            using CatalogDbContext catalog = new(database.CatalogPath);
            long seed = world.Set<SavedCapsuleerProvisioning>().Select(p => p.WorldSeed).SingleOrDefault();
            string fingerprint = CapsuleerProvisioner.Fingerprint(database, catalog, seed, world.Settings.Capsuleers);
            var systems = world
                .Set<SavedCapsuleerSystem>()
                .Where(s => !incremental || changedSystems.Contains(s.SystemId))
                .AsNoTracking()
                .OrderBy(s => s.SystemId)
                .AsEnumerable()
                .Select(s => new CapsuleerSystemSummary(
                    s.SystemId,
                    s.Eligibility,
                    s.HomeStationId,
                    s.DestinationStationId,
                    communities.Contains(s.SystemId)
                )
                {
                    CurrentPolicy = s.PolicyVersion == CapsuleerProvisioningRules.Version && s.CatalogFingerprint == fingerprint,
                })
                .ToArray();
            if (incremental)
            {
                var current = _metadata!.Systems.ToDictionary(s => s.SystemId);
                foreach (int system in changedSystems)
                {
                    current.Remove(system);
                }
                foreach (var system in systems)
                {
                    current[system.SystemId] = system;
                }
                systems = current.Values.OrderBy(s => s.SystemId).ToArray();
            }
            CapsuleerPopulationSummary summary = new(
                systems.Length,
                systems.Count(s => s.Eligibility == CapsuleerEligibility.Eligible),
                incremental ? world.Set<SavedCapsuleerPopulation>().Count() : communities.Count,
                world.Set<SavedCapsuleerAgent>().Count(),
                seed
            )
            {
                PausedAgents = world.Set<SavedCapsuleerSuspension>().Count(),
                PreparedSystems = systems.Count(s => s.CurrentPolicy),
                PreparedEligibleSystems = systems.Count(s => s.CurrentPolicy && s.Eligibility == CapsuleerEligibility.Eligible),
            };
            var metadata = new Metadata(live?.CapsuleerRevision ?? 0, summary, Array.AsReadOnly(systems));
            if (live is not null)
            {
                _metadata = metadata;
            }
            return metadata;
        }
    }

    internal static CapsuleerJob[] Jobs(WorldDbContext world) =>
        world
            .Set<SavedCapsuleerJob>()
            .OrderBy(j => j.DueTicks)
            .ThenBy(j => j.PilotId)
            .ThenBy(j => j.Sequence)
            .Select(j => new CapsuleerJob(j.PilotId, j.Revision, j.Sequence, j.DueTicks, j.Action))
            .ToArray();

    // The action's feature writes and job advancement use this same transaction.
    // Duplicate/stale callbacks return before invoking any feature operation.
    internal static bool Settle(
        WorldDbContext world,
        CapsuleerJob expected,
        long nextDueTicks,
        CapsuleerAction nextAction,
        Action<WorldDbContext> apply
    )
    {
        var next = expected.Next(nextDueTicks, nextAction);
        if (nextDueTicks <= expected.DueTicks)
        {
            throw new ArgumentOutOfRangeException(nameof(nextDueTicks));
        }
        using var transaction = world.Database.BeginTransaction();
        int changed = world.Database.ExecuteSql(
            $"""
            UPDATE WorldCapsuleerJob SET Revision={next.Revision}, Sequence={next.Sequence},
                DueTicks={next.DueTicks}, Action={next.Action}
            WHERE PilotId={expected.PilotId} AND Revision={expected.Revision} AND Sequence={expected.Sequence}
                AND DueTicks={expected.DueTicks} AND Action={expected.Action}
            """
        );
        if (changed == 0)
        {
            return false;
        }
        apply(world);
        world.SaveChanges();
        transaction.Commit();
        return true;
    }
}
