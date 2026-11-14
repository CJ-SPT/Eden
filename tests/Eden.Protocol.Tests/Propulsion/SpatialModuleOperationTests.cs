using Eden.Data.World.Combat;
using Eden.Data.World.Docking;
using Eden.Data.World.Fittings;
using Eden.Data.World.Inventory;
using Eden.Data.World.Propulsion;
using Eden.Data.World.Skills;
using Eden.Gameplay.Dogma;
using Eden.Gameplay.Flight;
using Eden.Gameplay.Inventory;
using Eden.Gameplay.Propulsion;
using Eden.Infrastructure.Diagnostics;
using Eden.Protocol.Targeting;
using Eden.World.Catalog;
using Eden.World.Fittings;
using Eden.World.Runtime;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Fixture = Eden.Protocol.Tests.Persistence.PassiveFittingTests.Fixture;

namespace Eden.Protocol.Tests.Propulsion;

public sealed partial class SpatialModuleOperationTests
{
    private const int Raven = 638;
    private const int Prototype = 11370;
    private const int LargeDrive = 4383;

    private static Fixture Prepare(int hull = Raven)
    {
        Fixture f = new();
        PassiveCatalogFixture.LoadSpatialModules(f.CatalogPath);
        Eden.Protocol.Tests.Persistence.SmallHullTests.Board(f, hull);
        return f;
    }

    private static void Space(Fixture f)
    {
        var pilot = f.Db.Store.FindPilot(1000001)!;
        f.Db.Store.SetPosition(f.ShipId, new(pilot.X + pilot.Flight.StationRadius + 50000, pilot.Y, pilot.Z));
        using WorldDbContext world = new(f.Db.Path);
        var energy = world.Set<ShipEnergy>().Find(f.ShipId) ?? world.Add(new ShipEnergy { ShipId = f.ShipId }).Entity;
        energy.Charge = 5000;
        energy.UpdatedAt = f.Clock.Now;
        world.SaveChanges();
    }

    private static IPilotSpatialModules Store(Fixture f) => f.Db.Services.GetRequiredService<IPilotSpatialModules>();

