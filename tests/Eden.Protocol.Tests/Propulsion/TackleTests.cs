using Eden.Data.Catalog;
using Eden.Data.World.Combat;
using Eden.Data.World.Fittings;
using Eden.Data.World.Inventory;
using Eden.Data.World.Propulsion;
using Eden.Data.World.Skills;
using Eden.Gameplay.Dogma;
using Eden.Gameplay.Flight;
using Eden.Gameplay.Inventory;
using Eden.Gameplay.Propulsion;
using Eden.Gameplay.Skills;
using Eden.Protocol.Docking;
using Eden.Protocol.Flight;
using Eden.Protocol.PilotSessions;
using Eden.Protocol.Propulsion;
using Eden.Protocol.Targeting;
using Eden.World.Catalog;
using Eden.World.Combat;
using Eden.World.Fittings;
using Eden.World.Inventory;
using Eden.World.Propulsion;
using Eden.World.Runtime;
using Eden.World.Skills;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Fixture = Eden.Protocol.Tests.Persistence.PassiveFittingTests.Fixture;

namespace Eden.Protocol.Tests.Propulsion;

public sealed class TackleTests
{
    private sealed class Clients : IDisposable
    {
        internal Fixture Fixture { get; }
        internal PilotCallContext Pilot { get; }
        internal PilotCallContext Wing { get; }
        internal TargetingHandler Targeting { get; }
        internal TargetingHandler WingTargeting { get; }
        internal TackleHandler Tackle { get; }
        internal TackleHandler WingTackle { get; }

        internal Clients(Fixture fixture)
        {
            Fixture = fixture;
            SharedSpace space = new();
            SharedTargetLocks locks = new();
            Pilot = Client(1000001);
            Wing = Client(1000002);
            Targeting = new(fixture.Clock, fixture.Fitting, locks);
            WingTargeting = new(fixture.Clock, fixture.Fitting, locks);
            Tackle = new(fixture.Clock, Store(fixture), Targeting, fixture.Inventory, fixture.Fitting);
            WingTackle = new(fixture.Clock, Store(fixture), WingTargeting, fixture.Inventory, fixture.Fitting);
            _ = Targeting.CallWire(Pilot, "dogmaIM", "AddTarget", [Wing.Pilot.ShipId]);
            fixture.Clock.Seconds += 30;
            Targeting.Advance(Pilot);
            Assert.True(Targeting.IsLocked(Wing.Pilot.ShipId));
            Pilot.Notifications.Clear();
            Wing.Notifications.Clear();

            PilotCallContext Client(long account)
            {
                var pilot = fixture.Db.Store.FindPilot(account)!;
                PilotCallContext context = new(
                    account,
                    pilot.Account,
                    [pilot],
                    p => new(fixture.Db.Store, p, fixture.Clock, space, fixture.Db.Objects, tackle: Store(fixture)),
                    account,
                    SignedSettings.Empty
                );
                context.Select(pilot);
                context.Flight.Snapshot();
                context.Flight.Updates.Clear();
                return context;
            }
        }

        public void Dispose()
        {
            Tackle.Dispose();
            WingTackle.Dispose();
            Targeting.Dispose();
            WingTargeting.Dispose();
            Pilot.Dispose();
            Wing.Dispose();
        }
    }

    private static long WingFit(Fixture f, int type)
    {
        long module = f.Fit(type);
        var wing = f.Db.Store.FindPilot(1000002)!;
        using WorldDbContext world = new(f.Db.Path);
        var item = world.Set<OwnedItem>().Find(module)!;
        item.OwnerId = wing.PilotId;
        item.LocationId = wing.ShipId;
        foreach (var skill in world.Set<PilotSkill>().Where(s => s.PilotId == f.PilotId).ToArray())
        {
            var saved =
                world.Set<PilotSkill>().Find(wing.PilotId, skill.TypeId)
                ?? world.Set<PilotSkill>().Add(new() { PilotId = wing.PilotId, TypeId = skill.TypeId }).Entity;
            saved.Points = skill.Points;
        }
        world.SaveChanges();
        return module;
    }

