using Eden.Gameplay.Characters;
using Eden.Gameplay.Combat;
using Eden.Gameplay.Docking;
using Eden.Gameplay.Dogma;
using Eden.Gameplay.Flight;
using Eden.Gameplay.Inventory;
using Eden.Gameplay.Loot;
using Eden.Gameplay.Mail;
using Eden.Gameplay.Market;
using Eden.Gameplay.Mining;
using Eden.Gameplay.Propulsion;
using Eden.Gameplay.Skills;
using Eden.Protocol.PilotSessions;
using Eden.Protocol.Tests.Ammunition;
using Eden.Protocol.Tests.Fixtures;
using Eden.Server.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Eden.Protocol.Tests.Persistence;

public sealed class ResidentReadTests
{
    private const long PilotId = 90000001;
    private const int SystemId = 30000142;

    [Fact]
    public void WarmMonitoringAndEmptySceneActivityReadNoSqlAndOpenNoConnections()
    {
        TestClock clock = new();
        using TestWorld db = new(clock: clock, liveWorld: true);
        long ship = db.Store.FindPilot(1000001)!.ShipId;
        var services = db.Services;
        void ReadAll()
        {
            _ = services.GetRequiredService<IPilotImplants>().Read(PilotId);
            _ = services.GetRequiredService<IPilotBoosters>().Read(PilotId);
            _ = services.GetRequiredService<IPilotSkillInjectors>().Read(PilotId);
            _ = services.GetRequiredService<IPilotFreeSkillPoints>().Observe(PilotId);
            _ = services.GetRequiredService<IPilotSkillExtraction>().Revision(PilotId);
            _ = services.GetRequiredService<IPilotLoyaltyWallet>().Read(PilotId);
            _ = services.GetRequiredService<IPilotWalletReader>().Balance(PilotId);
            _ = services.GetRequiredService<IPilotFitting>().ShipAttributes(PilotId);
            Assert.Empty(services.GetRequiredService<IPilotMail>().Changes(PilotId, 0).Changes);
            Assert.Empty(services.GetRequiredService<IPilotMailingLists>().Changes(PilotId, 0).Items);
            Assert.Empty(services.GetRequiredService<IAsteroidField>().List(SystemId));
            Assert.Empty(services.GetRequiredService<IWrecks>().List(SystemId, clock.Now));
            var missiles = services.GetRequiredService<IPilotMissiles>();
            missiles.Advance(SystemId, clock.Now);
            Assert.Empty(missiles.List(SystemId));
            Assert.Empty(missiles.TakeImpacts(PilotId));
            Assert.Empty(services.GetRequiredService<INpcMissiles>().Advance(PilotId, ship, SpacePosition.Zero, clock.Now));
            var tackle = services.GetRequiredService<IPilotTackle>();
            tackle.Sweep(clock.Now);
            Assert.Empty(tackle.Incoming(ship));
        }
        ReadAll();
        clock.Seconds += 10;
        using var measured = SessionPerformance.Begin("warm read views", _ => { }, force: true)!;
        ReadAll();
        ReadAll();
        Assert.Equal(0, measured.Commands);
        Assert.Equal(0, measured.Connections);
        Assert.Equal(0, measured.FittingCalculations);
    }

    [Fact]
    public void LearningAndInjectionUseExpiryIsCalculatedInMemoryAtTheExactDeadline()
    {
        TestClock clock = new();
        using TestWorld db = new(clock: clock, liveWorld: true);
        long expires = clock.Now + TimeSpan.TicksPerSecond;
        using (WorldDbContext world = new(db.Path))
        {
            world.Add(
                new SavedBooster
                {
                    ItemId = 9991,
                    PilotId = PilotId,
                    TypeId = 1,
                    Slot = 10,
                    Bonus = 4,
                    RemainingInjectionUses = 2,
                    StartedAt = clock.Now,
                    ExpiresAt = expires,
                }
            );
            world.SaveChanges();
        }
        var boosters = db.Services.GetRequiredService<IPilotBoosters>();
        var injectors = db.Services.GetRequiredService<IPilotSkillInjectors>();
        var before = boosters.Read(PilotId);
        Assert.Equal(2, injectors.Read(PilotId).AvailableUses);
        clock.Milliseconds += 999;
        using var measured = SessionPerformance.Begin("learning expiry", _ => { }, force: true)!;
        Assert.Single(boosters.Read(PilotId).Boosters);
        Assert.Equal(2, injectors.Read(PilotId).AvailableUses);
        clock.Milliseconds++;
        var after = boosters.Read(PilotId);
        Assert.Empty(after.Boosters);
        Assert.Equal(before.Attributes.Perception - 4, after.Attributes.Perception);
        Assert.Equal(0, injectors.Read(PilotId).AvailableUses);
        Assert.Equal(0, measured.Commands);
        Assert.Equal(0, measured.Connections);
    }

