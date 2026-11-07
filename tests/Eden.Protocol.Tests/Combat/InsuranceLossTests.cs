using Eden.Data.Characters;
using Eden.Data.Combat;
using Eden.Data.Insurance;
using Eden.Data.Inventory;
using Eden.Data.Loot;
using Eden.Data.Market;
using Eden.Gameplay.Characters;
using Eden.Gameplay.Insurance;
using Eden.Gameplay.Market;
using Eden.Protocol.Tests.Persistence;
using Eden.World.Characters;
using Eden.World.Combat;
using Eden.World.Insurance;
using Eden.World.Inventory;
using Eden.World.Loot;
using Eden.World.Market;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Fixture = Eden.Protocol.Tests.Combat.NpcCombatTests.Fixture;

namespace Eden.Protocol.Tests.Combat;

public sealed class InsuranceLossTests
{
    [Theory]
    [InlineData(InsuranceLevel.Uninsured, false, false, 40000)]
    [InlineData(InsuranceLevel.Basic, false, false, 50000)]
    [InlineData(InsuranceLevel.Platinum, false, false, 100000)]
    [InlineData(InsuranceLevel.Platinum, true, false, 40000)]
    [InlineData(InsuranceLevel.Platinum, false, true, 40000)]
    public async Task FatalNpcDamageCreditsCoverageOnceAndRetainsDurablePolicyAndWalletHistory(
        InsuranceLevel level,
        bool expired,
        bool cancelled,
        long payout
    )
    {
        using Fixture fixture = new();
        InsuranceTestData.Prepare(fixture.Db);
        fixture.AlmostLost();
        InsuranceTestData.Fund(fixture.Db);
        PilotInsuranceStore insurance = new(new(fixture.Db.Path), fixture.Clock);
        var initialPosition = fixture.Pilot.Position!;
        fixture.Db.Store.SetPosition(Fixture.ShipId, null);
        long premium = level == InsuranceLevel.Uninsured ? 0 : InsuranceRules.Premium(100000, level);
        InsurancePolicy? policy = null;
        if (premium > 0)
        {
            policy = insurance.Insure(Fixture.PilotId, fixture.Pilot.StationId, Fixture.ShipId, premium, false).Policy;
            if (cancelled)
            {
                insurance.Cancel(Fixture.PilotId, fixture.Pilot.StationId, Fixture.ShipId);
            }
        }
        fixture.Db.Store.SetPosition(Fixture.ShipId, initialPosition, fixture.Npc.SystemId);
        if (expired)
        {
            fixture.Clock.AdvanceWallTime(InsuranceRules.Duration - TimeSpan.FromMilliseconds(7500));
        }
        fixture.Aggress();
        fixture.Clock.Milliseconds = 7500;
        using (WorldDbContext health = new(fixture.Db.Path))
        {
            // Model fatal damage at the policy boundary rather than 84 days of
            // unattended shield recharge before the NPC's first attack.
            health.Set<ShipHealthState>().Find(Fixture.ShipId)!.UpdatedAt = fixture.Clock.Now;
            health.SaveChanges();
        }
        var shots = await Task.WhenAll(Enumerable.Range(0, 2).Select(_ => Task.Run(() => fixture.Fire())));
        var fatal = Assert.Single(shots, shot => shot is not null)!;
        Assert.NotNull(fatal.Capsule);
        var settlement = Assert.IsType<InsurancePayout>(fatal.Insurance);
        Assert.Equal(Fixture.ShipId, settlement.ShipId);
        Assert.Equal(payout, settlement.AmountCents);
        Assert.Equal(1_000_000 - premium + payout, settlement.BalanceCents);
        Assert.Null(fixture.Fire());
        Assert.Empty(insurance.Contracts(Fixture.PilotId));
        WorldInitialization.Open(fixture.Db.Path);
        Assert.Equal(settlement.BalanceCents, fixture.Db.Services.GetRequiredService<IPilotWalletReader>().Balance(Fixture.PilotId));
        var journal = fixture.Db.Services.GetRequiredService<IPilotWalletReader>().Journal(Fixture.PilotId).Insurance;
        Assert.Equal(premium > 0 ? 2 : 1, journal.Count);
        Assert.Equal(payout, Assert.Single(journal, entry => entry.AmountCents > 0).AmountCents);
        using WorldDbContext world = new(fixture.Db.Path);
        Assert.Single(world.Set<ShipLoss>());
        Assert.Single(world.Set<SavedInsuranceLedger>().Where(entry => entry.IsPayout));
        if (policy is not null)
        {
            var status =
                cancelled ? InsurancePolicyStatus.Cancelled
                : expired ? InsurancePolicyStatus.Expired
                : InsurancePolicyStatus.Paid;
            Assert.Equal(status, world.Set<SavedInsurancePolicy>().Find(policy.Id)!.Status);
        }
        Assert.Empty(world.Set<SavedInsuranceLedger>().Where(entry => entry.PilotId == 90000002));
    }