    internal static Fixture Prepare(params int[] types)
    {
        Fixture f = new();
        PassiveCatalogFixture.LoadTackleTiers(f.CatalogPath, types.Concat(new[] { 601, 3435, 3455, 3449 }).ToHashSet());
        using CatalogDbContext catalog = new(f.CatalogPath, writable: true);
        // Physics/tier cases use null security; legality is covered separately.
        catalog.Database.ExecuteSqlRaw(
            "INSERT OR REPLACE INTO SourceRecord VALUES ('client/systems','30000142','{{\"securityStatus\":0}}')"
        );
        foreach (
            var attribute in new[]
            {
                DogmaAttributeId.MediumSlots,
                DogmaAttributeId.LowSlots,
                DogmaAttributeId.CpuOutput,
                DogmaAttributeId.PowerOutput,
                DogmaAttributeId.CapacitorCapacity,
            }
        )
        {
            double value = attribute is DogmaAttributeId.MediumSlots or DogmaAttributeId.LowSlots ? 8 : 1000000;
            catalog.Database.ExecuteSql($"INSERT OR REPLACE INTO TypeAttribute VALUES (601,{(int)attribute},{value})");
        }
        return f;
    }

    private static IPilotTackle Store(Fixture f) => f.Db.Services.GetRequiredService<IPilotTackle>();

    private static long Wing(Fixture f) => f.Db.Store.FindPilot(1000002)!.ShipId;

    private static void Space(Fixture f, double separation = 1000)
    {
        var pilot = f.Db.Store.FindPilot(1000001)!;
        SpacePosition origin = new(pilot.X + pilot.Flight.StationRadius + 50000, pilot.Y, pilot.Z);
        f.Db.Store.SetPosition(f.ShipId, origin);
        f.Db.Store.SetPosition(Wing(f), origin + new SpacePosition(separation, 0, 0));
    }

    private static TackleActivation Activate(Fixture f, long module, DogmaEffect effect, long? target = null) =>
        Store(f)
            .Activate(
                f.PilotId,
                module,
                effect,
                target is long id ? new(id, TackleTargetKind.Ship) : null,
                target is long selected ? new HashSet<long> { selected } : [],
                f.Clock.Now
            );

    [Fact]
    public void AllEightyTwoStaticUnrestrictedProfilesHaveCompleteFittingDefinitions()
    {
        using Fixture f = new();
        PassiveCatalogFixture.LoadTackleTiers(f.CatalogPath);
        using CatalogDbContext catalog = new(f.CatalogPath);
        var types = catalog.Set<ItemType>().Where(t => new[] { 52, 65, 315, 1289 }.Contains(t.GroupId)).ToArray();
        var supported = types.Where(t => FittingCatalog.Definition(catalog, t.Id) is not null).ToArray();
        Assert.Equal(82, supported.Length);
        Assert.Equal(53, supported.Count(t => t.GroupId == 52));
        Assert.Equal(18, supported.Count(t => t.GroupId == 65));
        Assert.Equal(8, supported.Count(t => t.GroupId == 315));
        Assert.Equal(3, supported.Count(t => t.GroupId == 1289));
        Assert.All(
            types.Except(supported),
            t =>
                Assert.Equal(
                    15,
                    catalog.Set<SourceRecord>().Find("client/types", PassiveCatalog.Key(t.Id))!.Read<ClientTypes>().MetaGroupID
                )
        );
    }

