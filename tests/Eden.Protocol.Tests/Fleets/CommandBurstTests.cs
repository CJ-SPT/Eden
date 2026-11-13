using Eden.Data.Catalog;
using Eden.Data.World.Characters;
using Eden.Data.World.Combat;
using Eden.Data.World.Fittings;
using Eden.Data.World.Inventory;
using Eden.Gameplay.Ammunition;
using Eden.Gameplay.Combat;
using Eden.Gameplay.Docking;
using Eden.Gameplay.Dogma;
using Eden.Gameplay.Fleets;
using Eden.Gameplay.Flight;
using Eden.Gameplay.Inventory;
using Eden.Infrastructure.Diagnostics;
using Eden.Protocol.Docking;
using Eden.Protocol.Fleets;
using Eden.Protocol.Flight;
using Eden.Protocol.PilotSessions;
using Eden.World.Ammunition;
using Eden.World.Catalog;
using Eden.World.Fittings;
using Eden.World.Runtime;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Fixture = Eden.Protocol.Tests.Persistence.PassiveFittingTests.Fixture;

namespace Eden.Protocol.Tests.Fleets;

public sealed class CommandBurstTests
{
    private const int Ferox = 16227;
    private const int ArmorBurst = 42526;
    private const int ArmorReinforcement = 42834;
    private const int ShieldExtension = 42696;
    private const int ArmorReinforcementCollection = 15;
    private const long AccountId = 1000001;
    private const long FleetmateAccount = 1000002;
    private const string Effect = "moduleBonusWarfareLinkArmor";

