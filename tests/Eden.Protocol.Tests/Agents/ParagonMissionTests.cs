using Eden.Data.World.Agents;
using Eden.Data.World.Corporations;
using Eden.Data.World.Inventory;
using Eden.Data.World.Market;
using Eden.Data.World.Skills;
using Eden.Gameplay.Agents;
using Eden.Gameplay.Corporations;
using Eden.Gameplay.Docking;
using Eden.Gameplay.Dogma;
using Eden.Gameplay.Inventory;
using Eden.Gameplay.Redemption;
using Eden.Protocol.Agents;
using Eden.Protocol.Market;
using Eden.Protocol.PilotSessions;
using Eden.Protocol.Serialization;
using Eden.Protocol.Tests.PilotSessions;
using Eden.Server;
using Eden.World.Agents;
using Eden.World.Catalog;
using Eden.World.Runtime;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Eden.Protocol.Tests.Agents;

public sealed class ParagonMissionTests
{
    private const long Pilot = MissionFixture.PilotId;
    private const long Iris = 3020032;
    private const long OtherIris = 3020034;
    private const int Station = 60015168;
    private const int OtherStation = 60015169;
    private const int Issuer = CurrencyRewardRules.ParagonCorporation;
    private const int CorporationManagement = 3363;
    private const int FirstHull = 587;