    [Theory]
    [InlineData(526, -50, 5)]
    [InlineData(527, -60, 6)]
    [InlineData(4025, -55, 2)]
    [InlineData(14262, -55, 5)]
    [InlineData(14654, -60, 15)]
    [InlineData(30328, -35, 7)]
    public void WebTiersPayTheirSkilledCostAndSlowTheActualTarget(int type, double percent, double cost)
    {
        using Fixture f = Prepare(type);
        long module = f.Fit(type);
        Space(f);
        long wing = Wing(f);
        var baseline = f.Db.Store.FindPilot(1000002)!.Flight.MaxSpeed;
        var activation = Activate(f, module, DogmaEffect.StasisWebifier, wing);
        Assert.Equal(percent, activation.Module.WebPercent);
        Assert.Equal(cost * (type == 30328 ? 1 : .75), activation.Module.CapacitorNeed, 8);
        Assert.Equal(baseline * (1 + percent / 100), f.Db.Store.FindPilot(1000002)!.Flight.MaxSpeed, 4);
        Assert.Single(Store(f).Incoming(wing));
        Assert.Throws<InvalidDataException>(() => Activate(f, module, DogmaEffect.StasisWebifier, wing));
        Eden.Protocol.Tests.Combat.DroneCombatTriggerTests.AssertModule(f.Db.Path, f.ShipId, wing, f.Clock.Now);
        f.Clock.Milliseconds += 5000;
        Store(f).Stop(f.PilotId, f.ShipId, module);
        Store(f).Sweep(f.Clock.Now);
        Assert.Equal(baseline, f.Db.Store.FindPilot(1000002)!.Flight.MaxSpeed);
    }

    [Fact]
    public void WebsDiminishStrongestFirstAndRespectTargetResistance()
    {
        using Fixture f = Prepare(526, 527);
        long one = f.Fit(526);
        long two = f.Fit(527);
        using (CatalogDbContext catalog = new(f.CatalogPath, writable: true))
        {
            catalog.Database.ExecuteSqlRaw("INSERT OR IGNORE INTO DogmaAttribute VALUES (2115,'stasisWebifierResistance')");
            catalog.Database.ExecuteSqlRaw("INSERT OR REPLACE INTO TypeAttribute VALUES (601,2115,0.8)");
        }
        Space(f);
        double baseline = f.Db.Store.FindPilot(1000002)!.Flight.MaxSpeed;
        Activate(f, one, DogmaEffect.StasisWebifier, Wing(f));
        Activate(f, two, DogmaEffect.StasisWebifier, Wing(f));
        const double SecondWeight = .8691199808003975;
        Assert.Equal(baseline * (1 - .6 * .8) * (1 - .5 * .8 * SecondWeight), f.Db.Store.FindPilot(1000002)!.Flight.MaxSpeed, 4);
    }

    [Theory]
    [InlineData(3242, 1, 25, false)]
    [InlineData(3244, 1, 30, false)]
    [InlineData(447, 2, 5, true)]
    [InlineData(448, 2, 6, true)]
    [InlineData(14252, 3, 6, true)]
    [InlineData(14670, 10, 30, true)]
    [InlineData(40734, 3, 225, false)]
    [InlineData(32459, 0, 25, false)]
    public void DisruptionTiersHaveTheirOwnAbsoluteStrengthCostAndMwdPolicy(int type, double strength, double cost, bool scrambler)
    {
        using Fixture f = Prepare(type);
        long module = f.Fit(type);
        Space(f);
        var effect = scrambler ? DogmaEffect.WarpScrambler : DogmaEffect.WarpDisruptor;
        var activation = Activate(f, module, effect, Wing(f));
        Assert.Equal(strength, activation.Module.Strength);
        Assert.Equal(cost * (type == 32459 ? 1 : .75), activation.Module.CapacitorNeed, 8);
        Assert.Equal(scrambler, activation.Module.MwdBlock > 0);
        Assert.Equal(
            strength,
            Store(f).ShipAttributes(f.Db.Store.FindPilot(1000002)!.PilotId).GetValueOrDefault(DogmaAttributeId.WarpScrambleStatus),
            8
        );
    }

