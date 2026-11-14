using Eden.Data.World.Inventory;
using Eden.Data.World.Mining;
using Eden.Data.World.Skills;
using Eden.Gameplay.Agents;
using Eden.Gameplay.Docking;
using Eden.Gameplay.Dogma;
using Eden.Gameplay.Flight;
using Eden.Gameplay.Inventory;
using Eden.Gameplay.Mining;
using Eden.Infrastructure.Diagnostics;
using Eden.Protocol.Exploration;
using Eden.Protocol.Flight;
using Eden.Protocol.PilotSessions;
using Eden.Protocol.Serialization;
using Eden.Protocol.Tests.Fixtures;
using Eden.World.Agents;
using Eden.World.Catalog;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Eden.Protocol.Tests.Agents;

public sealed class MiningMissionTests
{
    private const int AsteroidCatastrophe = 4804;
    private const int Augumene = 28618;
    private const int Venture = 32880;
    private const int OwnerAccount = 1000001;
    private const int ObserverAccount = 1000002;
    private static readonly MiningCollection s_cycle = new(1, true, new(.5, 0, .5, 0, .5));

    [Theory]
    [InlineData(4800, 2, 28617, 18000, 10)]
    [InlineData(13822, 3, 28627, 10, 2)]
    [InlineData(4884, 4, 28629, 425, 1)]
    public void CatalogDepositsMatchTheirObjectives(int mission, int level, int type, int quantity, int deposits)
    {
        using MissionFixture f = new(division: MissionRules.MiningDivision);
        using CatalogDbContext catalog = new(new WorldDatabase(f.Db.Path).CatalogPath);
        var profile = MissionCatalog.Find(catalog, mission);
        Assert.Equal(MissionKind.Mining, profile.Kind);
        Assert.Equal(level, profile.Definition.Level);
        Assert.Equal(deposits, profile.Deposits.Length);
        Assert.Equal(quantity, profile.CargoQuantity);
        Assert.Equal(quantity, profile.Deposits.Sum(d => d.Quantity));
        Assert.All(
            profile.Deposits,
            deposit =>
            {
                Assert.Equal(type, deposit.Contact.TypeId);
                Assert.True(deposit.Contact.Radius > 1);
                Assert.Equal(type != 28629, deposit.Contact.Solid);
            }
        );
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void QuitAndExactExpiryRemoveMissionDeposits(bool expiry)
    {
        using MissionFixture f = new(division: MissionRules.MiningDivision, live: true);
        var mission = f.Accept(f.Request());
        long deposit = MissionMiningRules.DepositId(mission.Id, 0);
        Assert.NotNull(Stock(f, deposit));
        if (expiry)
        {
            f.Clock.UtcNow = DateTimeOffset.FromFileTime(mission.ExpiresAt);
        }
        else
        {
            f.Missions.Act(MissionFixture.PilotId, mission.Agent.Id, MissionAction.Quit, mission.Id);
        }
        Assert.Empty(f.Missions.Sites(mission.SystemId));
        Assert.Null(Stock(f, deposit));
        var next = f.Accept(f.Request());
        Assert.NotEqual(mission.Id, next.Id);
        Assert.NotNull(Stock(f, MissionMiningRules.DepositId(next.Id, 0)));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void PrivateMiningPocketPersistsDepletionAndPaysOnce(bool live)
    {
        using MissionFixture f = new(
            division: MissionRules.MiningDivision,
            live: live,
            configure: catalog =>
            {
                PassiveCatalogFixture.LoadMining(catalog.Database.GetDbConnection().DataSource);
                catalog.Database.ExecuteSqlRaw("DELETE FROM SourceRecord WHERE Dataset='client/missions' AND Key!='4804'");
                catalog.Database.ExecuteSqlRaw(
                    """
                    UPDATE TypeAttribute SET Value=260 WHERE ItemTypeId=3651 AND AttributeId=77;
                    UPDATE TypeAttribute SET Value=1 WHERE ItemTypeId=3651 AND AttributeId=5967;
                    """
                );
            }
        );
        long ownerModule = Equip(f, OwnerAccount);
        long observerModule = Equip(f, ObserverAccount);
        string service = $"@agent:{MissionFixture.AgentId}";
        Assert.True(f.Missions.Information(MissionFixture.PilotId, MissionFixture.AgentId).MissionAvailable);
        var offered = f.Request();
        Assert.Equal(AsteroidCatastrophe, offered.MissionId);
        Assert.Equal(MissionKind.Mining, offered.Kind);
        Assert.Empty(f.Missions.Sites(offered.SystemId));
        var mission = f.Accept(offered);
        Assert.Equal(3600, mission.Retrieval!.Quantity);
        Assert.Equal(Augumene, mission.Retrieval.TypeId);
        var objective = BlueRead.Fields(MissionFixture.Reply(f.Call(service, "GetMissionObjectiveInfo", [])));
        Assert.Empty(Assert.IsType<List<object?>>(objective["agentGift"]));
        Assert.Single(Assert.IsType<List<object?>>(objective["dungeons"]));
        Assert.Throws<InvalidDataException>(() => Complete(f, mission));
        var site = Assert.Single(f.Missions.Sites(mission.SystemId));
        Assert.True(site.Private);
        var rock = Assert.Single(site.Objects, item => MissionMiningRules.IsDeposit(item.Id));
        var publicField = f.Services.GetRequiredService<IAsteroidField>();
        Assert.DoesNotContain(publicField.List(mission.SystemId), item => item.Id == rock.Id);

        if (live)
        {
            using var measured = SessionPerformance.Begin("warm mining mission scene", _ => { }, force: true)!;
            Assert.Same(site, Assert.Single(f.Missions.Sites(mission.SystemId)));
            Assert.DoesNotContain(publicField.List(mission.SystemId), item => item.Id == rock.Id);
            Assert.Equal(0, measured.Commands);
            Assert.Equal(0, measured.Connections);
            Assert.Equal(0, measured.FittingCalculations);
        }

        Move(f, OwnerAccount, mission, mission.Position + new SpacePosition(500_000, 0, 0));
        f.Context.Flight.Snapshot();
        Assert.IsType<PilotCallResult.Success>(f.Call(service, "WarpToLocation", ["dungeon", 0L, 0d, false, null, null]));
        Move(f, OwnerAccount, mission, rock.Position);
        Move(f, ObserverAccount, mission, rock.Position);
        var observer = f.Db.Store.FindPilot(ObserverAccount)!;
        using PilotCallContext observerContext = new(
            ObserverAccount,
            observer.Account,
            [observer],
            selected => new FlightSession(f.Db.Store, selected, f.Clock, new(), f.Db.Objects, missions: f.Missions),
            43,
            SignedSettings.Empty
        );
        observerContext.Select(observer);
        using var observerScope = f.Services.CreateScope();
        observerScope.ServiceProvider.GetRequiredService<CombatAnomalyHandler>().Advance(observerContext);
        Assert.Null(observerContext.Flight.FindBookmarkObject(rock.Id));
        f.Handler<CombatAnomalyHandler>().Advance(f.Context);
        f.Context.Flight.RefreshAsteroids(publicField.List(mission.SystemId));
        Assert.NotNull(f.Context.Flight.FindBookmarkObject(rock.Id));
        var tracker = Assert.IsType<List<object?>>(
            MissionFixture.Reply(f.Call("missionTrackerMgr", "GetMissionInfoItems", [mission.Agent.Id]))
        );
        Assert.Equal("MissionFetchMineTrigger", BlueMarshal.Text(tracker[0]));
        Assert.Equal("3600", BlueMarshal.Text(tracker[2]));

        var mining = f.Services.GetRequiredService<IPilotMining>();
        var rejection = Assert.Throws<InvalidDataException>(() => mining.Begin(observer.PilotId, observerModule, rock.Id, f.Clock.Now));
        Assert.Contains("your active mission pocket", rejection.Message);
        var first = Mine(f, ownerModule, rock.Id);
        Assert.InRange(first.Extracted, 1, 3599);
        Assert.Equal(0, first.Residue);
        Assert.Equal(0, first.CriticalBonus);
        Assert.False(first.Depleted);
        Assert.Equal((int)InventoryFlag.GeneralMiningHold, first.Cargo!.Item.FlagId);
        Assert.False(f.Missions.Current(MissionFixture.PilotId, mission.Agent.Id)!.ObjectiveComplete);

        f.StopResident();
        Assert.Equal(3600 - first.Extracted, Stock(f, rock.Id));
        Assert.Contains(Assert.Single(f.Missions.Sites(mission.SystemId)).Objects, item => item.Id == rock.Id);
        int total = first.Extracted;
        for (int cycle = 0; cycle < 8 && total < 3600; cycle++)
        {
            total += Mine(f, ownerModule, rock.Id).Extracted;
        }
        Assert.Equal(3600, total);
        Assert.DoesNotContain(Assert.Single(f.Missions.Sites(mission.SystemId)).Objects, item => item.Id == rock.Id);
        Assert.True(f.Missions.Current(MissionFixture.PilotId, mission.Agent.Id)!.ObjectiveComplete);
        f.Dock(MissionFixture.DestinationId);
        Assert.Throws<InvalidDataException>(() => Complete(f, mission));
        f.Dock(MissionFixture.StationId);
        var paid = Complete(f, mission);
        Assert.Equal(MissionState.Completed, paid.Offer!.State);
        Assert.Equal(mission.Rewards.IskCents + mission.Bonus!.IskCents, paid.BalanceCents);
        Assert.Equal(mission.Rewards.LoyaltyPoints, paid.PointsAfter);
        Assert.Equal(3600, Assert.Single(paid.Items).Previous!.Quantity);
        Assert.Throws<InvalidDataException>(() => Complete(f, mission));
        Assert.Empty(f.Missions.Sites(mission.SystemId));
        Assert.Null(Stock(f, rock.Id));
    }

    private static long Equip(MissionFixture f, long account)
    {
        var pilot = f.Db.Store.FindPilot(account)!;
        using WorldDbContext world = new(f.Db.Path);
        world.Ships.Find(pilot.ShipId)!.TypeId = Venture;
        var item = world.Set<OwnedItem>().Single(i => i.OwnerId == pilot.PilotId && i.TypeId == (int)MiningLaserType.Civilian);
        item.LocationId = pilot.ShipId;
        item.FlagId = (int)InventoryFlag.HighSlot0;
        item.Singleton = true;
        item.Online = true;
        if (world.Set<PilotSkill>().Find(pilot.PilotId, (int)MiningSkill.Mining) is null)
        {
            world.Add(
                new PilotSkill
                {
                    PilotId = pilot.PilotId,
                    TypeId = (int)MiningSkill.Mining,
                    Points = 250,
                }
            );
        }
        world.SaveChanges();
        return item.Id;
    }

    private static void Move(MissionFixture f, long account, MissionOffer mission, SpacePosition position)
    {
        f.Db.Store.SetPosition(f.Db.Store.FindPilot(account)!.ShipId, position, mission.SystemId);
        if (account == OwnerAccount)
        {
            f.Context.ChangeShip(f.Db.Store.FindPilot(account)!);
        }
    }

    private static MiningYield Mine(MissionFixture f, long module, long deposit)
    {
        var mining = f.Services.GetRequiredService<IPilotMining>();
        var start = mining.Begin(MissionFixture.PilotId, module, deposit, f.Clock.Now, DogmaEffect.MiningLaser);
        f.Clock.Milliseconds += (long)Math.Ceiling(start.Laser.DurationMilliseconds);
        return mining.Collect(MissionFixture.PilotId, module, deposit, s_cycle);
    }

    private static long? Stock(MissionFixture f, long deposit)
    {
        using WorldDbContext world = new(f.Db.Path);
        return world.Set<WorldAsteroid>().Find(deposit)?.Quantity;
    }

    private static MissionUpdate Complete(MissionFixture f, MissionOffer mission) =>
        f.Missions.Act(MissionFixture.PilotId, mission.Agent.Id, MissionAction.Complete, mission.Id);
}
