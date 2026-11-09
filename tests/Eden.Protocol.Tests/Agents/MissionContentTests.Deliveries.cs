using Eden.Data.World.Agents;
using Eden.Data.World.Characters;
using Eden.Data.World.Inventory;
using Eden.Data.World.Market;
using Eden.Gameplay.Agents;
using Eden.Gameplay.Market;
using Eden.Server;
using Eden.World.Agents;
using Eden.World.Catalog;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Eden.Protocol.Tests.Agents;

public sealed partial class MissionContentTests
{
    [Theory]
    [MemberData(nameof(DeliveryMissions))]
    public void EveryAdmittedDeliveryCanBeOfferedTransportedReopenedAndPaidExactlyOnce(int id, int level)
    {
        using var f = Create(id, level, MissionRules.DistributionDivision);
        using CatalogDbContext catalog = new(new WorldDatabase(f.Db.Path).CatalogPath);
        var profile = MissionCatalog.Find(catalog, id);
        MissionOffer offer;
        if (profile.Definition.Storyline)
        {
            int faction = f.Services.GetRequiredService<INpcDirectory>().Agent(MissionFixture.AgentId)!.FactionId!.Value;
            using (WorldDbContext world = new(f.Db.Path))
            {
                world.Add(
                    new SavedMissionProgress
                    {
                        PilotId = MissionFixture.PilotId,
                        FactionId = faction,
                        Level = level,
                        Completed = MissionRules.StorylineInterval - 1,
                    }
                );
                world.SaveChanges();
            }
            var regular = f.Accept(f.Request());
            Transport(f, regular);
            f.Missions.Act(MissionFixture.PilotId, MissionFixture.AgentId, MissionAction.Complete, regular.Id);
            offer = Assert.Single(f.Missions.Journal(MissionFixture.PilotId));
            Assert.True(offer.Storyline);
            Assert.Equal(MissionFixture.StorylineId, offer.Agent.Id);
            Assert.Equal(level, offer.Level);
            Assert.Equal(0, offer.Rewards.LoyaltyPoints);
            f.Dock(MissionFixture.StationId);
        }
        else
        {
            offer = f.Request();
        }
        Assert.Equal(id, offer.MissionId);
        Assert.True(profile.AvailableTo(offer.Agent));
        Assert.Equal(profile.CargoType, offer.Cargo!.TypeId);
        Assert.Equal(profile.CargoQuantity, offer.Cargo.Quantity);
        Assert.Equal(profile.CargoVolume, offer.Cargo.Volume);
        Assert.False(offer.ObjectiveComplete);
        f.Accept(offer);
        f.Dock(offer.Cargo.DestinationStation);
        Assert.Throws<InvalidDataException>(() => f.Missions.Act(MissionFixture.PilotId, offer.Agent.Id, MissionAction.Complete, offer.Id));
        using (WorldDbContext world = new(f.Db.Path))
        {
            var cargo = Assert.Single(world.Set<OwnedItem>().Where(i => i.TypeId == profile.CargoType));
            Assert.Equal(profile.CargoQuantity, cargo.Quantity);
            Assert.Equal(MissionFixture.StationId, cargo.LocationId);
            Assert.Equal(MissionFixture.PilotId, cargo.OwnerId);
        }
        Transport(f, offer);
        using var reopened = ServerServices.Build(new(f.Db.Path), f.Clock);
        var missions = reopened.GetRequiredService<IPilotMissions>();
        Assert.True(missions.Current(MissionFixture.PilotId, offer.Agent.Id)!.ObjectiveComplete);
        var paid = missions.Act(MissionFixture.PilotId, offer.Agent.Id, MissionAction.Complete, offer.Id);
        Assert.Equal(MissionState.Completed, paid.Offer!.State);
        Assert.Equal(profile.CargoQuantity, paid.Items.Sum(i => i.Previous!.Quantity - i.Item.Quantity));
        Assert.Throws<InvalidDataException>(() => missions.Act(MissionFixture.PilotId, offer.Agent.Id, MissionAction.Complete, offer.Id));
        using WorldDbContext check = new(f.Db.Path);
        Assert.Empty(check.Set<OwnedItem>().Where(i => i.TypeId == profile.CargoType));
        Assert.Equal(
            profile.Definition.Storyline ? 2 : 1,
            check.Set<SavedMarketPayment>().Count(p => p.Kind == WalletEntryType.AgentMissionReward)
        );
    }

    private static void Transport(MissionFixture f, MissionOffer offer)
    {
        using (WorldDbContext world = new(f.Db.Path))
        {
            var item = world.Set<OwnedItem>().Single(i => i.TypeId == offer.Cargo!.TypeId);
            item.LocationId = offer.Cargo!.DestinationStation;
            world.SaveChanges();
        }
        f.Dock(offer.Cargo!.DestinationStation);
    }
}
