using Eden.Data.World.Structures;
using Eden.Gameplay.Combat;
using Eden.Gameplay.Flight;
using Eden.Gameplay.Inventory;
using Eden.Gameplay.Structures;
using Eden.Protocol.Flight;
using Eden.World.Runtime;
using Eden.World.Storage;
using Microsoft.Extensions.DependencyInjection;

namespace Eden.Protocol.Tests.Combat;

public sealed partial class AreaWeaponTests
{
    [Fact]
    public void VoidBombCannotDrainATetheredShipWithoutAnOnlineFlightContact()
    {
        using var fixture = Prepare(Manticore);
        PassiveCatalogFixture.LoadStructures(fixture.CatalogPath);
        long module = fixture.Fit(BombLauncher);
        const int VoidBomb = 27924;
        Load(fixture, module, VoidBomb);
        var victim = Place(fixture, new SpacePosition(30000, 0, 0));
        using (WorldDbContext world = new(fixture.Db.Path))
        {
            world.Add(
                new SavedStructure
                {
                    Id = StructureRules.FirstStructureId + 1,
                    TypeId = 35832,
                    Name = "Tether test",
                    SystemId = SystemId,
                    State = StructureState.ShieldVulnerable,
                    HasCore = true,
                    PublicAccess = true,
                    X = 30000,
                }
            );
            world.SaveChanges();
        }
        using LiveWorld live = new(new(fixture.Db.Path), fixture.Clock);
        var tethering = fixture.Db.Services.GetRequiredService<IPilotTethering>();
        Assert.NotNull(tethering.Advance(victim.PilotId, new(true, false, false, false, false)));
        var store = fixture.Db.Services.GetRequiredService<IPilotAreaWeapons>();
        var paid = store.Activate(
            fixture.PilotId,
            fixture.ShipId,
            module,
            DogmaEffect.UseMissiles,
            new(1, 0, 0),
            new(100, 0, 0),
            null,
            fixture.Clock.Now
        );
        fixture.Clock.AdvanceWallTime(TimeSpan.FromSeconds(12));
        store.Advance(SystemId, fixture.Clock.Now);
        Assert.True(store.Impacted(paid.FlightId!.Value));
        Assert.True(tethering.Protected(victim.ShipId));
        var charge = live.Economy.Inspect(world => world.Set<Eden.Data.World.Fittings.ShipEnergy>().Find(victim.ShipId)?.Charge);
        Assert.Null(charge);
    }

    [Fact]
    public void BurstJammerCanBreakAnExistingTetherWithoutBeingMistakenForTravel()
    {
        using var fixture = Prepare();
        PassiveCatalogFixture.LoadStructures(fixture.CatalogPath);
        long module = fixture.Fit(BurstJammer);
        Place(fixture, new SpacePosition(1000, 0, 0));
        using (WorldDbContext world = new(fixture.Db.Path))
        {
            world.Add(
                new SavedStructure
                {
                    Id = StructureRules.FirstStructureId + 1,
                    TypeId = 35832,
                    Name = "Tether test",
                    SystemId = SystemId,
                    State = StructureState.ShieldVulnerable,
                    HasCore = true,
                    PublicAccess = true,
                }
            );
            world.SaveChanges();
        }
        using LiveWorld live = new(new(fixture.Db.Path), fixture.Clock);
        var tethering = fixture.Db.Services.GetRequiredService<IPilotTethering>();
        Assert.NotNull(tethering.Advance(fixture.PilotId, new(true, false, false, false, false)));
        var pilot = fixture.Db.Store.FindPilot(1000001)!;
        var space = fixture.Db.Services.GetRequiredService<SharedSpace>();
        space.Publish(new(PilotContact.From(pilot), SpacePosition.Zero, null, 0, null));
        Assert.True(space.IsAreaProtected(SystemId, fixture.ShipId));
        Assert.False(space.IsTravelProtected(SystemId, fixture.ShipId));
        var store = fixture.Db.Services.GetRequiredService<IPilotAreaWeapons>();
        Assert.NotNull(
            store.Activate(
                fixture.PilotId,
                fixture.ShipId,
                module,
                DogmaEffect.BurstJammer,
                new(1, 0, 0),
                SpacePosition.Zero,
                null,
                fixture.Clock.Now
            )
        );
        Assert.False(tethering.Protected(fixture.ShipId));
        Assert.Null(tethering.Advance(fixture.PilotId, new(true, false, false, false, false)));
    }
}
