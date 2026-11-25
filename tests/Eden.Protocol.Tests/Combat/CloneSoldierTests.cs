using Eden.Data.World.Combat.Npcs;
using Eden.Gameplay.Characters;
using Eden.Gameplay.Combat;
using Eden.Gameplay.Flight;
using Eden.Gameplay.Loot;
using Eden.Protocol.Tests.Persistence;
using Eden.Server;
using Eden.World.Catalog;
using Eden.World.Combat.Npcs;
using Eden.World.Runtime;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Eden.Protocol.Tests.Combat;

public sealed class CloneSoldierTests
{
    private const int System = 30000142;
    private const int Belt = 40200000;

    [Theory]
    [InlineData(NpcPirateFaction.Angels, 33132, 33143, 33173, 33175)]
    [InlineData(NpcPirateFaction.BloodRaiders, 33135, 33144, 33172, 33182)]
    [InlineData(NpcPirateFaction.Guristas, 33134, 33145, 33174, 33189)]
    [InlineData(NpcPirateFaction.Sansha, 33136, 33142, 33163, 33183)]
    [InlineData(NpcPirateFaction.Serpentis, 33137, 33146, 33164, 33185)]
    public void EveryFactionUsesThePinnedHullAndTagForEachLowSecurityTier(
        NpcPirateFaction faction,
        int trainer,
        int recruiter,
        int transporter,
        int negotiator
    )
    {
        AssertTier(0.449, trainer, SecurityTagType.CloneSoldierTrainer);
        AssertTier(0.349, recruiter, SecurityTagType.CloneSoldierRecruiter);
        AssertTier(0.249, transporter, SecurityTagType.CloneSoldierTransporter);
        AssertTier(0.001, negotiator, SecurityTagType.CloneSoldierNegotiator);
        Assert.Null(NpcCloneSoldierRules.Select(faction, 0.45, 0));
        Assert.Null(NpcCloneSoldierRules.Select(faction, 0, 0));
        Assert.Null(NpcCloneSoldierRules.Select(faction, -0.2, 0));
        Assert.Null(NpcCloneSoldierRules.Select(faction, 0.2, 2));
        Assert.Null(NpcCloneSoldierRules.Select(NpcPirateFaction.RogueDrones, 0.2, 0));

        void AssertTier(double security, int hull, SecurityTagType tag)
        {
            Assert.Equal((NpcCloneSoldierType)hull, NpcCloneSoldierRules.Select(faction, security, 0));
            Assert.Equal(tag, NpcCloneSoldierRules.TagFor(hull));
        }
    }

    [Fact]
    public async Task ResidentCloneSoldierWreckCarriesOneSecurityTagAcrossCheckpoint()
    {
        const NpcPirateFaction faction = NpcPirateFaction.Guristas;
        const double security = 0.2;
        const NpcCloneSoldierType type = NpcCloneSoldierType.GuristasTransporter;
        var clock = CalendarTestData.Clock();
        long now = clock.Now;
        using TestWorld db = new(clock: clock, regionalNpcs: true, liveWorld: true);
        RegionalNpcTests.Prepare(db, security);
        WorldDatabase database = new(db.Path);
        AddTagTypes(database);
        var profiles = db.Services.GetRequiredService<NpcSpawnCatalog>();
        Assert.All(Enum.GetValues<NpcCloneSoldierType>(), hull => Assert.NotNull(profiles.CloneSoldier(hull)));
        Assert.DoesNotContain(profiles.Profiles, profile => NpcCloneSoldierRules.TagFor(profile.TypeId).HasValue);
        VerifyElectronicWarfare(database);

        var live = db.Services.GetRequiredService<LiveWorld>();
        int generation = Enumerable
            .Range(0, 1000)
            .First(g => NpcCloneSoldierRules.Select(faction, security, NpcSpawnRules.Seed(System, Belt, g)) == type);
        using (var action = live.Economy.Begin(new(systems: [System])))
        {
            action.Add(
                new WorldNpcEncounter
                {
                    Id = Belt,
                    SystemId = System,
                    AnchorId = Belt,
                    AnchorKind = NpcAnchorKind.Belt,
                    Role = NpcRole.Pirate,
                    FactionId = (int)faction,
                    HomeX = 10_000_000_000 + NpcSpawnRules.BeltStandOff,
                    Occupied = true,
                    Version = NpcSpawnRules.PopulationVersion,
                    Generation = generation,
                    NextSpawnAt = now,
                }
            );
            action.Commit();
        }

        var encounters = db.Services.GetRequiredService<INpcEncounters>();
        var spawned = Assert.Single(encounters.List(System, now), npc => npc.AnchorId == Belt);
        Assert.Equal((int)type, spawned.TypeId);
        Assert.Equal(NpcAnchorKind.CloneSoldierBelt, spawned.AnchorKind);
        Assert.Equal(spawned.Id, Assert.Single(encounters.List(System, now + TimeSpan.TicksPerSecond), npc => npc.AnchorId == Belt).Id);
        var pilot = db.Store.FindPilot(1000001)!;
        db.Store.SetPosition(pilot.ShipId, spawned.Position + new SpacePosition(1000, 0, 0), System);
        using (var action = live.Economy.Begin(new(pilots: [pilot.PilotId], systems: [System])))
        using (CatalogDbContext catalog = new(database.CatalogPath))
        {
            var npc = action.Set<WorldNpc>().Find(spawned.Id)!;
            var attributes = NpcProfileCatalog.ReadAttributes(catalog, npc.TypeId);
            var result = NpcDamagePersistence.Apply(
                action,
                catalog,
                npc,
                action.Pilots.Find(pilot.PilotId)!,
                pilot.ShipId,
                attributes,
                new(1_000_000_000, 0, 0, 0),
                now + TimeSpan.TicksPerSecond
            );
            Assert.True(result.Destroyed);
            Assert.Throws<InvalidDataException>(() =>
                NpcDamagePersistence.Apply(
                    action,
                    catalog,
                    npc,
                    action.Pilots.Find(pilot.PilotId)!,
                    pilot.ShipId,
                    attributes,
                    new(1_000_000_000, 0, 0, 0),
                    now + TimeSpan.TicksPerSecond
                )
            );
            action.Commit();
        }

        long due = now + (1 + NpcEncounterDefinitions.RespawnSeconds) * TimeSpan.TicksPerSecond;
        var wrecks = db.Services.GetRequiredService<IWrecks>();
        var wreck = Assert.Single(wrecks.List(System, now + TimeSpan.TicksPerSecond));
        var tag = Assert.Single(wrecks.Read(pilot.PilotId, wreck.Id, now + TimeSpan.TicksPerSecond).Items);
        Assert.Equal((int)SecurityTagType.CloneSoldierTransporter, tag.TypeId);
        Assert.Equal(1, tag.Quantity);
        Assert.Equal(pilot.PilotId, wreck.OwnerId);
        Assert.DoesNotContain(encounters.List(System, due - 1), npc => npc.AnchorId == Belt);

        await live.FlushAsync();
        db.Services.Dispose();
        using var reopened = ServerServices.Build(database, clock, liveWorld: true);
        var restored = reopened.GetRequiredService<IWrecks>();
        Assert.Equal(tag, Assert.Single(restored.Read(pilot.PilotId, wreck.Id, due).Items));
        var transfer = Assert.Single(restored.Take(pilot.PilotId, pilot.ShipId, wreck.Id, [tag.Id], null, due));
        Assert.Equal(pilot.ShipId, transfer.Item.LocationId);
        Assert.Empty(restored.Read(pilot.PilotId, wreck.Id, due).Items);
        var replacements = reopened.GetRequiredService<INpcEncounters>().List(System, due).Where(npc => npc.AnchorId == Belt).ToArray();
        Assert.NotEmpty(replacements);
        Assert.DoesNotContain(replacements, npc => npc.Id == spawned.Id);
    }