    [Theory]
    [InlineData(AccountCloneGrade.Alpha, 250)]
    [InlineData(AccountCloneGrade.Omega, 2500)]
    public async Task PackagedHullPaysOnceAndStartsSharedCooldownAcrossCheckpoint(AccountCloneGrade grade, long points)
    {
        using var f = Create();
        var live = LiveWorld.Find(f.Db.Path)!;
        int? corporation = null;
        using (var action = live.Economy.Begin(new(pilots: [Pilot])))
        {
            var pilot = action.Pilots.Find(Pilot)!;
            pilot.Account.CloneGrade = grade;
            var wallet = action.Set<PilotWallet>().Find(Pilot) ?? action.Add(new PilotWallet { PilotId = Pilot }).Entity;
            wallet.Cents = 1_000_000_000;
            var skill =
                action.Set<PilotSkill>().Find(Pilot, CorporationManagement)
                ?? action.Add(new PilotSkill { PilotId = Pilot, TypeId = CorporationManagement }).Entity;
            skill.Points = 250;
            action.Commit();
        }
        if (grade == AccountCloneGrade.Omega)
        {
            corporation = f
                .Services.GetRequiredService<IPlayerCorporations>()
                .Create(Pilot, PlayerCorporationTests.Details())
                .Corporation.Id;
        }
        long isk = live.WalletBalance(Pilot);
        Dock(live, OtherStation);
        var other = f.Accept(f.Request(OtherIris));
        Assert.Throws<InvalidDataException>(() => f.Request(Iris));
        Dock(live, Station);
        string service = $"@agent:{Iris}";
        Assert.True(f.Missions.Information(Pilot, Iris).MissionAvailable);
        var offer = f.Request(Iris);
        Assert.Equal(17013, offer.MissionId);
        Assert.Equal(points, offer.Rewards.LoyaltyPoints);
        Assert.Equal(FirstHull, offer.Retrieval!.TypeId);
        Assert.Equal(2500, offer.Retrieval.Volume);
        Assert.Equal(TimeSpan.TicksPerDay, offer.ExpiresAt - offer.OfferedAt);
        var accepted = f.Missions.Act(Pilot, Iris, MissionAction.Accept, offer.Id);
        Assert.Empty(accepted.Items);
        var objectives = BlueRead.Fields(MissionFixture.Reply(f.Call(service, "GetMissionObjectiveInfo", [])));
        Assert.Equal(points, objectives["loyaltyPoints"]);
        Assert.Empty(Assert.IsType<List<object?>>(objectives["normalRewards"]));
        Assert.Empty(Assert.IsType<List<object?>>(objectives["agentGift"]));
        var objective = Assert.Single(Assert.IsType<List<object?>>(objectives["objectives"]));
        Assert.Equal("fetch", BlueMarshal.Text(BlueRead.Tuple(objective, 2)[0]));
        Assert.Throws<InvalidDataException>(() => f.Missions.CompleteWithFleet(Pilot, Iris, offer.Id));
        long assembled = AddHull(live, Station, 1, singleton: true);
        long remote = AddHull(live, OtherStation, 1);
        long complete = offer.Id * MissionRules.ActionStride + (int)MissionAction.Complete;
        Assert.IsType<PilotCallResult.Failure>(f.Call(service, "DoAction", [complete]));
        long packaged = AddHull(live, Station, 2);
        var completion = BlueRead.Tuple(MissionFixture.Reply(f.Call(service, "DoAction", [complete])), 2);
        Assert.Equal(TimeSpan.FromHours(23).Ticks, BlueRead.Fields(completion[1])["missionCantReplay"]);
        Assert.IsType<PilotCallResult.Failure>(f.Call(service, "DoAction", [complete]));
        Assert.Contains(
            f.Context.Notifications,
            n => n is LoyaltyBalanceChanged { IssuerCorporationId: Issuer } change && change.After == points
        );
        Assert.Contains(f.Context.Notifications, n => n is AgentMissionChanged { Action: "offer_removed", AgentId: OtherIris });
        Assert.Equal(isk, live.WalletBalance(Pilot));
        Assert.Empty(f.Missions.Journal(Pilot));
        Assert.Equal(MissionState.Failed, f.Missions.Current(Pilot, OtherIris)!.State);
        Assert.Throws<InvalidDataException>(() => f.Missions.Act(Pilot, OtherIris, MissionAction.Complete, other.Id));
        long deadline = checked(f.Clock.Now + TimeSpan.FromHours(23).Ticks);
        Assert.Equal(deadline, MissionFixture.Reply(f.Call(service, "GetReplayTimestamp", [Pilot])));
        Assert.Equal(deadline, f.Missions.Information(Pilot, OtherIris).ReplayAt);
        var cooldown = BlueRead.Tuple(MissionFixture.Reply(f.Call(service, "DoAction", [null])), 2);
        Assert.Empty(Assert.IsType<List<object?>>(BlueRead.Tuple(cooldown[0], 2)[1]));
        Assert.Equal(TimeSpan.FromHours(23).Ticks, BlueRead.Fields(cooldown[1])["missionCantReplay"]);
        await live.FlushAsync();
        f.StopResident();

        using var reopened = ServerServices.Build(new(f.Db.Path), f.Clock, liveWorld: true);
        var missions = reopened.GetRequiredService<IPilotMissions>();
        live = LiveWorld.Find(f.Db.Path)!;
        live.Economy.Inspect(world =>
        {
            Assert.Equal(points, world.Set<SavedLoyaltyBalance>().Find(Pilot, Issuer)!.Points);
            Assert.Equal(1, world.Set<OwnedItem>().Find(packaged)!.Quantity);
            Assert.True(world.Set<OwnedItem>().Find(assembled)!.Singleton);
            Assert.Equal(OtherStation, world.Set<OwnedItem>().Find(remote)!.LocationId);
            Assert.Empty(world.Filter<SavedMarketPayment>(p => p.OrderId == offer.Id));
            Assert.Empty(world.Filter<SavedMissionProgress>(p => p.PilotId == Pilot));
            if (corporation is int id)
            {
                Assert.Equal(points, world.Set<SavedCorporationLoyaltyBalance>().Find(id, Issuer)!.Points);
            }
            return true;
        });
        f.Clock.AdvanceWallTime(TimeSpan.FromTicks(deadline - f.Clock.Now - 1));
        Dock(live, OtherStation);
        Assert.Throws<InvalidDataException>(() => missions.Act(Pilot, OtherIris, MissionAction.Request));
        f.Clock.AdvanceWallTime(TimeSpan.FromTicks(1));
        Assert.Null(missions.Information(Pilot, OtherIris).ReplayAt);
        Assert.Equal(MissionState.Offered, missions.Act(Pilot, OtherIris, MissionAction.Request).Offer!.State);
    }