    [Theory]
    [InlineData(10998, 10000)]
    [InlineData(11640, 15000)]
    [InlineData(22875, 18000)]
    public void WarpCoreStrengthCountersTiedDisruptionAndRetainsReactivationDelay(int type, double duration)
    {
        using Fixture f = Prepare(type, 447);
        long scrambler = f.Fit(447);
        long core = f.Grant(type);
        var wing = f.Db.Store.FindPilot(1000002)!;
        using (WorldDbContext world = new(f.Db.Path))
        {
            var item = world.Set<OwnedItem>().Find(core)!;
            item.OwnerId = wing.PilotId;
            item.LocationId = wing.StationId;
            world.SaveChanges();
        }
        var fit = f.Db.Services.GetRequiredService<IPilotFitting>();
        f.Train(3455, 1);
        using (WorldDbContext world = new(f.Db.Path))
        {
            world
                .Set<PilotSkill>()
                .Add(
                    new()
                    {
                        PilotId = wing.PilotId,
                        TypeId = 3455,
                        Points = TrainedSkill.PointsForLevel(1, 5),
                    }
                );
            world.SaveChanges();
        }
        fit.Fit(wing.PilotId, [core], wing.StationId, (int)InventoryFlag.AutoFit);
        fit.SetOnline(wing.PilotId, wing.ShipId, core, true);
        Space(f);
        Activate(f, scrambler, DogmaEffect.WarpScrambler, wing.ShipId);
        var activation = Store(f).Activate(wing.PilotId, core, DogmaEffect.WarpCoreStabilizer, null, new HashSet<long>(), f.Clock.Now);
        Assert.Equal(80, activation.Module.CapacitorNeed);
        Assert.Equal(duration, activation.Module.DurationMilliseconds);
        Assert.Equal(0, Store(f).ShipAttributes(wing.PilotId)[DogmaAttributeId.WarpScrambleStatus]);
        Assert.Equal(f.Clock.Now + (long)((duration + 150000) * TimeSpan.TicksPerMillisecond), activation.ReadyAt);
        Store(f).Stop(wing.PilotId, wing.ShipId, core);
        Assert.Throws<InvalidDataException>(() =>
            Store(f).Activate(wing.PilotId, core, DogmaEffect.WarpCoreStabilizer, null, new HashSet<long>(), f.Clock.Now)
        );
    }

    [Theory]
    [InlineData(33981, .2)]
    [InlineData(33983, .25)]
    [InlineData(33985, .3)]
    public void HyperspatialAcceleratorsChangeSavedPilotWarpFlightAndRestoreOffline(int type, double bonus)
    {
        using Fixture f = Prepare(type);
        double baseline = f.Db.Store.FindPilot(1000001)!.Flight.WarpSpeed;
        long module = f.Fit(type);
        using CatalogDbContext catalog = new(f.CatalogPath);
        double unit = PilotFittingStore.ReadAttributes(catalog, 601)[DogmaAttributeId.BaseWarpSpeed];
        Assert.Equal(baseline + unit * bonus, f.Db.Store.FindPilot(1000001)!.Flight.WarpSpeed, 8);
        f.Fitting.SetOnline(f.PilotId, f.ShipId, module, false);
        Assert.Equal(baseline, f.Db.Store.FindPilot(1000001)!.Flight.WarpSpeed, 8);
    }

    [Theory]
    [InlineData("range")]
    [InlineData("lock")]
    [InlineData("offline")]
    [InlineData("skill")]
    public void InvalidActivationsCannotSpendCapacitorOrInstallAnEffect(string reason)
    {
        using Fixture f = Prepare(526);
        long module = f.Fit(526);
        if (reason == "offline")
        {
            f.Fitting.SetOnline(f.PilotId, f.ShipId, module, false);
        }
        Space(f, reason == "range" ? 20000 : 1000);
        if (reason == "skill")
        {
            f.Train(3435, 0);
        }
        Assert.Throws<InvalidDataException>(() =>
            Store(f)
                .Activate(
                    f.PilotId,
                    module,
                    DogmaEffect.StasisWebifier,
                    new(Wing(f), TackleTargetKind.Ship),
                    reason == "lock" ? new HashSet<long>() : new HashSet<long> { Wing(f) },
                    f.Clock.Now
                )
        );
        using WorldDbContext world = new(f.Db.Path);
        Assert.Empty(world.Set<WorldTackle>());
        Assert.Null(world.Set<ModuleCooldown>().Find(module));
    }

