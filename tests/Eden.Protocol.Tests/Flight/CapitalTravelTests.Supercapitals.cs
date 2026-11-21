using Eden.Data.World.Sovereignty;
using Eden.Gameplay.Combat;
using Eden.Gameplay.Docking;
using Eden.Gameplay.Dogma;
using Eden.Gameplay.Flight;
using Eden.Gameplay.Industry;
using Eden.Gameplay.Inventory;
using Eden.Gameplay.Sovereignty;
using Eden.Gameplay.Structures;
using Eden.Protocol.PilotSessions;
using Eden.World.Catalog;
using Eden.World.Fittings;
using Eden.World.Industry;
using Eden.World.Storage;
using Microsoft.Extensions.DependencyInjection;

namespace Eden.Protocol.Tests.Flight;

public sealed partial class CapitalTravelTests
{
    private const int Avatar = 11567;
    private const int Aeon = 23919;
    private const int Keepstar = 35834;
    private const int Helium = 16274;
    private const int NavigationCorporation = 98000001;

    [Fact]
    public async Task AvatarBridgesRapierThenJumpsAndDocksAtKeepstar()
    {
        using Fixture fixture = new(emitterHull: Rapier, generator: CapitalTravelRules.StandardGenerator, structures: true);
        var (beacon, _) = fixture.PrepareNavigation();
        fixture.AdmitBridgePilot(NavigationCorporation);
        var original = fixture.Db.Store.FindPilot(1000001)!;
        long source = fixture.AddUpwell(Keepstar, Jan, new(10_000_000, 0, 0), NavigationCorporation);
        var keepstarPoint = beacon.Position + new SpacePosition(175_000, 0, 0);
        var sotiyoPoint = beacon.Position + new SpacePosition(-500_000, 0, 0);
        long destination = fixture.AddUpwell(Keepstar, NullSystem, keepstarPoint, NavigationCorporation);
        long sotiyo = fixture.AddUpwell(StructureIndustryRules.Sotiyo, NullSystem, sotiyoPoint, NavigationCorporation);
        var docking = fixture.Db.Services.GetRequiredService<IStationDocking>();
        fixture.Db.Store.SetPosition(original.ShipId, new(10_000_000, 0, 0), Jan);
        var docked = docking.Dock(original.PilotId, original.ShipId, source);
        long ship = fixture.AssembleAndBoard(docked, Avatar);
        long generator = fixture.FitCapitalModule(CapitalTravelRules.TitanPortalGenerator);
        fixture.Train(docked.PilotId, (CapitalTravelRules.JumpPortalGenerationSkill, 1));
        fixture.Grant(docked.PilotId, ship, InventoryFlag.FuelBay, Helium, 80_000);
        fixture.Db.Store.SetPosition(ship, s_station, Jan);
        Assert.Throws<InvalidDataException>(() => docking.Dock(docked.PilotId, ship, JanStation));
        fixture.Db.Store.SetPosition(ship, new(20_000_000, 0, 0), Jan);
        var escort = fixture.Db.Store.FindPilot(1000002)!;
        fixture.Db.Store.SetPosition(escort.ShipId, new(20_008_000, 0, 0), Jan);
        using Client host = fixture.Select(1000001);
        using Client passenger = fixture.Select(1000002);
        fixture.Join(passenger);

        PilotCallResult Open() => host.Call("structureCynoBeaconMgr", "CmdBridgeToStructureBeacon", beacon.Id, NullSystem);
        Assert.IsType<PilotCallResult.Failure>(Open());
        Assert.Null(fixture.Travel.Portal(host.Pilot.PilotId));
        fixture.Grant(docked.PilotId, ship, InventoryFlag.Cargo, Strontium, 500);
        var opened = Open();
        Assert.True(opened is PilotCallResult.Success, opened.ToString());
        var portal = Assert.IsType<JumpPortal>(fixture.Travel.Portal(docked.PilotId));
        Assert.Equal(generator, portal.ModuleId);
        Assert.Equal(CapitalTravelRules.TitanPortalGenerator, portal.ModuleTypeId);
        Assert.Equal(60 * TimeSpan.TicksPerSecond, portal.EndsAt - portal.StartedAt);
        Assert.Equal(50, Quantity(fixture, host, Strontium));
        await host.TickAsync();
        await passenger.TickAsync();
        Assert.Contains(passenger.Packets, packet => ContainsText(packet, "effects.JumpPortal"));
        await fixture.Live.FlushAsync();
        fixture.Reopen();
        Assert.Equal(portal, fixture.Travel.Portal(docked.PilotId));

        var traverse = passenger.Call("beyonce", "CmdJumpThroughFleet", docked.PilotId, ship, beacon.Id, NullSystem);
        Assert.True(traverse is PilotCallResult.Success, traverse.ToString());
        await passenger.TickAsync();
        fixture.Clock.Seconds += 3;
        await passenger.TickAsync();
        Assert.Equal(NullSystem, passenger.Pilot.SystemId);
        Assert.Equal(Jan, host.Pilot.SystemId);
        int bridgedFuel = Quantity(fixture, host, Helium);
        Assert.InRange(bridgedFuel, 1, 79_999);
        Assert.NotNull(fixture.Travel.Timers(passenger.Pilot.PilotId));
        Assert.Null(fixture.Travel.Timers(host.Pilot.PilotId));

        fixture.Clock.Seconds += 57;
        await host.TickAsync();
        Assert.Null(fixture.Travel.Portal(docked.PilotId));
        var direct = host.Call("structureCynoBeaconMgr", "CmdJumpToStructureBeacon", beacon.Id, NullSystem);
        Assert.True(direct is PilotCallResult.Success, direct.ToString());
        await host.TickAsync();
        fixture.Clock.Seconds += 3;
        await host.TickAsync();
        Assert.Equal(NullSystem, host.Pilot.SystemId);
        Assert.InRange(Quantity(fixture, host, Helium), 1, bridgedFuel - 1);
        var arrival = host.Pilot.Position;
        fixture.Db.Store.SetPosition(ship, sotiyoPoint + new SpacePosition(157_000, 0, 0), NullSystem);
        Assert.Throws<InvalidDataException>(() => docking.Dock(docked.PilotId, ship, sotiyo));
        fixture.Db.Store.SetPosition(ship, arrival, NullSystem);
        Assert.Equal(destination, docking.Dock(docked.PilotId, ship, destination).DockedLocationId);
    }