    [Fact]
    public void ForeignCancellationAndCompletionCannotConsumeAnotherPilotsPaidJump()
    {
        using Fixture f = Prepare();
        long module = f.Fit(LargeDrive);
        Space(f);
        var store = Store(f);
        var jump = store.Activate(f.PilotId, module, DogmaEffect.MicroJumpDrive, new(1, 0, 0), SpacePosition.Zero, f.Clock.Now);
        long foreign = f.Db.Store.FindPilot(1000002)!.PilotId;
        Assert.Empty(store.Stop(foreign, f.ShipId, module, f.Clock.Now));
        Assert.Throws<InvalidDataException>(() => store.CompleteJump(foreign, f.ShipId, module, jump.StartedAt, jump.EndsAt));
        using (WorldDbContext verify = new(f.Db.Path))
        {
            Assert.Equal(module, verify.Set<SpatialNavigationState>().Find(f.ShipId)!.JumpModuleId);
            Assert.Equal(jump.Capacitor, verify.Set<ShipEnergy>().Find(f.ShipId)!.Charge);
        }
        Assert.NotNull(store.CompleteJump(f.PilotId, f.ShipId, module, jump.StartedAt, jump.EndsAt));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ScramblerInterruptsPaidJumpButDisruptorDoesNot(bool scrambler)
    {
        using Fixture f = Prepare();
        int tackleType = scrambler ? 447 : 3242;
        PassiveCatalogFixture.LoadTackleTiers(f.CatalogPath, new HashSet<int> { tackleType, 3435, 3455, 3449 });
        long drive = f.Fit(LargeDrive);
        long tackleModule = f.Fit(tackleType);
        Space(f);
        var wing = f.Db.Store.FindPilot(1000002)!;
        f.Db.Store.SetPosition(wing.ShipId, f.Db.Store.FindPilot(1000001)!.Position! + new SpacePosition(5000, 0, 0));
        using (WorldDbContext world = new(f.Db.Path))
        {
            var item = world.Set<OwnedItem>().Find(tackleModule)!;
            item.OwnerId = wing.PilotId;
            item.LocationId = wing.ShipId;
            foreach (var skill in world.Set<PilotSkill>().Where(s => s.PilotId == f.PilotId).ToArray())
            {
                var saved =
                    world.Set<PilotSkill>().Find(wing.PilotId, skill.TypeId)
                    ?? world.Add(new PilotSkill { PilotId = wing.PilotId, TypeId = skill.TypeId }).Entity;
                saved.Points = skill.Points;
            }
            world.SaveChanges();
        }
        using (CatalogDbContext catalog = new(f.CatalogPath, writable: true))
        {
            catalog.Database.ExecuteSqlRaw(
                "INSERT OR REPLACE INTO SourceRecord VALUES ('client/systems','30000142','{{\"securityStatus\":0}}')"
            );
        }
        var store = Store(f);
        var origin = f.Db.Store.FindPilot(1000001)!.Position!;
        var jump = store.Activate(f.PilotId, drive, DogmaEffect.MicroJumpDrive, new(1, 0, 0), SpacePosition.Zero, f.Clock.Now);
        f.Clock.Milliseconds += 7000;
        f.Db.Services.GetRequiredService<IPilotTackle>()
            .Activate(
                wing.PilotId,
                tackleModule,
                scrambler ? DogmaEffect.WarpScrambler : DogmaEffect.WarpDisruptor,
                new(f.ShipId, TackleTargetKind.Ship),
                new HashSet<long> { f.ShipId },
                f.Clock.Now
            );
        f.Clock.Milliseconds += 2000;
        var result = store.CompleteJump(f.PilotId, f.ShipId, drive, jump.StartedAt, f.Clock.Now);
        Assert.Equal(scrambler, result is null);
        Assert.Equal(scrambler ? origin : origin + new SpacePosition(100000, 0, 0), f.Db.Store.FindPilot(1000001)!.Position);
        using WorldDbContext verify = new(f.Db.Path);
        Assert.Equal(jump.ReadyAt, verify.Set<ModuleCooldown>().Find(drive)!.ReadyAt);
        Assert.Equal(0, verify.Set<SpatialNavigationState>().Find(f.ShipId)!.JumpModuleId);
    }

    [Theory]
    [InlineData(11370)]
    [InlineData(11577)]
    [InlineData(11578)]
    [InlineData(14234)]
    [InlineData(14776)]
    [InlineData(14778)]
    [InlineData(14780)]
    [InlineData(14782)]
    [InlineData(15790)]
    [InlineData(16126)]
    [InlineData(20561)]
    [InlineData(20563)]
    [InlineData(20565)]
    [InlineData(32260)]
    public void EveryStaticCloakUsesItsCatalogSpeedAndFittedScanPenalty(int type)
    {
        using Fixture f = Prepare(type is 11578 or 20563 ? 33468 : Raven);
        long module = f.Fit(type);
        Space(f);
        using CatalogDbContext catalog = new(f.CatalogPath);
        var attributes = catalog.FittingAttributes(type);
        var effect = FittingCatalog.DefaultEffect(catalog, type)!.Value;
        double speed = f.Fitting.ShipAttributes(f.PilotId)[DogmaAttributeId.MaxVelocity];
        var activated = Store(f).Activate(f.PilotId, module, effect, new(1, 0, 0), SpacePosition.Zero, f.Clock.Now);
        Assert.Equal(
            speed * attributes[DogmaAttributeId.CloakVelocityMultiplier],
            activated.ShipAttributes[DogmaAttributeId.MaxVelocity],
            6
        );
        Assert.Equal(type is 11578 or 20563, Store(f).Status(f.ShipId).WarpSafe);
        Store(f).Stop(f.PilotId, f.ShipId, module, f.Clock.Now);
    }

    [Fact]
    public void CloakChangesFittingAndVisibilityThenPersistsReactivationAndTargetingDeadlines()
    {
        using Fixture f = Prepare();
        long module = f.Fit(Prototype);
        Space(f);
        using LiveWorld live = new(new(f.Db.Path), f.Clock, _ => { });
        var store = Store(f);
        double speed = f.Fitting.ShipAttributes(f.PilotId)[DogmaAttributeId.MaxVelocity];
        var activated = store.Activate(f.PilotId, module, DogmaEffect.PrototypeCloaking, new(1, 0, 0), SpacePosition.Zero, f.Clock.Now);
        Assert.Equal(speed * .1, activated.ShipAttributes[DogmaAttributeId.MaxVelocity], 7);
        Assert.Equal(module, store.Status(f.ShipId).CloakModuleId);
        Assert.Throws<InvalidDataException>(() =>
            store.Activate(f.PilotId, module, DogmaEffect.PrototypeCloaking, new(1, 0, 0), SpacePosition.Zero, f.Clock.Now)
        );
        store.Stop(f.PilotId, f.ShipId, module, f.Clock.Now);
        Assert.Equal(0, store.Status(f.ShipId).CloakModuleId);
        Assert.Equal(f.Clock.Now + TimeSpan.FromSeconds(15).Ticks, store.Status(f.ShipId).TargetingReadyAt);
        Assert.Equal(speed, f.Fitting.ShipAttributes(f.PilotId)[DogmaAttributeId.MaxVelocity], 7);
        using WorldDbContext saved = new(f.Db.Path);
        Assert.Equal(f.Clock.Now + TimeSpan.FromSeconds(30).Ticks, saved.Set<ModuleCooldown>().Find(module)!.ReadyAt);
    }

    [Theory]
    [InlineData(4383, Raven)]
    [InlineData(88710, Raven)]
    [InlineData(33915, 16227)]
    [InlineData(88711, 16227)]
    public void DrivesPayOnceApplySkillAndSignatureThenMoveExactlyOnce(int type, int hull)
    {
        using Fixture f = Prepare(hull);
        long module = f.Fit(type);
        Space(f);
        var store = Store(f);
        var origin = f.Db.Store.FindPilot(1000001)!.Position!;
        double signature = f.Fitting.ShipAttributes(f.PilotId)[DogmaAttributeId.SignatureRadius];
        var activated = store.Activate(f.PilotId, module, DogmaEffect.MicroJumpDrive, new(0, 1, 0), new(0, 20, 0), f.Clock.Now);
        Assert.Equal(9000, activated.Module.DurationMilliseconds);
        Assert.Equal(signature * (type is 88710 or 88711 ? 2.2 : 2.5), activated.ShipAttributes[DogmaAttributeId.SignatureRadius], 7);
        Assert.True(activated.Capacitor < 5000);
        Assert.Null(store.CompleteJump(f.PilotId, f.ShipId, module, activated.StartedAt, activated.EndsAt - 1));
        var result = store.CompleteJump(f.PilotId, f.ShipId, module, activated.StartedAt, activated.EndsAt)!;
        Assert.Equal(origin + new SpacePosition(0, 100000, 0), result.Position);
        Assert.Equal(new SpacePosition(0, 20, 0), result.Velocity);
        Assert.Null(store.CompleteJump(f.PilotId, f.ShipId, module, activated.StartedAt, activated.EndsAt));
        Assert.Equal(result.Position, f.Db.Store.FindPilot(1000001)!.Position);
        Assert.Equal(signature, f.Fitting.ShipAttributes(f.PilotId)[DogmaAttributeId.SignatureRadius], 7);
        Assert.Throws<InvalidDataException>(() =>
            store.Activate(f.PilotId, module, DogmaEffect.MicroJumpDrive, new(1, 0, 0), SpacePosition.Zero, activated.EndsAt)
        );
    }

    [Fact]
    public void ResidentJumpUsesTheLatestPositionAndCheckpointsThePaidCycleOnce()
    {
        using Fixture f = Prepare();
        long drive = f.Fit(LargeDrive);
        Space(f);
        long ship = f.ShipId;
        long pilot = f.PilotId;
        var store = Store(f);
        using LiveWorld live = new(new(f.Db.Path), f.Clock, _ => { });
        live.Economy.Warm();
        var origin = f.Db.Store.FindPilot(1000001)!.Position!;
        _ = f.Fitting.ShipAttributes(pilot);
        var moved = origin + new SpacePosition(500, 0, 0);
        SpacePosition destination;
        long reactivation;
        List<string> commands = [];
        using (var measured = SessionPerformance.Begin("resident micro jump", _ => { }, force: true, command: commands.Add)!)
        {
            var activated = store.Activate(pilot, drive, DogmaEffect.MicroJumpDrive, new(0, 1, 0), new(0, 20, 0), f.Clock.Now);
            reactivation = activated.ReadyAt;
            f.Db.Store.SetPosition(ship, moved);
            Assert.Null(store.CompleteJump(pilot, ship, drive, activated.StartedAt, activated.EndsAt - 1));
            var result = store.CompleteJump(pilot, ship, drive, activated.StartedAt, activated.EndsAt)!;
            destination = result.Position;
            Assert.Equal(moved + new SpacePosition(0, 100000, 0), destination);
            Assert.Null(store.CompleteJump(pilot, ship, drive, activated.StartedAt, activated.EndsAt));
            Assert.DoesNotContain(commands, sql => sql.Contains("\"World", StringComparison.Ordinal));
            Assert.InRange(measured.Connections, 0, 1);
        }
        using var checkpoint = Assert.IsType<WorldSnapshot>(live.CaptureSnapshot());
        checkpoint.Project();
        using WorldDbContext reopened = new(checkpoint.Database);
        var position = reopened.Set<FlightPosition>().Find(ship)!;
        Assert.Equal(destination, new SpacePosition(position.X, position.Y, position.Z));
        Assert.Equal(reactivation, reopened.Set<ModuleCooldown>().Find(drive)!.ReadyAt);
        Assert.Null(reopened.Set<ActiveFittedModule>().Find(drive));
    }

    [Theory]
    [InlineData("lock")]
    [InlineData("silent-lock")]
    [InlineData("offline")]
    [InlineData("owner")]
    [InlineData("docked")]
    public void RejectedCloaksLeaveCapacitorAndStateUnchanged(string reason)
    {
        using Fixture f = Prepare();
        long module = f.Fit(Prototype);
        Space(f);
        if (reason is "lock" or "silent-lock")
        {
            f.Db.Services.GetRequiredService<SharedTargetLocks>().Add(999, f.ShipId, reason == "silent-lock");
        }
        using (WorldDbContext world = new(f.Db.Path))
        {
            if (reason == "offline")
            {
                world.Set<OwnedItem>().Find(module)!.Online = false;
            }
            if (reason == "owner")
            {
                world.Set<OwnedItem>().Find(module)!.OwnerId++;
            }
            world.SaveChanges();
        }
        if (reason == "docked")
        {
            f.Db.Store.SetPosition(f.ShipId, null);
        }
        Assert.Throws<InvalidDataException>(() =>
            Store(f).Activate(f.PilotId, module, DogmaEffect.PrototypeCloaking, new(1, 0, 0), SpacePosition.Zero, f.Clock.Now)
        );
        using WorldDbContext verify = new(f.Db.Path);
        Assert.Equal(5000, verify.Set<ShipEnergy>().Find(f.ShipId)!.Charge);
        Assert.Empty(verify.Set<ActiveFittedModule>());
        Assert.Empty(verify.Set<SpatialNavigationState>());
    }
}