    [Fact]
    public void RemoteWebRepeatsKeepHudAndEwarStableAndStopAfterPaidCycle()
    {
        using Fixture f = Prepare(526);
        long module = f.Fit(526);
        Space(f);
        using Clients clients = new(f);
        double speed = clients.Wing.Pilot.Flight.MaxSpeed;
        Assert.Same(
            PilotCallResult.None,
            clients.Tackle.Call(clients.Pilot, "dogmaIM", "Activate", [module, "remoteWebifierFalloff", clients.Wing.Pilot.ShipId, 100L])
        );
        clients.WingTackle.Advance(clients.Wing);
        Assert.Equal(speed / 2, clients.Wing.Pilot.Flight.MaxSpeed, 4);
        var ewar = Assert.Single(clients.Wing.Notifications.OfType<TackleEwarChanged>());
        Assert.Equal(new object?[] { f.ShipId, module, Wing(f), "webify" }, ewar.Arguments);
        Assert.True(ewar.Active);
        Assert.Contains(clients.Wing.Flight.Updates, u => u.Command is DestinyCommand.SetMaxSpeed);
        clients.Pilot.Notifications.Clear();
        clients.Wing.Notifications.Clear();
        for (int cycle = 0; cycle < 3; cycle++)
        {
            f.Clock.Milliseconds += 5000;
            clients.Tackle.Advance(clients.Pilot);
            clients.WingTackle.Advance(clients.Wing);
            Assert.NotNull(clients.Tackle.ActiveEffect(clients.Pilot.Pilot, module));
            Assert.Empty(clients.Pilot.Notifications.OfType<ShipEffectChanged>());
            Assert.Empty(clients.Wing.Notifications.OfType<TackleEwarChanged>());
            Assert.Equal(speed / 2, clients.Wing.Pilot.Flight.MaxSpeed, 4);
        }
        Assert.Same(PilotCallResult.None, clients.Tackle.Call(clients.Pilot, "dogmaIM", "Deactivate", [module, "remoteWebifierFalloff"]));
        Assert.Single(Store(f).Incoming(Wing(f)));
        f.Clock.Milliseconds += 5000;
        clients.Tackle.Advance(clients.Pilot);
        clients.WingTackle.Advance(clients.Wing);
        Assert.False(Assert.Single(clients.Pilot.Notifications.OfType<ShipEffectChanged>()).Start);
        Assert.False(Assert.Single(clients.Wing.Notifications.OfType<TackleEwarChanged>()).Active);
        Assert.Equal(speed, clients.Wing.Pilot.Flight.MaxSpeed);
    }

    [Theory]
    [InlineData(447, true)]
    [InlineData(3242, false)]
    public void ScramblerStopsRunningMwdAndBlocksRestartWhileDisruptorAllowsIt(int type, bool blocks)
    {
        using Fixture f = Prepare(type, 434);
        long module = f.Fit(type);
        long mwd = WingFit(f, 434);
        Space(f);
        using Clients clients = new(f);
        var propulsion = f.Db.Services.GetRequiredService<IPilotPropulsion>();
        using PropulsionHandler handler = new(f.Clock, propulsion);
        Assert.Null(handler.CallWire(clients.Wing, "dogmaIM", "Activate", [mwd, "moduleBonusMicrowarpdrive", null, 100L]));
        double boosted = clients.Wing.Pilot.Flight.MaxSpeed;
        Assert.NotNull(handler.ActiveEffect(clients.Wing.Pilot, mwd));
        Assert.Same(
            PilotCallResult.None,
            clients.Tackle.Call(
                clients.Pilot,
                "dogmaIM",
                "Activate",
                [module, blocks ? "warpScrambleBlockMWDWithNPCEffect" : "warpDisrupt", clients.Wing.Pilot.ShipId, 100L]
            )
        );
        clients.WingTackle.Advance(clients.Wing);
        handler.Advance(clients.Wing);
        Assert.Equal(blocks, clients.Wing.Flight.MicrowarpdriveBlocked);
        Assert.Equal(blocks, handler.ActiveEffect(clients.Wing.Pilot, mwd) is null);
        Assert.Equal(blocks, clients.Wing.Pilot.Flight.MaxSpeed < boosted);
        if (blocks)
        {
            Assert.Throws<InvalidDataException>(() => propulsion.Activate(clients.Wing.Pilot.PilotId, mwd, f.Clock.Now));
            clients.Tackle.Cancel(clients.Pilot);
            clients.WingTackle.Advance(clients.Wing);
            Assert.False(clients.Wing.Flight.MicrowarpdriveBlocked);
        }
    }