    [Fact]
    public async Task AeonLaunchesHeavyAttackFightersAgainstAstrahus()
    {
        using Fixture fixture = new(structures: true);
        var original = fixture.Db.Store.FindPilot(1000001)!;
        int corporation = original.CorpId;
        var point = new SpacePosition(50_000_000, 0, 0);
        long keepstar = fixture.AddUpwell(Keepstar, NullSystem, point, corporation);
        fixture.Db.Store.SetPosition(original.ShipId, point, NullSystem);
        var docked = fixture.Db.Services.GetRequiredService<IStationDocking>().Dock(original.PilotId, original.ShipId, keepstar);
        long ship = fixture.AssembleAndBoard(docked, Aeon);
        fixture.Db.Store.SetPosition(ship, point + new SpacePosition(500_000, 0, 0), NullSystem);
        long squadron = fixture.PutInFighterBay(Cyclops, 6);
        long target = fixture.AddUpwell(35832, NullSystem, point + new SpacePosition(505_000, 0, 0), corporation);
        using Client pilot = fixture.Select(1000001);
        int tube = FighterDeckRules.FirstTube;
        Assert.IsType<PilotCallResult.Success>(pilot.Call("fighterMgr", "LoadFightersToTube", squadron, tube));
        var launched = pilot.Call("fighterMgr", "LaunchFightersFromTubes", new List<object?> { tube });
        Assert.True(launched is PilotCallResult.Success, launched.ToString());
        var deck = fixture.Db.Services.GetRequiredService<IPilotFighterDeck>();
        Assert.Equal(squadron, Assert.Single(deck.Read(original.PilotId, ship).Squadrons).SquadronId);
        Assert.IsType<PilotCallResult.Success>(pilot.Call("dogmaIM", "AddTarget", target));
        fixture.Clock.Seconds += 10;
        await pilot.TickAsync();
        var attack = pilot.Call("fighterMgr", "CmdActivateAbilitySlots", new List<object?> { squadron }, 0, target);
        Assert.True(attack is PilotCallResult.Success, attack.ToString());
        fixture.Clock.Seconds += 1;
        await pilot.TickAsync();
        Assert.NotNull(fixture.Structures.Read(original.PilotId, target)!.Battle!.RepairAt);
    }

