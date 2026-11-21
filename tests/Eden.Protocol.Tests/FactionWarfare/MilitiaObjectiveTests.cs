using Eden.Data.World.Characters;
using Eden.Data.World.Combat.Npcs;
using Eden.Data.World.FactionWarfare;
using Eden.Data.World.Inventory;
using Eden.Data.World.Market;
using Eden.Data.World.Skills;
using Eden.Gameplay.Combat;
using Eden.Gameplay.FactionWarfare;
using Eden.Gameplay.Flight;
using Eden.Gameplay.Inventory;
using Eden.Server;
using Eden.World.Catalog;
using Eden.World.Combat.Npcs;
using Eden.World.FactionWarfare;
using Eden.World.Inventory;
using Eden.World.Runtime;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Eden.Protocol.Tests.FactionWarfare;

public sealed class MilitiaObjectiveTests
{
    private const int Frontline = 30002087;
    private const int AmarrPropaganda = 73225;
    private const int MinmatarDepot = 72851;
    private const long Account = 1000001;

    [Fact]
    public async Task DeployConsumesOneItemOnlyAfterEligibilityAndExpiresAcrossRestart()
    {
        TestClock clock = new();
        using TestWorld fixture = new(clock: clock, liveWorld: true);
        WorldDatabase database = new(fixture.Path);
        Prepare(database);
        var pilot = fixture.Store.FindPilot(Account)!;
        fixture.Store.SetPosition(pilot.ShipId, new(2e11, 3e11, 4e11), Frontline);
        var live = fixture.Services.GetRequiredService<LiveWorld>();
        var deployments = fixture.Services.GetRequiredService<IMilitiaObjectiveDeployment>();
        long itemId;
        using (var action = live.Economy.Begin(TransactionParticipants.None))
        {
            action.Add(new SavedMilitiaEnlistment { PilotId = pilot.PilotId, DirectFaction = MilitiaFaction.Amarr });
            using CatalogDbContext catalog = new(database.CatalogPath);
            var item = InventoryPersistence.MergeOutput(
                action,
                catalog,
                pilot.PilotId,
                pilot.ShipId,
                (int)InventoryFlag.Cargo,
                AmarrPropaganda,
                2
            );
            action.SaveChanges();
            itemId = item.Item.Id;
            action.Commit();
        }
        Assert.Throws<InvalidDataException>(() => deployments.Deploy(pilot.PilotId, pilot.ShipId, [itemId], clock.Now));
        Assert.Equal(2, live.Economy.Inspect(world => world.Set<OwnedItem>().Find(itemId)!.Quantity));
        using (var action = live.Economy.Begin(TransactionParticipants.None))
        {
            action.Add(
                new PilotSkill
                {
                    PilotId = pilot.PilotId,
                    TypeId = MilitiaObjectiveRules.AnchoringSkill,
                    Points = 8_000,
                }
            );
            action.Commit();
        }

        var drop = Assert.Single(deployments.Deploy(pilot.PilotId, pilot.ShipId, [itemId], clock.Now));
        Assert.Equal(itemId, drop.ItemId);
        Assert.Equal(1, drop.Change.Item.Quantity);
        Assert.Throws<InvalidDataException>(() => deployments.Deploy(pilot.PilotId, pilot.ShipId, [itemId], clock.Now));
        Assert.Equal(1, live.Economy.Inspect(world => world.Set<OwnedItem>().Find(itemId)!.Quantity));
        Assert.Equal(
            clock.Now + MilitiaObjectiveRules.DeploymentActivation.Ticks,
            live.Economy.Inspect(world => world.Set<SavedMilitiaObjective>().Find(drop.ObjectId)!.ReadyAt)
        );
        await live.FlushAsync();
        fixture.Services.Dispose();

        using var reopened = ServerServices.Build(database, clock, liveWorld: true);
        live = reopened.GetRequiredService<LiveWorld>();
        Assert.Equal(1, live.Economy.Inspect(world => world.Set<OwnedItem>().Find(itemId)!.Quantity));
        var objectives = reopened.GetRequiredService<MilitiaObjectiveStore>();
        objectives.Advance(Frontline, [], clock.Now + MilitiaObjectiveRules.DeploymentExpiry.Ticks);
        Assert.Null(live.Economy.Inspect(world => world.Set<SavedMilitiaObjective>().Find(drop.ObjectId)));
        Assert.NotNull(live.Economy.Inspect(world => world.Set<WorldNpc>().Find(drop.ObjectId)!.RetiredAt));
    }