    [Fact]
    public void VictimUpdatingBeforeTheSourceAtCycleBoundariesNeverLosesWebOrScramblerStrength()
    {
        using Fixture f = Prepare(527, 447);
        long web = f.Fit(527);
        long scram = f.Fit(447);
        Space(f);
        using Clients clients = new(f);
        double baseline = clients.Wing.Pilot.Flight.MaxSpeed;
        Assert.Same(
            PilotCallResult.None,
            clients.Tackle.Call(clients.Pilot, "dogmaIM", "Activate", [web, "remoteWebifierFalloff", Wing(f), 100L])
        );
        Assert.Same(
            PilotCallResult.None,
            clients.Tackle.Call(clients.Pilot, "dogmaIM", "Activate", [scram, "warpScrambleBlockMWDWithNPCEffect", Wing(f), 100L])
        );
        clients.WingTackle.Advance(clients.Wing);
        clients.Wing.Notifications.Clear();
        for (int cycle = 0; cycle < 3; cycle++)
        {
            f.Clock.Milliseconds += 5000;
            // Separate connection loops have no ordering guarantee. The target
            // must retain the live row while the source decides its next cycle.
            clients.Wing.Flight.Checkpoint();
            clients.WingTackle.Advance(clients.Wing);
            Assert.Equal(baseline * .4, clients.Wing.Pilot.Flight.MaxSpeed, 4);
            Assert.Equal(2, Store(f).ShipAttributes(clients.Wing.Pilot.PilotId)[DogmaAttributeId.WarpScrambleStatus]);
            Assert.Empty(clients.Wing.Notifications.OfType<TackleEwarChanged>());
            clients.Tackle.Advance(clients.Pilot);
            clients.WingTackle.Advance(clients.Wing);
            Assert.Equal(baseline * .4, clients.Wing.Pilot.Flight.MaxSpeed, 4);
            Assert.Empty(clients.Wing.Notifications.OfType<TackleEwarChanged>());
        }
        clients.Tackle.Cancel(clients.Pilot);
        clients.WingTackle.Advance(clients.Wing);
        Assert.Equal(baseline, clients.Wing.Pilot.Flight.MaxSpeed);
        Assert.Equal(0, Store(f).ShipAttributes(clients.Wing.Pilot.PilotId).GetValueOrDefault(DogmaAttributeId.WarpScrambleStatus));
        Assert.Equal(2, clients.Wing.Notifications.OfType<TackleEwarChanged>().Count(e => !e.Active));
    }

    [Fact]
    public void NewPointCancelsAlignmentAndCoreStrengthAllowsOnlyTiedDisruption()
    {
        using Fixture f = Prepare(447, 3242, 10998);
        long scram = f.Fit(447);
        long disruptor = f.Fit(3242);
        long core = WingFit(f, 10998);
        Space(f);
        using Clients clients = new(f);
        long destination = f.Db.Objects.First(o => o.DestinationGateId.HasValue).Id;
        Assert.Null(clients.Wing.Flight.CallWire("beyonce", "CmdWarpToStuff", ["item", destination]));
        Assert.True(clients.Wing.Flight.CanTarget);
        Assert.Same(
            PilotCallResult.None,
            clients.Tackle.Call(clients.Pilot, "dogmaIM", "Activate", [scram, "warpScrambleBlockMWDWithNPCEffect", Wing(f), 100L])
        );
        clients.Wing.Flight.Checkpoint();
        Assert.Contains(clients.Wing.Flight.Updates, u => u.Command is DestinyCommand.Stop);
        Assert.IsType<PilotCallResult.Failure>(clients.Wing.Flight.CallWire("beyonce", "CmdWarpToStuff", ["item", destination]));
        Assert.Same(
            PilotCallResult.None,
            clients.WingTackle.Call(clients.Wing, "dogmaIM", "Activate", [core, "warpCoreStabilizerActive", null, 100L])
        );
        Assert.Null(clients.Wing.Flight.CallWire("beyonce", "CmdWarpToStuff", ["item", destination]));
        Assert.True(clients.Wing.Flight.MicrowarpdriveBlocked);
        Assert.Same(
            PilotCallResult.None,
            clients.Tackle.Call(clients.Pilot, "dogmaIM", "Activate", [disruptor, "warpDisrupt", Wing(f), 100L])
        );
        clients.Wing.Flight.Checkpoint();
        Assert.IsType<PilotCallResult.Failure>(clients.Wing.Flight.CallWire("beyonce", "CmdWarpToStuff", ["item", destination]));
        clients.Tackle.Cancel(clients.Pilot);
        f.Clock.Milliseconds += 10000;
        clients.WingTackle.Advance(clients.Wing);
        Assert.Null(clients.WingTackle.ActiveEffect(clients.Wing.Pilot, core));
        Assert.Throws<InvalidDataException>(() =>
            Store(f).Activate(clients.Wing.Pilot.PilotId, core, DogmaEffect.WarpCoreStabilizer, null, new HashSet<long>(), f.Clock.Now)
        );
    }