    [Fact]
    public async Task AeonAppliesRacialSkillToPaidBurstProjector()
    {
        const int weaponDisruptionProjector = 40699;
        const int heavyWater = 16272;
        const int amarrCarrier = 24311;
        using Fixture fixture = new(structures: true);
        var original = fixture.Db.Store.FindPilot(1000001)!;
        var point = new SpacePosition(80_000_000, 0, 0);
        long keepstar = fixture.AddUpwell(Keepstar, NullSystem, point, original.CorpId);
        fixture.Db.Store.SetPosition(original.ShipId, point, NullSystem);
        var docked = fixture.Db.Services.GetRequiredService<IStationDocking>().Dock(original.PilotId, original.ShipId, keepstar);
        long ship = fixture.AssembleAndBoard(docked, Aeon);
        long module = fixture.FitCapitalModule(weaponDisruptionProjector);
        fixture.Train(original.PilotId, (amarrCarrier, 4));
        var item = fixture.Inventory.List(original.PilotId, ship).Single(i => i.Id == module);
        var fitting = fixture.Db.Services.GetRequiredService<IPilotFitting>();
        Assert.Equal(228_000, fitting.ModuleAttributes(original.PilotId, item)[DogmaAttributeId.DisruptorProjectorDuration], 6);

        fixture.Grant(original.PilotId, ship, InventoryFlag.Cargo, heavyWater, 300);
        var source = point + new SpacePosition(500_000, 0, 0);
        var target = source + new SpacePosition(50_000, 0, 0);
        fixture.Db.Store.SetPosition(ship, source, NullSystem);
        var escort = fixture.Db.Store.FindPilot(1000002)!;
        fixture.Db.Store.SetPosition(escort.ShipId, target, NullSystem);
        using Client pilot = fixture.Select(1000001);
        using Client escortClient = fixture.Select(1000002);
        var activated = pilot.Call(
            "superWeaponMgr",
            "ActivateSinglePointTargetedModule",
            module,
            (long)DogmaEffect.ProjectorDisruptor,
            new object?[] { target.X, target.Y, target.Z }
        );
        Assert.True(activated is PilotCallResult.Success, activated.ToString());
        Assert.Equal(50, Quantity(fixture, pilot, heavyWater));
        fixture.Clock.Seconds += 10;
        await pilot.TickAsync();
        var disruption = Assert.Single(fixture.Db.Services.GetRequiredService<IPilotElectronicWarfare>().Incoming(escort.ShipId));
        Assert.Equal(-20, disruption.Applied!.TrackingPercent);
    }

    [Theory]
    [InlineData(671)]
    [InlineData(3764)]
    [InlineData(Avatar)]
    [InlineData(23773)]
    [InlineData(22852)]
    [InlineData(23913)]
    [InlineData(23917)]
    [InlineData(Aeon)]
    public void PinnedEmpireSupercapitalHullModifiersAreClassified(int hull)
    {
        using Fixture fixture = new();
        using CatalogDbContext catalog = new(new WorldDatabase(fixture.Db.Path).CatalogPath);
        Assert.True(HullCatalog.UsesCatalogBonuses(catalog, hull));
    }