    [Fact]
    public void EveryVerifiedHullCanBeOfferedAndDeclinedWithoutCooldown()
    {
        using var f = Create();
        var live = LiveWorld.Find(f.Db.Path)!;
        Dock(live, Station);
        using CatalogDbContext catalog = new(new WorldDatabase(f.Db.Path).CatalogPath);
        var expected = ParagonMissionCatalog.Read(catalog);
        Assert.Equal(41, expected.Count);
        HashSet<int> seen = [];
        foreach (int mission in expected.Keys.Order())
        {
            var offer = f.Request(Iris);
            Assert.Equal(mission, offer.MissionId);
            Assert.Equal(expected[mission].CourierMission!.ObjectiveTypeID, offer.Retrieval!.TypeId);
            Assert.True(seen.Add(offer.Retrieval.TypeId));
            var declined = f.Missions.Act(Pilot, Iris, MissionAction.Decline, offer.Id);
            Assert.Empty(declined.Standings);
        }
        var accepted = f.Accept(f.Request(Iris));
        f.Clock.AdvanceWallTime(TimeSpan.FromTicks(accepted.ExpiresAt - f.Clock.Now));
        Assert.Equal(MissionState.Failed, f.Missions.Current(Pilot, Iris)!.State);
        Assert.Null(f.Missions.Information(Pilot, Iris).ReplayAt);
        Assert.Equal(MissionState.Offered, f.Request(Iris).State);
    }

    private static MissionFixture Create() =>
        new(
            live: true,
            configure: catalog =>
            {
                catalog.Database.ExecuteSql($"INSERT OR IGNORE INTO NpcCorporation VALUES ({Issuer},'Paragon')");
                // Native agent rows come from the pinned fixture. Use local geometry for their stations.
                foreach (int station in new[] { Station, OtherStation })
                {
                    catalog.Database.ExecuteSql(
                        $"""
                        INSERT INTO Station(Id,TypeId,OwnerId,SolarSystemId,OperationId,OrbitId,X,Y,Z)
                        SELECT {station},TypeId,{Issuer},SolarSystemId,OperationId,OrbitId,X,Y,Z
                        FROM Station WHERE Id={MissionFixture.StationId};
                        """
                    );
                }
                catalog.Database.ExecuteSql(
                    $"INSERT OR IGNORE INTO ItemType(Id,Name,GroupId) VALUES ({CorporationManagement},'Corporation Management',257)"
                );
                catalog.Database.ExecuteSql(
                    $"""
                    INSERT OR IGNORE INTO TypeAttribute(ItemTypeId,AttributeId,Value)
                    VALUES ({CorporationManagement},{(int)DogmaAttributeId.SkillTimeConstant},1)
                    """
                );
            }
        );

    private static void Dock(LiveWorld live, int station)
    {
        using var action = live.Economy.Begin(new(pilots: [Pilot]));
        action.Pilots.Find(Pilot)!.ActiveShip.StationId = station;
        action.Commit();
    }

    private static long AddHull(LiveWorld live, int station, int quantity, bool singleton = false)
    {
        using var action = live.Economy.Begin(new(pilots: [Pilot]));
        long id = action.NextItemId();
        action.Add(
            new OwnedItem
            {
                Id = id,
                OwnerId = Pilot,
                TypeId = FirstHull,
                LocationId = station,
                FlagId = (int)InventoryFlag.Hangar,
                Quantity = quantity,
                Singleton = singleton,
            }
        );
        action.Commit();
        return id;
    }
}