    [Fact]
    public void SupplyLossAndPayoutSettleOnceFromResidentDamage()
    {
        TestClock clock = new();
        using TestWorld fixture = new(clock: clock, liveWorld: true);
        WorldDatabase database = new(fixture.Path);
        Prepare(database);
        long slot = MilitiaObjectiveRules.Slot(Frontline, MilitiaObjectiveKind.SupplyDepot, MilitiaFaction.Minmatar);
        var position = MilitiaObjectiveRules.Grid(slot);
        var candidate = fixture.Store.FindPilot(Account)!;
        fixture.Store.SetPosition(candidate.ShipId, position, Frontline);
        var live = fixture.Services.GetRequiredService<LiveWorld>();
        long npc;
        using (var action = live.Economy.Begin(TransactionParticipants.None))
        {
            var target = action
                .Add(
                    new WorldNpc
                    {
                        SystemId = Frontline,
                        TypeId = MinmatarDepot,
                        Role = NpcRole.MilitiaObjective,
                        AnchorKind = NpcAnchorKind.MilitiaObjective,
                        EncounterId = slot,
                        FactionId = (int)MilitiaFaction.Minmatar,
                        CorporationId = MilitiaRules.TribalLiberationForce,
                        X = position.X,
                        Y = position.Y,
                        Z = position.Z,
                        HomeX = position.X,
                        HomeY = position.Y,
                        HomeZ = position.Z,
                        ShieldCharge = 1_000,
                        UpdatedAt = clock.Now,
                    }
                )
                .Entity;
            action.SaveChanges();
            npc = target.Id;
            action.Add(
                new SavedMilitiaObjective
                {
                    Id = slot,
                    SystemId = Frontline,
                    Kind = MilitiaObjectiveKind.SupplyDepot,
                    Faction = MilitiaFaction.Minmatar,
                    TargetId = npc,
                }
            );
            MilitiaAdvantageStore.Apply(action, FactionWarzoneStore.System(action, Frontline)!, MilitiaFaction.Minmatar, 10_000, clock.Now);
            action.Commit();
        }
        Assert.Throws<InvalidDataException>(Strike);
        using (var action = live.Economy.Begin(TransactionParticipants.None))
        {
            action.Add(new SavedMilitiaEnlistment { PilotId = candidate.PilotId, DirectFaction = MilitiaFaction.Amarr });
            action.Commit();
        }
        Strike();
        Assert.Throws<InvalidDataException>(Strike);
        var zone = fixture.Services.GetRequiredService<IFactionWarzones>().Advantage(Frontline, clock.Now)!;
        Assert.Equal(8_000, zone.Second.Contribution);
        Assert.Equal(
            15_000L,
            live.Economy.Inspect(world => world.Set<SavedLoyaltyBalance>().Find(candidate.PilotId, MilitiaRules.ImperialCrusade)!.Points)
        );
        Assert.Equal(
            clock.Now + MilitiaObjectiveRules.SupplyRespawn.Ticks,
            live.Economy.Inspect(world => world.Set<SavedMilitiaObjective>().Find(slot)!.ReplaceAt)
        );

        void Strike()
        {
            using var action = live.Economy.Begin(new(pilots: [candidate.PilotId], resources: [new(SharedSimulationResource.Npc, npc)]));
            using CatalogDbContext catalog = new(database.CatalogPath);
            var target = action.Set<WorldNpc>().Find(npc)!;
            var attacking = action.Set<Pilot>().Find(candidate.PilotId)!;
            var result = NpcDamagePersistence.Apply(
                action,
                catalog,
                target,
                attacking,
                candidate.ShipId,
                catalog.FittingAttributes(target.TypeId),
                new(50_000, 0, 0, 0),
                clock.Now
            );
            Assert.True(result.Destroyed);
            action.Commit();
        }
    }

    private static void Prepare(WorldDatabase database)
    {
        MilitiaComplexTests.LoadComplexCatalog(database);
        using CatalogDbContext catalog = new(database.CatalogPath, writable: true);
        catalog.Database.ExecuteSqlRaw(
            """
            INSERT OR IGNORE INTO ItemCategory(Id,Name) VALUES (22,'Deployable');
            INSERT OR IGNORE INTO ItemGroup(Id,Name,CategoryId) VALUES (4477,'Militia deployable',22),(319,'Militia depot',11);
            INSERT OR IGNORE INTO ItemType(Id,Name,GroupId,Radius) VALUES
              (73225,'Amarr Propaganda Broadcast Structure',4477,100),(72851,'Minmatar Supply Depot',319,100);
            INSERT OR IGNORE INTO DogmaAttribute(Id,Name) VALUES (9,'hp'),(263,'shieldCapacity'),(265,'armorHP'),(479,'shieldRechargeRate');
            INSERT OR IGNORE INTO TypeAttribute(ItemTypeId,AttributeId,Value) VALUES
              (73225,9,1000),(73225,263,1000),(73225,265,1000),(73225,479,100000),
              (72851,9,1000),(72851,263,1000),(72851,265,1000),(72851,479,100000);
            """
        );
        const string deployable = """{"volume":1}""";
        catalog.Database.ExecuteSql($"INSERT INTO SourceRecord(Dataset,Key,Data) VALUES ('client/types',{AmarrPropaganda},{deployable})");
    }
}