    [Fact]
    public async Task SotiyoPausesAndCompletesPinnedAeonRecipeWithSovereigntyUpgrade()
    {
        const int blueprintType = 23920;
        const int construction = 22242;
        const int fuelBlock = 4051;
        const int maximumBlocks = 6000;
        using Fixture fixture = new(emitterHull: Rapier, generator: CapitalTravelRules.StandardGenerator, structures: true);
        fixture.PrepareNavigation();
        var owner = fixture.Db.Store.FindPilot(1000002)!;
        var point = new SpacePosition(40_000_000, 0, 0);
        long yard = fixture.AddUpwell(StructureIndustryRules.Sotiyo, NullSystem, point, NavigationCorporation);
        fixture.Db.Store.SetPosition(owner.ShipId, point, NullSystem);
        long module = fixture.Grant(owner.PilotId, owner.ShipId, InventoryFlag.Cargo, StructureIndustryRules.SupercapitalShipyard, 1);
        fixture.Structures.FitService(owner.PilotId, yard, module, StructureRules.FirstServiceSlot);
        fixture.Structures.DepositFuel(
            owner.PilotId,
            yard,
            fixture.Grant(owner.PilotId, owner.ShipId, InventoryFlag.Cargo, fuelBlock, maximumBlocks)
        );
        Assert.Throws<InvalidDataException>(() => fixture.Structures.SetServiceOnline(owner.PilotId, yard, module, true));
        Assert.Equal(maximumBlocks, fixture.Structures.Inventory(owner.PilotId, yard).Single(item => item.TypeId == fuelBlock).Quantity);

        using (var action = fixture.Live.Economy.Begin(new(systems: [NullSystem])))
        {
            var claim = action.Set<SavedSovereigntyClaim>().Find(NullSystem)!;
            action.Add(
                new SavedSovereigntyUpgrade
                {
                    Id = action.NextItemId(),
                    HubId = claim.HubId,
                    SystemId = NullSystem,
                    TypeId = StructureIndustryRules.SupercapitalConstructionUpgrade,
                    ConfiguredOnline = true,
                    PowerState = SovereigntyUpgradePower.Online,
                }
            );
            action.Commit();
        }
        Assert.Contains(
            StructureService.SupercapitalManufacturing,
            fixture.Structures.SetServiceOnline(owner.PilotId, yard, module, true).Structure.Services
        );
        fixture.Db.Services.GetRequiredService<IStationDocking>().Dock(owner.PilotId, owner.ShipId, yard);
        var industry = fixture.Db.Services.GetRequiredService<IPilotIndustry>();
        Assert.Contains(Aeon, industry.ProductTypes(yard));
        using CatalogDbContext catalog = new(new WorldDatabase(fixture.Db.Path).CatalogPath);
        Assert.False(ManufacturingCatalog.Allowed(catalog, Aeon, ManufacturingCatalog.Security(catalog, NullSystem)));
        var recipe = ManufacturingCatalog.Recipes(catalog)[blueprintType];
        long blueprint = fixture.Grant(owner.PilotId, yard, InventoryFlag.Hangar, blueprintType, 1);
        foreach (var material in recipe.Materials)
        {
            fixture.Grant(owner.PilotId, yard, InventoryFlag.Hangar, (int)material.TypeId!.Value, (int)material.Quantity!.Value);
        }
        fixture.Train(owner.PilotId, (construction, 4));
        var facility = industry.Facility(owner.PilotId, yard);
        var plan = ManufacturingRules.Plan(
            1,
            new(
                recipe.Seconds,
                0,
                [],
                [new(catalog.FittingAttributes(construction).GetValueOrDefault(DogmaAttributeId.ManufactureTimePerLevel), 4)],
                recipe.Materials,
                new(0, 0, -1),
                facility.TimeMultiplier,
                facility.MaterialMultiplier
            )
        );
        var installed = industry.Install(owner.PilotId, new(blueprint, blueprintType, yard, 1, Aeon, plan.DurationSeconds, plan.Materials));
        fixture.SetUpgrade(StructureIndustryRules.SupercapitalConstructionUpgrade, SovereigntyUpgradePower.Offline, NullSystem);
        var paused = industry.Job(owner.PilotId, installed.Job.Id);
        Assert.Equal(IndustryJobStatus.Paused, paused.Status);
        Assert.False(Assert.Single(fixture.Structures.Read(owner.PilotId, yard)!.Modules).Online);
        await fixture.Live.FlushAsync();
        fixture.Reopen(restart: true);
        fixture.Clock.AdvanceWallTime(TimeSpan.FromDays(1));
        fixture.SetUpgrade(StructureIndustryRules.SupercapitalConstructionUpgrade, SovereigntyUpgradePower.Online, NullSystem);
        fixture.Structures.SetServiceOnline(owner.PilotId, yard, module, true);
        var resumed = industry.Job(owner.PilotId, paused.Id);
        Assert.Equal(installed.Job.EndTime + TimeSpan.TicksPerDay, resumed.EndTime);
        while (fixture.Clock.Now < resumed.EndTime)
        {
            int stock = fixture.Structures.Inventory(owner.PilotId, yard).Single(item => item.TypeId == fuelBlock).Quantity;
            if (stock < maximumBlocks)
            {
                long fuel = fixture.Grant(owner.PilotId, yard, InventoryFlag.Hangar, fuelBlock, maximumBlocks - stock);
                fixture.Structures.DepositFuel(owner.PilotId, yard, fuel);
            }
            fixture.Clock.AdvanceWallTime(TimeSpan.FromTicks(Math.Min(2 * TimeSpan.TicksPerDay, resumed.EndTime - fixture.Clock.Now)));
        }
        var delivered = industry.Deliver(owner.PilotId, resumed.Id, NullSystem);
        var package = Assert.Single(delivered.Changes, change => change.Item.TypeId == Aeon).Item;
        Assert.Equal(yard, package.LocationId);
        Assert.Equal(1, package.Quantity);
        Assert.Empty(industry.Deliver(owner.PilotId, resumed.Id, NullSystem).Changes);
        long ship = fixture.AssembleAndBoard(fixture.Db.Store.FindPilot(1000002)!, Aeon, package.Id);
        using Client builder = fixture.Select(1000002);
        var undocked = builder.Context.Flight.Execute(new FlightCommand.Undock(ship));
        Assert.Same(PilotCallResult.None, undocked);
        await builder.TickAsync();
        Assert.NotNull(builder.Pilot.Position);
        Assert.Equal(NullSystem, builder.Pilot.SystemId);
    }
}