    [Fact]
    public void PublishedModulesAndChargesMatchPinnedDogma()
    {
        using Fixture fixture = new();
        PassiveCatalogFixture.LoadCommandBursts(fixture.CatalogPath);
        using CatalogDbContext catalog = new(fixture.CatalogPath);
        var types = catalog.Set<ItemType>().ToArray();
        var modules = types.Where(type => type.GroupId == CommandBurstRules.Group).ToArray();
        var charges = types.Where(type => CommandBurstRules.IsCharge(type.GroupId)).ToArray();
        Assert.Equal(18, modules.Length);
        Assert.Equal(19, charges.Length);
        foreach (var type in modules)
        {
            Assert.Equal(new(ModuleRack.High, false, false, 0, 0), FittingCatalog.Definition(catalog, type.Id));
            Assert.True(InventoryDefinitions.RequiresSpecificShip(catalog.FittingAttributes(type.Id)));
        }
        Assert.All(charges, type => Assert.True(AmmunitionCatalog.Supported(catalog, type.Id, type.GroupId)));
        Assert.Throws<InvalidDataException>(() => PilotFittingStore.CheckHull(catalog, ArmorBurst, 601));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void PaidPulseBoostsFleetmatesAndExpiresAtTheSavedDeadline(bool resident)
    {
        using var fixture = Prepare();
        long module = fixture.Fit(ArmorBurst);
        long source = fixture.ShipId;
        var target = fixture.Db.Store.FindPilot(FleetmateAccount)!;
        var store = fixture.Db.Services.GetRequiredService<IPilotCommandBursts>();
        var ammo = fixture.Db.Services.GetRequiredService<IPilotAmmunition>();
        long wrong = fixture.Grant(ShieldExtension, 2);
        Assert.Throws<InvalidDataException>(() =>
            ammo.Load(fixture.PilotId, source, [module], [wrong], fixture.StationId, fixture.Clock.Now)
        );
        Assert.Empty(ammo.Read(fixture.PilotId, source));
        Assert.Equal(2, fixture.Inventory.List(fixture.PilotId, fixture.StationId).Single(i => i.Id == wrong).Quantity);
        Load(fixture, module);
        fixture.Clock.AdvanceWallTime(TimeSpan.FromMinutes(1));
        Place(fixture, target);
        var fleets = fixture.Db.Services.GetRequiredService<ICooperativeFleets>();
        var pilot = fixture.Db.Store.FindPilot(AccountId)!;
        using ResidentFitCache? cache = resident ? new(new(fixture.Db.Path)) : null;
        using LiveWorld? live = resident ? new(new(fixture.Db.Path), fixture.Clock, _ => { }) : null;
        using var system = live?.UseSystem(pilot.SystemId);

        CommandBurstCandidate[] candidates = [new(target.PilotId, target.ShipId, new(1000, 0, 0))];
        double baseArmor = fixture.Fitting.ShipAttributes(target.PilotId)[DogmaAttributeId.ArmorHitPoints];
        CommandBurstPulse Fire() =>
            store.Pulse(fixture.PilotId, source, module, DogmaEffect.ArmorCommandBurst, candidates, fixture.Clock.Now);

        var solo = Fire();
        Assert.Equal([source], solo.Recipients);
        Assert.Empty(store.Incoming(target.ShipId, fixture.Clock.Now));
        Assert.Equal(3, solo.Magazine.Quantity);
        Assert.Throws<InvalidDataException>(() => Fire());
        Assert.Equal(3, Assert.Single(ammo.Read(fixture.PilotId, source)).Quantity);

        Join(fleets, pilot, target);
        fixture.Clock.AdvanceWallTime(TimeSpan.FromTicks(solo.Module.CycleTicks));
        var paid = Fire();
        Assert.Equal([source, target.ShipId], paid.Recipients);
        Assert.Equal(2, paid.Magazine.Quantity);
        Assert.Equal(25, paid.Module.Capacitor);
        Assert.Equal(30375, paid.Module.Radius, 6);
        var boost = Assert.Single(store.Incoming(target.ShipId, fixture.Clock.Now));
        Assert.Equal(ArmorReinforcementCollection, boost.CollectionId);
        Assert.Equal(8, boost.Value, 6);
        Assert.Equal(fixture.Clock.Now + TimeSpan.FromSeconds(90).Ticks, boost.ExpiresAt);
        Assert.Equal(baseArmor * 1.08, fixture.Fitting.ShipAttributes(target.PilotId)[DogmaAttributeId.ArmorHitPoints], 6);
        if (live is not null)
        {
            using (var metrics = SessionPerformance.Begin("warmed command burst reads", _ => { }, force: true)!)
            {
                for (int sample = 0; sample < 12; sample++)
                {
                    Assert.Single(store.Incoming(target.ShipId, fixture.Clock.Now));
                    Assert.Equal(baseArmor * 1.08, fixture.Fitting.ShipAttributes(target.PilotId)[DogmaAttributeId.ArmorHitPoints], 6);
                }
                Assert.Equal(0, metrics.FittingCalculations);
                Assert.Equal(0, metrics.Commands);
                Assert.Equal(0, metrics.Connections);
            }
            using var checkpoint = Assert.IsType<WorldSnapshot>(live.CaptureSnapshot());
            checkpoint.Project();
            using WorldDbContext saved = new(checkpoint.Database);
            Assert.Equal(paid.ReadyAt, saved.Set<ModuleCooldown>().Find(module)!.ReadyAt);
            Assert.Equal(paid.Capacitor, saved.Set<ShipEnergy>().Find(source)!.Charge);
            Assert.Equal(paid.ReadyAt, saved.Set<SavedCrimewatch>().Find(fixture.PilotId)!.WeaponsUntil);
            var savedBoost = Assert.Single(saved.Set<Eden.Data.World.Fleets.WorldCommandBurst>().Where(b => b.ShipId == target.ShipId));
            Assert.Equal(boost.ExpiresAt, savedBoost.ExpiresAt);
        }
        fixture.Clock.AdvanceWallTime(TimeSpan.FromTicks(boost.ExpiresAt - fixture.Clock.Now - 1));
        Assert.Single(store.Incoming(target.ShipId, fixture.Clock.Now));
        fixture.Clock.AdvanceWallTime(TimeSpan.FromTicks(1));
        Assert.Empty(store.Incoming(target.ShipId, fixture.Clock.Now));
        Assert.Equal(baseArmor, fixture.Fitting.ShipAttributes(target.PilotId)[DogmaAttributeId.ArmorHitPoints], 6);

        candidates[0] = candidates[0] with { Position = new(1_000_000, 0, 0) };
        Assert.Equal([source], Fire().Recipients);
        Assert.Empty(store.Incoming(target.ShipId, fixture.Clock.Now));
    }

    [Fact]
    public void NativeRouteSendsBurstVisualsChargeAndRecipientState()
    {
        using var fixture = Prepare();
        long module = fixture.Fit(ArmorBurst);
        Load(fixture, module);
        fixture.Clock.AdvanceWallTime(TimeSpan.FromMinutes(1));
        var target = fixture.Db.Store.FindPilot(FleetmateAccount)!;
        Place(fixture, target);
        var pilot = fixture.Db.Store.FindPilot(AccountId)!;
        target = fixture.Db.Store.FindPilot(FleetmateAccount)!;
        Join(fixture.Db.Services.GetRequiredService<ICooperativeFleets>(), pilot, target);
        SharedSpace space = new();
        using var sourceContext = Context(pilot);
        using var targetContext = Context(target);
        using var sourceScope = fixture.Db.Services.CreateScope();
        using var targetScope = fixture.Db.Services.CreateScope();
        var route = sourceScope.ServiceProvider.GetRequiredService<ModuleActivationHandler>();
        var sourceHandler = sourceScope.ServiceProvider.GetRequiredService<CommandBurstHandler>();
        var targetHandler = targetScope.ServiceProvider.GetRequiredService<CommandBurstHandler>();
        Assert.IsType<PilotCallResult.Failure>(route.Call(sourceContext, "dogmaIM", "Activate", [module, Effect, target.ShipId, 1L]));
        Assert.Same(PilotCallResult.None, route.Call(sourceContext, "dogmaIM", "Activate", [module, Effect, null, 1L]));
        Assert.Equal(DogmaEffect.ArmorCommandBurst, sourceHandler.ActiveEffect(pilot, module)!.Effect);
        Assert.Contains(sourceContext.Notifications, notification => notification.Name == "OnFleetBoosted");
        Assert.Contains(
            sourceContext.Flight.Updates,
            update => update.Command is DestinyCommand.CommandBurstVisual { Graphic: "effects.WarfareLinkSphereArmor" }
        );
        targetContext.Flight.RefreshPeers();
        Assert.Contains(
            targetContext.Flight.Updates,
            u => u.Command is DestinyCommand.CommandBurstVisual { Graphic: "effects.WarfareLinkArmor" } visual && visual.Id == target.ShipId
        );
        targetHandler.Advance(targetContext);
        var update = Assert.Single(targetContext.Flight.Updates.Select(u => u.Command).OfType<DestinyCommand.DbuffUpdated>());
        Assert.Equal(target.ShipId, update.Id);
        Assert.Equal(8, Assert.Single(update.Boosts).Value);
        Assert.Same(PilotCallResult.None, route.Call(sourceContext, "dogmaIM", "Deactivate", [module, Effect]));
        fixture.Clock.AdvanceWallTime(TimeSpan.FromMinutes(1));
        sourceHandler.Advance(sourceContext);
        Assert.Null(sourceHandler.ActiveEffect(pilot, module));
        Assert.Single(fixture.Db.Services.GetRequiredService<IPilotCommandBursts>().Incoming(pilot.ShipId, fixture.Clock.Now));

        PilotCallContext Context(DockedPilot selected)
        {
            PilotCallContext context = new(
                AccountId,
                selected.Account,
                [selected],
                p => new(fixture.Db.Store, p, fixture.Clock, space, fixture.Db.Objects),
                AccountId,
                SignedSettings.Empty
            );
            context.Select(selected);
            context.Flight.Snapshot();
            context.Flight.Updates.Clear();
            return context;
        }
    }

    [Fact]
    public void CollectionUsesStrongestValueWithAnIndependentFallback()
    {
        CommandBurstBoost[] rows = [new(1, -10, 10), new(1, -8, 20), new(2, 8, 20), new(2, 12, 20)];
        CommandBurstCollection Definition(int id) => new(id, id == 1, DogmaOperation.PostPercent, []);
        Assert.Equal([rows[0], rows[3]], CommandBurstRules.Strongest(rows, Definition));
        Assert.Equal([rows[1], rows[3]], CommandBurstRules.Strongest(rows.Where(row => row.ExpiresAt > 10), Definition));
    }

    private static Fixture Prepare()
    {
        Fixture fixture = new();
        PassiveCatalogFixture.LoadCommandBursts(fixture.CatalogPath);
        using WorldDbContext world = new(fixture.Db.Path);
        using CatalogDbContext catalog = new(fixture.CatalogPath, writable: true);
        string system = fixture.Db.Store.FindPilot(AccountId)!.SystemId.ToString(System.Globalization.CultureInfo.InvariantCulture);
        string security = "{\"securityStatus\":0}";
        catalog.Database.ExecuteSql($"INSERT OR REPLACE INTO SourceRecord VALUES ('client/systems',{system},{security})");
        world.Set<Account>().Find(AccountId)!.CloneGrade = AccountCloneGrade.Omega;
        world.Ships.Find(fixture.ShipId)!.TypeId = Ferox;
        world.SaveChanges();
        return fixture;
    }

    private static void Load(Fixture fixture, long module)
    {
        long charge = fixture.Grant(ArmorReinforcement, 4);
        var ammo = fixture.Db.Services.GetRequiredService<IPilotAmmunition>();
        ammo.Load(fixture.PilotId, fixture.ShipId, [module], [charge], fixture.StationId, fixture.Clock.Now);
    }

    private static void Place(Fixture fixture, DockedPilot target)
    {
        fixture.Db.Store.SetPosition(fixture.ShipId, SpacePosition.Zero, target.SystemId);
        fixture.Db.Store.SetPosition(target.ShipId, new(1000, 0, 0), target.SystemId);
    }

    private static void Join(ICooperativeFleets fleets, DockedPilot pilot, DockedPilot target)
    {
        fleets.Update(Guid.NewGuid(), new(pilot.PilotId, pilot.CorpId, pilot.ShipId, pilot.ShipTypeId, pilot.SystemId, pilot.Position));
        fleets.Update(
            Guid.NewGuid(),
            new(target.PilotId, target.CorpId, target.ShipId, target.ShipTypeId, target.SystemId, target.Position)
        );
        var fleet = fleets.Create(pilot.PilotId);
        fleets.Invite(
            pilot.PilotId,
            fleet.Id,
            target.PilotId,
            fleet.Members.Single(m => m.Pilot.Id == pilot.PilotId).Placement with
            {
                Role = FleetRole.Member,
            }
        );
        fleets.Accept(target.PilotId, fleet.Id);
    }
}
