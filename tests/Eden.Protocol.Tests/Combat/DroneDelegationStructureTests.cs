using Eden.Data.World.Structures;
using Eden.Gameplay.Combat;
using Eden.Gameplay.Drones;
using Eden.Gameplay.Flight;
using Eden.Gameplay.Structures;
using Eden.World.Catalog;
using Eden.World.Runtime.Economy;
using Eden.World.Structures;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Eden.Protocol.Tests.Combat;

public sealed partial class DroneDelegationTests
{
    [Fact]
    public void AssistedStructureAttacksRespectOwnerSurfaceRangeAndRequireANewPaidTriggerAfterRejection()
    {
        using Fixture fixture = new(resident: true, quantity: 1);
        PassiveCatalogFixture.LoadStructures(fixture.Base.Base.CatalogPath);
        using CatalogDbContext catalog = new(fixture.Base.Base.CatalogPath, writable: true);
        const string security = "{\"security\":0}";
        catalog.Database.ExecuteSql($"INSERT OR REPLACE INTO SourceRecord VALUES ('client/solarsystemcontent','30000144',{security})");
        fixture.Assign(DroneDelegationMode.Assist);
        var owner = fixture.Base.Base.Db.Store.FindPilot(1000001)!;
        var profile = StructureCatalog.Require(catalog, 35832);
        double range = fixture.Base.Drones.Control(owner.PilotId).Range;
        var origin = owner.Position!;
        var far = origin + new SpacePosition(range + profile.Radius + 10_000, 0, 0);
        const long structureId = StructureRules.FirstStructureId + 9_000_000;
        using (WorldActionScope prepare = new(fixture.Database, new(systems: [SystemId])))
        {
            prepare.World.Add(
                new SavedStructure
                {
                    Id = structureId,
                    TypeId = profile.TypeId,
                    CorporationId = owner.CorpId,
                    SystemId = SystemId,
                    Name = "Assisted target",
                    X = far.X,
                    Y = far.Y,
                    Z = far.Z,
                    HasCore = true,
                    State = StructureState.ShieldVulnerable,
                    StateStartedAt = fixture.Base.Now,
                    DeployedAt = fixture.Base.Now,
                }
            );
            prepare.Commit();
        }
        fixture.Base.Base.Db.Store.SetPosition(MemberShip, far - new SpacePosition(profile.Radius + 1000, 0, 0), SystemId);
        fixture.PublishMember();
        var combat = fixture.Base.Base.Db.Services.GetRequiredService<IPilotCombat>();
        combat.Fire(Member, fixture.Gun, structureId, SpacePosition.Zero, fixture.Base.Now, .01);
        Assert.Empty(fixture.Base.Tick().Hits);
        Assert.Equal(DroneActivity.Idle, Assert.Single(fixture.Contacts()).Activity);
        using (WorldActionScope move = new(fixture.Database, new(systems: [SystemId])))
        {
            var structure = move.World.Set<SavedStructure>().Find(structureId)!;
            structure.X = origin.X + profile.Radius + 1000;
            move.Commit();
        }
        Assert.Empty(fixture.Base.Tick(10).Hits);
        Assert.Equal(DroneActivity.Idle, Assert.Single(fixture.Contacts()).Activity);
        combat.Fire(Member, fixture.Gun, structureId, SpacePosition.Zero, fixture.Base.Now, .01);
        var hits = fixture.Base.Tick().Hits;
        Assert.Equal(structureId, Assert.Single(hits).TargetId);
        Assert.True(hits[0].Applied > 0);
    }
}