    private static void VerifyElectronicWarfare(WorldDatabase database)
    {
        using CatalogDbContext catalog = new(database.CatalogPath);
        NpcProfileCatalog profiles = new();
        foreach (var hull in Enum.GetValues<NpcCloneSoldierType>())
        {
            var abilities = profiles.Read(catalog, (int)hull).ElectronicWarfare;
            switch (NpcCloneSoldierRules.TagFor((int)hull))
            {
                case SecurityTagType.CloneSoldierTransporter:
                    AssertAbility(Assert.Single(abilities), NpcEwarKind.Webifying, 10_000, 5_000);
                    Assert.Equal(-60, abilities[0].Power.WebPercent);
                    break;
                case SecurityTagType.CloneSoldierNegotiator:
                    AssertAbility(Assert.Single(abilities), NpcEwarKind.WarpDisruption, 24_000, 10_000);
                    Assert.Equal(1, abilities[0].Power.WarpStrength);
                    break;
                default:
                    Assert.Empty(abilities);
                    break;
            }
        }

        static void AssertAbility(NpcEwarAbility ability, NpcEwarKind kind, double range, double duration)
        {
            Assert.Equal(kind, ability.Kind);
            Assert.Equal(range, ability.Optimal);
            Assert.Equal(duration, ability.DurationMilliseconds);
            Assert.Equal(1, ability.Chance);
            Assert.Equal(1, NpcEwarRules.RangeFactor(ability, range));
            Assert.Equal(0, NpcEwarRules.RangeFactor(ability, range + 1));
        }
    }

    private static void AddTagTypes(WorldDatabase database)
    {
        using CatalogDbContext catalog = new(database.CatalogPath, writable: true);
        catalog.Database.ExecuteSqlRaw(
            """
            INSERT OR IGNORE INTO ItemCategory(Id,Name) VALUES (17,'Commodity');
            INSERT OR IGNORE INTO ItemGroup(Id,Name,CategoryId) VALUES (1206,'Security Tags',17);
            INSERT INTO ItemType(Id,Name,GroupId) VALUES
                (33138,'Clone Soldier Trainer Tag',1206),(33139,'Clone Soldier Recruiter Tag',1206),
                (33140,'Clone Soldier Transporter Tag',1206),(33141,'Clone Soldier Negotiator Tag',1206);
            INSERT INTO SourceRecord(Dataset,Key,Data) VALUES
                ('client/types','33138','{{"volume":0.1,"published":1}}'),('client/types','33139','{{"volume":0.1,"published":1}}'),
                ('client/types','33140','{{"volume":0.1,"published":1}}'),('client/types','33141','{{"volume":0.1,"published":1}}');
            """
        );
    }
}