    [Fact]
    public void ReconfigurationInvalidatesTheInMemoryFitAttributes()
    {
        using AmmunitionTests.Fixture fixture = new(liveWorld: true);
        var pilot = fixture.Pilot;
        var before = fixture.Fitting.ShipAttributes(pilot.PilotId);
        fixture.Fitting.SetOnline(pilot.PilotId, pilot.ShipId, fixture.Gun, false);
        var after = fixture.Fitting.ShipAttributes(pilot.PilotId);
        Assert.True(before[DogmaAttributeId.CpuLoad] > after[DogmaAttributeId.CpuLoad]);
        using var measured = SessionPerformance.Begin("reconfigured warm fit", _ => { }, force: true)!;
        Assert.Equal(after, fixture.Fitting.ShipAttributes(pilot.PilotId));
        Assert.Equal(0, measured.Commands);
        Assert.Equal(0, measured.Connections);
    }

    [Fact]
    public void AmmoQuantityUpdatesRetainSceneViewsWhileMembershipChangesInvalidateThem()
    {
        using AmmunitionTests.Fixture fixture = new(liveWorld: true);
        fixture.Load(quantity: 20);
        var wrecks = fixture.Db.Services.GetRequiredService<IWrecks>();
        Assert.Empty(wrecks.List(SystemId, fixture.Clock.Now));
        long station = fixture.Pilot.StationId;
        using (WorldDbContext world = new(fixture.Db.Path))
        {
            world.Database.ExecuteSql($"UPDATE worlditem SET quantity=quantity-1 WHERE LocationId={fixture.Gun}");
        }
        using (var measured = SessionPerformance.Begin("quantity keeps scene", _ => { }, force: true)!)
        {
            Assert.Empty(wrecks.List(SystemId, fixture.Clock.Now));
            Assert.Equal(0, measured.Commands);
            Assert.Equal(0, measured.Connections);
        }
        using (WorldDbContext world = new(fixture.Db.Path))
        {
            int hangar = (int)InventoryFlag.Hangar;
            world.Database.ExecuteSql($"UPDATE worlditem SET locationid={station},flagid={hangar} WHERE LocationId={fixture.Gun}");
        }
        using var refresh = SessionPerformance.Begin("membership refreshes scene", _ => { }, force: true)!;
        Assert.Empty(wrecks.List(SystemId, fixture.Clock.Now));
        Assert.True(refresh.Commands > 0);
    }

    [Fact]
    public async Task ScalarViewsRefreshAfterCommitAndRollbackAndRecoverOnReopen()
    {
        TestClock clock = new();
        using TestWorld db = new(clock: clock, liveWorld: true);
        var wallet = db.Services.GetRequiredService<IPilotWalletReader>();
        Assert.Equal(0, wallet.Balance(PilotId));
        Assert.Empty(db.Services.GetRequiredService<IPilotLoyaltyWallet>().Read(PilotId));
        Assert.Equal(0, db.Services.GetRequiredService<IPilotFreeSkillPoints>().Observe(PilotId).Points);
        using (WorldDbContext world = new(db.Path))
        {
            world.Add(new PilotWallet { PilotId = PilotId, Cents = 12345 });
            world.SaveChanges();
        }
        Assert.Equal(12345, wallet.Balance(PilotId));
        using (WorldDbContext world = new(db.Path))
        using (var transaction = world.Database.BeginTransaction())
        {
            world.Database.ExecuteSql($"UPDATE WorldWallet SET Cents=999 WHERE PilotId={PilotId}");
            transaction.Rollback();
        }
        Assert.Equal(12345, wallet.Balance(PilotId));
        using (WorldDbContext world = new(db.Path))
        {
            world.Database.ExecuteSql($"INSERT OR REPLACE INTO WorldFreeSkillPoints VALUES({PilotId},42,3)");
            world.Database.ExecuteSql($"INSERT OR REPLACE INTO WorldLoyaltyWallet VALUES({PilotId},1000001,37)");
        }
        Assert.Equal(42, db.Services.GetRequiredService<IPilotFreeSkillPoints>().Observe(PilotId).Points);
        Assert.Equal(37, Assert.Single(db.Services.GetRequiredService<IPilotLoyaltyWallet>().Read(PilotId)).Points);
        await LiveWorld.Find(db.Path)!.FlushAsync();
        string copy = Path.Combine(Path.GetDirectoryName(db.Path)!, "read-views-checkpoint.sqlite");
        File.Copy(db.Path, copy);
        using WorldDbContext reopened = new(copy);
        Assert.Equal(12345, reopened.Set<PilotWallet>().Find(PilotId)!.Cents);
        Assert.Equal(42, reopened.Set<SavedFreeSkillPoints>().Find(PilotId)!.Points);
        using WorldDbContext delete = new(db.Path);
        delete.Database.ExecuteSqlRaw("DELETE FROM WorldWallet");
        Assert.Equal(0, wallet.Balance(PilotId));
    }
}
