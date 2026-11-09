using System.Globalization;
using Eden.Data.World.Agents;
using Eden.Data.World.Inventory;
using Eden.Gameplay.Agents;
using Eden.World.Agents;
using Eden.World.Catalog;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Eden.Protocol.Tests.Agents;

public sealed partial class MissionContentTests
{
    [Theory]
    [InlineData(0, 0, true)]
    [InlineData(500001, 1000044, true)]
    [InlineData(500002, 0, false)]
    [InlineData(0, 1000045, false)]
    [InlineData(500001, 1000045, false)]
    public void SupplierRestrictionsFilterOffersAndRejectInvalidSavedAcceptanceWithoutCargo(int faction, int corporation, bool allowed)
    {
        using MissionFixture f = new(configure: catalog =>
        {
            catalog.Database.ExecuteSqlRaw("DELETE FROM SourceRecord WHERE Dataset='client/missions' AND Key<>'2912'");
            catalog.Database.ExecuteSql(
                $"""
                UPDATE SourceRecord SET Data=json_set(Data,'$.factionID',{faction},'$.corporationID',{corporation})
                WHERE Dataset='client/missions' AND Key='2912'
                """
            );
        });
        using CatalogDbContext catalog = new(new WorldDatabase(f.Db.Path).CatalogPath);
        var profile = MissionCatalog.Find(catalog, 2912);
        var agent = f.Services.GetRequiredService<INpcDirectory>().Agent(MissionFixture.AgentId)!;
        Assert.Equal(allowed, profile.AvailableTo(agent));
        if (allowed)
        {
            Assert.Equal(MissionState.Accepted, f.Accept(f.Request()).State);
            return;
        }
        Assert.Throws<InvalidDataException>(() => f.Request());
        long offerId;
        using (WorldDbContext world = new(f.Db.Path))
        {
            Assert.Empty(world.Set<SavedMission>());
            var stale = world
                .Add(
                    new SavedMission
                    {
                        PilotId = MissionFixture.PilotId,
                        AgentId = MissionFixture.AgentId,
                        MissionId = 2912,
                        Level = 1,
                        State = MissionState.Offered,
                        OfferedAt = f.Clock.Now,
                        ExpiresAt = f.Clock.Now + TimeSpan.TicksPerDay,
                        DestinationStation = MissionFixture.DestinationId,
                        SystemId = 30000144,
                    }
                )
                .Entity;
            world.SaveChanges();
            offerId = stale.Id;
        }
        Assert.Throws<InvalidDataException>(() =>
            f.Missions.Act(MissionFixture.PilotId, MissionFixture.AgentId, MissionAction.Accept, offerId)
        );
        using WorldDbContext check = new(f.Db.Path);
        Assert.Equal(MissionState.Offered, check.Set<SavedMission>().Find(offerId)!.State);
        Assert.Empty(check.Set<OwnedItem>().Where(i => i.TypeId == profile.CargoType));
    }
}