    [Theory]
    [InlineData("source")]
    [InlineData("target")]
    [InlineData("unlock")]
    public void LosingEitherConnectionOrTargetLockRemovesRemoteEffects(string departure)
    {
        using Fixture f = Prepare(526);
        long module = f.Fit(526);
        Space(f);
        using Clients clients = new(f);
        Assert.Same(
            PilotCallResult.None,
            clients.Tackle.Call(clients.Pilot, "dogmaIM", "Activate", [module, "remoteWebifierFalloff", Wing(f), 100L])
        );
        if (departure == "source")
        {
            clients.Pilot.Flight.Dispose();
        }
        else if (departure == "target")
        {
            clients.Wing.Flight.Dispose();
        }
        else
        {
            _ = clients.Targeting.CallWire(clients.Pilot, "dogmaIM", "RemoveTarget", [Wing(f)]);
        }
        clients.Targeting.Advance(clients.Pilot);
        clients.Tackle.Advance(clients.Pilot);
        Assert.Empty(Store(f).Incoming(Wing(f)));
    }

    [Fact]
    public void NpcWebsUseDiminishingSpeedAndPreserveCruiseRatio()
    {
        var attributes = new Dictionary<DogmaAttributeId, double>
        {
            [DogmaAttributeId.MaxVelocity] = 500,
            [DogmaAttributeId.EntityCruiseSpeed] = 200,
            [DogmaAttributeId.StasisWebifierResistance] = .5,
        };
        TackleModule web = new(1, 526, DogmaEffect.StasisWebifier, 5000, 5, 10000, 0, -50, 0, 0);
        var effective = TackleDefinitions.NpcAttributes(attributes, [web, web with { ItemId = 2 }]);
        Assert.Equal(500 * .75 * (1 - .25 * .8691199808003975), effective[DogmaAttributeId.MaxVelocity], 8);
        Assert.Equal(.4, effective[DogmaAttributeId.EntityCruiseSpeed] / effective[DogmaAttributeId.MaxVelocity], 8);
    }

    [Fact]
    public void CoreOnlinePenaltiesAndFitLimitsApplyBeforeActivation()
    {
        using Fixture f = Prepare(10998, 33981);
        var baseline = f.Fitting.ShipAttributes(f.PilotId);
        long core = f.Fit(10998);
        var penalized = f.Fitting.ShipAttributes(f.PilotId);
        Assert.Equal(baseline[DogmaAttributeId.MaxTargetRange] / 2, penalized[DogmaAttributeId.MaxTargetRange], 8);
        Assert.Equal(baseline[DogmaAttributeId.ScanResolution] / 2, penalized[DogmaAttributeId.ScanResolution], 8);
        long duplicate = f.Grant(10998);
        Assert.Throws<InvalidDataException>(() => f.Fitting.Fit(f.PilotId, [duplicate], f.StationId, (int)InventoryFlag.AutoFit));
        f.Fitting.SetOnline(f.PilotId, f.ShipId, core, false);
        Assert.Equal(baseline[DogmaAttributeId.MaxTargetRange], f.Fitting.ShipAttributes(f.PilotId)[DogmaAttributeId.MaxTargetRange], 8);
        for (int i = 0; i < 3; i++)
        {
            _ = f.Fit(33981);
        }
        long fourth = f.Grant(33981);
        Assert.Throws<InvalidDataException>(() => f.Fitting.Fit(f.PilotId, [fourth], f.StationId, (int)InventoryFlag.AutoFit));
    }

