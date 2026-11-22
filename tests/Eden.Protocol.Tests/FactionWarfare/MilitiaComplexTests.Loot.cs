using Eden.Data.World.Combat.Npcs;
using Eden.Data.World.Inventory;
using Eden.Gameplay.Combat;
using Eden.Gameplay.Dogma;
using Eden.Gameplay.FactionWarfare;
using Eden.Gameplay.Flight;
using Eden.Gameplay.Loot;
using Eden.World.Catalog;
using Eden.World.Combat.Npcs;
using Eden.World.Runtime;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Eden.Protocol.Tests.FactionWarfare;

public sealed partial class MilitiaComplexTests
{
    [Theory]
    [InlineData(MilitiaFaction.Amarr, 75351, 27052, 15610, 15992, 16000)]
    [InlineData(MilitiaFaction.Caldari, 75356, 27049, 15599, 15600, 15601)]
    [InlineData(MilitiaFaction.Gallente, 75361, 27055, 15590, 15994, 16002)]
    [InlineData(MilitiaFaction.Minmatar, 75366, 27043, 15628, 15629, 15660)]
    public async Task ScoutAndSmallDefendersLeaveOneUsableTagInAnOwnedWreck(
        MilitiaFaction faction,
        int frigate,
        int wreckType,
        int firstTag,
        int secondTag,
        int thirdTag
    )
    {
        TestClock clock = new();
        using TestWorld fixture = new(clock: clock, liveWorld: true);
        WorldDatabase database = new(fixture.Path);
        LoadComplexCatalog(database);
        using (CatalogDbContext edit = new(database.CatalogPath, writable: true))
        {
            edit.Database.ExecuteSql(
                $"""
                INSERT OR IGNORE INTO ItemCategory VALUES (17,'Commodity');
                INSERT OR IGNORE INTO ItemGroup VALUES (409,'Empire Insignia Drops',17),(186,'Wreck',2);
                INSERT OR IGNORE INTO ItemType(Id,Name,GroupId,Radius) VALUES
                    ({frigate},'Militia frigate',1186,100),({frigate + 1},'Militia destroyer',1186,100),
                    ({wreckType},'Small militia wreck',186,100),
                    ({firstTag},'Militia tag I',409,0),({secondTag},'Militia tag II',409,0),({thirdTag},'Militia tag III',409,0);
                """
            );
            const string wreckDefinition = """{"volume":27500,"capacity":27500,"mass":10000}""";
            edit.Database.ExecuteSql($"INSERT OR REPLACE INTO SourceRecord VALUES ('client/types',{wreckType},{wreckDefinition})");
            const string tagDefinition = """{"volume":0.1}""";
            foreach (int tag in new[] { firstTag, secondTag, thirdTag })
            {
                edit.Database.ExecuteSql($"INSERT OR REPLACE INTO SourceRecord VALUES ('client/types',{tag},{tagDefinition})");
            }
        }
        using CatalogDbContext catalog = new(database.CatalogPath);
        var pilot = fixture.Store.FindPilot(FirstAccount)!;
        var position = new SpacePosition(12_000, 30_000, 80_000);
        fixture.Store.SetPosition(pilot.ShipId, position, SystemId);
        var live = fixture.Services.GetRequiredService<LiveWorld>();
        var wrecks = fixture.Services.GetRequiredService<IWrecks>();
        long[] tags = new long[2];
        for (int index = 0; index < 2; index++)
        {
            var family = index == 0 ? MilitiaComplexFamily.Scout : MilitiaComplexFamily.Small;
            long npcId;
            using (var world = live.Economy.Begin(new(systems: [SystemId], pilots: [pilot.PilotId])))
            {
                var npc = world
                    .Add(
                        new WorldNpc
                        {
                            TypeId = frigate + index,
                            SystemId = SystemId,
                            EncounterId = MilitiaComplexRules.SiteId(MilitiaComplexRules.Slot(SystemId, family), 0),
                            Role = NpcRole.MilitiaComplexDefender,
                            AnchorKind = NpcAnchorKind.MilitiaComplex,
                            FactionId = (int)faction,
                            CorporationId = MilitiaRules.Corporation(faction),
                            X = position.X,
                            Y = position.Y,
                            Z = position.Z,
                            UpdatedAt = clock.Now,
                        }
                    )
                    .Entity;
                npcId = npc.Id;
                var result = NpcDamagePersistence.Apply(
                    world,
                    catalog,
                    npc,
                    world.Pilots.Find(pilot.PilotId)!,
                    pilot.ShipId,
                    new Dictionary<DogmaAttributeId, double> { [DogmaAttributeId.StructureHitPoints] = 10 },
                    new(100, 0, 0, 0),
                    clock.Now,
                    bypassResistances: true
                );
                Assert.True(result.Destroyed);
                Assert.Null(result.Reward);
                world.Commit();
            }
            var wreck = Assert.Single(wrecks.List(SystemId, clock.Now), contact => contact.SourceId == npcId);
            Assert.Equal(wreckType, wreck.TypeId);
            var item = Assert.Single(wrecks.Read(pilot.PilotId, wreck.Id, clock.Now).Items);
            Assert.Equal(1, item.Quantity);
            Assert.Contains(item.TypeId, new[] { firstTag, secondTag, thirdTag });
            tags[index] = item.Id;
            Assert.Single(wrecks.Take(pilot.PilotId, pilot.ShipId, wreck.Id, [item.Id], null, clock.Now));
            Assert.Empty(wrecks.Read(pilot.PilotId, wreck.Id, clock.Now).Items);
        }
        await live.FlushAsync();
        using var file = WorldConnections.FileConnection(fixture.Path);
        using WorldDbContext saved = new(file);
        Assert.Equal(2, saved.Set<OwnedItem>().Count(item => tags.Contains(item.Id) && item.LocationId == pilot.ShipId));
    }
}
