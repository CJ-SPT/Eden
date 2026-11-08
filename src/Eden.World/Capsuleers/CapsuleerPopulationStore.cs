using Eden.Data.World.Capsuleers;
using Eden.Data.World.Characters;
using Eden.Gameplay.Capsuleers;
using Eden.Infrastructure.Registration;
using Eden.World.Catalog;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;

namespace Eden.World.Capsuleers;

[Injectable]
internal sealed class CapsuleerPopulationStore(WorldDatabase database) : ICapsuleerPopulation
{
    internal const int PopulationVersion = 1;

    internal static void Configure(ModelBuilder model)
    {
        model.Entity<SavedCapsuleerPopulation>().ToTable("WorldCapsuleerPopulation").HasKey(c => c.SystemId);
        var agents = model.Entity<SavedCapsuleerAgent>();
        agents.ToTable("WorldCapsuleerAgent").HasKey(a => a.PilotId);
        agents.HasOne<Pilot>().WithMany().HasForeignKey(a => a.PilotId);
        agents.HasOne<SavedCapsuleerPopulation>().WithMany().HasForeignKey(a => a.CommunitySystemId);
        agents.HasIndex(a => new { a.CommunitySystemId, a.Career }).IsUnique();
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
                FounderModuleId INTEGER NOT NULL, StartingCents INTEGER NOT NULL CHECK(StartingCents>=0));
            CREATE UNIQUE INDEX IF NOT EXISTS IX_WorldCapsuleerAgent_CommunitySystemId_Career
                ON WorldCapsuleerAgent(CommunitySystemId,Career);
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
    }

    // Called inside startup/character-creation transactions. This records grants already
    // made by the legacy founders; it never provisions assets or copies paid work.
    internal static void Reconcile(WorldDbContext world, CatalogDbContext catalog)
    {
        var miner = world.Set<SavedCapsuleerCommunity>().AsNoTracking().SingleOrDefault();
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
        if (world.Set<SavedCapsuleerHauler>().AsNoTracking().SingleOrDefault() is { } hauler)
        {
            AddAgent(world, new(hauler.PilotId, system, CapsuleerCareer.Hauling, hauler.FounderShipId, 0, hauler.StartingCents));
        }
    }

    private static void AddAgent(WorldDbContext world, CapsuleerAgent agent) =>
        world.Database.ExecuteSql(
            $"""
            INSERT INTO WorldCapsuleerAgent(PilotId,CommunitySystemId,Career,FounderShipId,FounderModuleId,StartingCents)
            VALUES ({agent.PilotId},{agent.CommunitySystemId},{agent.Career},{agent.FounderShipId},
                {agent.FounderModuleId},{agent.StartingCents}) ON CONFLICT(PilotId) DO NOTHING
            """
        );

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
            .Select(a => new CapsuleerAgent(a.PilotId, a.CommunitySystemId, a.Career, a.FounderShipId, a.FounderModuleId, a.StartingCents))
            .ToArray();
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