    [Fact]
    public void CoreStrengthExpiresWithoutAdvancingItsOwningConnection()
    {
        using Fixture f = Prepare(10998);
        using LiveWorld live = new(new(f.Db.Path), f.Clock, _ => { });
        Store(f).Sweep(f.Clock.Now);
        long module = f.Fit(10998);
        Space(f);
        var activation = Activate(f, module, DogmaEffect.WarpCoreStabilizer);
        Assert.Equal(-2, Store(f).ShipAttributes(f.PilotId)[DogmaAttributeId.WarpScrambleStatus]);
        f.Clock.Milliseconds += 10000;
        Store(f).Sweep(f.Clock.Now);
        Assert.Equal(0, Store(f).ShipAttributes(f.PilotId).GetValueOrDefault(DogmaAttributeId.WarpScrambleStatus));
        using WorldDbContext world = new(f.Db.Path);
        Assert.Empty(world.Set<ActiveFittedModule>());
        Assert.Equal(activation.ReadyAt, world.Set<ModuleCooldown>().Find(module)!.ReadyAt);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RestartAndVersionThirtyFourMigrationClearEffectsAndRetainPaymentsAndDeadlines(bool downgrade)
    {
        using Fixture f = Prepare(10998, 526);
        long core = f.Fit(10998);
        long web = f.Fit(526);
        Space(f);
        var activation = Activate(f, core, DogmaEffect.WarpCoreStabilizer);
        var last = Activate(f, web, DogmaEffect.StasisWebifier, Wing(f));
        if (downgrade)
        {
            using WorldDbContext world = new(f.Db.Path);
            world.Database.ExecuteSqlRaw(TestWorld.DropSafetySchema + "DROP TABLE WorldTackle; PRAGMA user_version=34;");
        }
        WorldInitialization.Initialize(new(f.Db.Path), f.Clock);
        using WorldDbContext saved = new(f.Db.Path);
        Assert.Empty(saved.Set<ActiveFittedModule>());
        Assert.Empty(saved.Set<WorldTackle>());
        Assert.Equal(last.Capacitor, saved.Set<ShipEnergy>().Find(f.ShipId)!.Charge, 8);
        Assert.Equal(activation.ReadyAt, saved.Set<ModuleCooldown>().Find(core)!.ReadyAt);
        Assert.Equal(WorldMigration.CurrentVersion, saved.Database.SqlQueryRaw<int>("PRAGMA user_version").AsEnumerable().Single());
    }

    [Fact]
    public void InsufficientCapacitorStopsRepeatingWebWithoutChargingOrKeepingItsDebuff()
    {
        using Fixture f = Prepare(526);
        long module = f.Fit(526);
        Space(f);
        using Clients clients = new(f);
        Assert.Same(
            PilotCallResult.None,
            clients.Tackle.Call(clients.Pilot, "dogmaIM", "Activate", [module, "remoteWebifierFalloff", Wing(f), 100L])
        );
        f.Clock.Milliseconds += 5000;
        using (WorldDbContext world = new(f.Db.Path))
        {
            var energy = world.Set<ShipEnergy>().Find(f.ShipId)!;
            energy.Charge = 0;
            energy.UpdatedAt = f.Clock.Now;
            world.SaveChanges();
        }
        clients.Tackle.Advance(clients.Pilot);
        Assert.Empty(Store(f).Incoming(Wing(f)));
        Assert.Null(clients.Tackle.ActiveEffect(clients.Pilot.Pilot, module));
        using WorldDbContext saved = new(f.Db.Path);
        Assert.Equal(0, saved.Set<ShipEnergy>().Find(f.ShipId)!.Charge);
    }
}
