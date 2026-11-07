using Eden.Gameplay.Capsuleers;
using Eden.Gameplay.Characters;
using Eden.Gameplay.Docking;
using Eden.Infrastructure.Registration;
using Eden.World.Catalog;
using Eden.World.Characters;
using Eden.World.Market;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;

namespace Eden.World.Capsuleers;

internal sealed class SavedCapsuleerCommunity
{
    public int Id { get; set; }
    public int Seed { get; set; }
    public int SchoolId { get; set; }
    public CapsuleerCareer Career { get; set; }
    public long? PilotId { get; set; }
    public string? Progress { get; set; }
}

[Injectable]
internal sealed class CapsuleerCommunityStore(WorldDatabase database) : ICapsuleerCommunity
{
    // Build 3662168: school 17 starts a Caldari pilot in Kisogo with a racial Ibis.
    internal const int School = 17;
    private const int CaldariRace = 1;
    private const int DeteisBloodline = 1;

    // Launcher accounts are positive. This world-owned account has no login or human character slots.
    private const long MinerAccountId = -1;

    internal static void Configure(ModelBuilder model)
    {
        var community = model.Entity<SavedCapsuleerCommunity>();
        community.ToTable("WorldCapsuleerCommunity").HasKey(c => c.Id);
        community.HasOne<Pilot>().WithMany().HasForeignKey(c => c.PilotId);
    }

    internal static void Initialize(WorldDbContext world, int? seed)
    {
        world.Database.ExecuteSqlRaw(
            """
            CREATE TABLE IF NOT EXISTS WorldCapsuleerCommunity(
                Id INTEGER PRIMARY KEY CHECK(Id=1), Seed INTEGER NOT NULL, SchoolId INTEGER NOT NULL,
                Career INTEGER NOT NULL, PilotId INTEGER REFERENCES WorldPilot(Id), Progress TEXT);
            """
        );
        if (seed is int value)
        {
            world.Add(
                new SavedCapsuleerCommunity
                {
                    Id = 1,
                    Seed = value,
                    SchoolId = School,
                    Career = CapsuleerCareer.Mining,
                }
            );
            world.SaveChanges();
        }
    }

    internal static void Provision(WorldDbContext world, CatalogDbContext catalog, TimeProvider clock)
    {
        if (world.Set<SavedCapsuleerCommunity>().SingleOrDefault() is not { PilotId: null } community)
        {
            return;
        }
        world.Add(
            new Account
            {
                Id = MinerAccountId,
                UserName = "Capsuleer Community",
                CreatedAt = clock.GetUtcNow().UtcDateTime.ToFileTimeUtc(),
                CloneGrade = AccountCloneGrade.Omega,
            }
        );
        world.SaveChanges();
        // Normal character creation grants the racial corvette, civilian kit and starting skills.
        string[] given = ["Ari", "Kira", "Mika", "Ren"];
        string[] family = ["Koronen", "Auvinen", "Vartio", "Saarinen"];
        var random = new Random(community.Seed);
        string name = $"{given[random.Next(given.Length)]} {family[random.Next(family.Length)]}";
        var character = new NewCharacter(
            name,
            CaldariRace,
            DeteisBloodline,
            random.Next(2),
            CharacterRules.UnspecifiedAncestry,
            null,
            null,
            community.SchoolId
        );
        long id = CharacterCreation.Create(world, catalog, clock, MinerAccountId, character);
        community.PilotId = id;
        world.Add(new PilotWallet { PilotId = id });
        world.SaveChanges();
    }

    public IReadOnlyList<CapsuleerIdentity> Pilots()
    {
        using WorldDbContext world = new(database.Path);
        return world
            .Set<SavedCapsuleerCommunity>()
            .Where(c => c.PilotId != null)
            .Select(c => new CapsuleerIdentity(c.PilotId!.Value, c.Seed, c.SchoolId, c.Career))
            .ToArray();
    }
}