    [Fact]
    public void FailureAfterSettlementRollsBackTheCreditPolicyDamageLossAndCapsuleTogether()
    {
        using Fixture fixture = new();
        InsuranceTestData.Prepare(fixture.Db);
        fixture.AlmostLost();
        InsuranceTestData.Fund(fixture.Db);
        var position = fixture.Pilot.Position!;
        fixture.Db.Store.SetPosition(Fixture.ShipId, null);
        PilotInsuranceStore insurance = new(new(fixture.Db.Path), fixture.Clock);
        var purchase = insurance.Insure(Fixture.PilotId, fixture.Pilot.StationId, Fixture.ShipId, 30000, false);
        fixture.Db.Store.SetPosition(Fixture.ShipId, position, fixture.Npc.SystemId);
        fixture.Aggress();
        fixture.Clock.Milliseconds = 7500;
        using (WorldDbContext world = new(fixture.Db.Path))
        {
            world.Database.ExecuteSqlRaw(
                """
                CREATE TRIGGER RejectInsurancePayout BEFORE INSERT ON WorldInsuranceLedger WHEN NEW.IsPayout=1
                BEGIN SELECT RAISE(ABORT, 'forced insurance settlement failure'); END;
                """
            );
        }
        Assert.Throws<DbUpdateException>(() => fixture.Fire());
        using (WorldDbContext world = new(fixture.Db.Path))
        {
            Assert.Equal(Fixture.ShipId, world.Pilots.Find(Fixture.PilotId)!.ActiveShipId);
            Assert.NotNull(world.Ships.Find(Fixture.ShipId));
            Assert.Empty(world.Set<ShipLoss>());
            Assert.Empty(world.Set<WorldWreck>());
            Assert.Equal(174, world.Set<ShipHealthState>().Single().StructureDamage);
            Assert.Equal(purchase.BalanceCents, world.Set<PilotWallet>().Find(Fixture.PilotId)!.Cents);
            Assert.Equal(InsurancePolicyStatus.Active, world.Set<SavedInsurancePolicy>().Find(purchase.Policy.Id)!.Status);
            Assert.Single(world.Set<SavedInsuranceLedger>());
            Assert.Null(world.Set<NpcAggression>().Single().LastShotAt);
            Assert.Empty(world.Set<SavedTaskCounter>().Where(counter => counter.Name == TaskEvents.ShipLost));
            world.Database.ExecuteSqlRaw("DROP TRIGGER RejectInsurancePayout");
        }
        Assert.Equal(100000, fixture.Fire()!.Insurance!.AmountCents);
        Assert.Equal(1, TaskTrackingTestData.Count(fixture.Db, TaskEvents.ShipLost));
        Assert.Empty(insurance.Contracts(Fixture.PilotId));
    }

    [Fact]
    public void RemovingAnUnpilotedHullVoidsItsPolicyWithoutPayingThePilotsActiveShipCoverage()
    {
        using Fixture fixture = new();
        InsuranceTestData.Prepare(fixture.Db);
        InsuranceTestData.Fund(fixture.Db);
        fixture.Db.Store.SetPosition(Fixture.ShipId, null);
        long otherShip;
        using (WorldDbContext world = new(fixture.Db.Path))
        {
            otherShip = InventoryPersistence.NextId(world);
            world.Add(
                new Ship
                {
                    Id = otherShip,
                    OwnerId = Fixture.PilotId,
                    TypeId = 601,
                    StationId = fixture.Pilot.StationId,
                    Name = "Spare",
                }
            );
            world.SaveChanges();
        }
        PilotInsuranceStore insurance = new(new(fixture.Db.Path), fixture.Clock);
        var purchase = insurance.Insure(Fixture.PilotId, fixture.Pilot.StationId, otherShip, 30000, false);
        using (WorldDbContext world = new(fixture.Db.Path))
        {
            using var transaction = world.Database.BeginTransaction();
            ShipStoragePersistence.Destroy(world, world.Ships.Find(otherShip)!);
            world.SaveChanges();
            transaction.Commit();
        }
        Assert.Empty(insurance.Contracts(Fixture.PilotId));
        Assert.Equal(purchase.BalanceCents, fixture.Db.Services.GetRequiredService<IPilotWalletReader>().Balance(Fixture.PilotId));
        using WorldDbContext check = new(fixture.Db.Path);
        Assert.Equal(InsurancePolicyStatus.HullRemoved, check.Set<SavedInsurancePolicy>().Find(purchase.Policy.Id)!.Status);
        Assert.Empty(check.Set<SavedInsuranceLedger>().Where(entry => entry.IsPayout));
    }
}
