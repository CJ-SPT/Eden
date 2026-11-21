using Eden.Gameplay.Docking;
using Eden.Gameplay.Flight;
using Eden.Gameplay.Inventory;
using Eden.Protocol.PilotSessions;
using Eden.World.Catalog;
using Eden.World.Fittings;
using Eden.World.Storage;
using Microsoft.Extensions.DependencyInjection;

namespace Eden.Protocol.Tests.Flight;

public sealed partial class CapitalTravelTests
{
    [Fact]
    public async Task SarathielCanSteerAtOneTenthSpeedDuringSiegeAndCannotWarp()
    {
        using Fixture fixture = new(CapitalModeRules.Sarathiel, 17889);
        long module = fixture.FitCapitalModule(CapitalModeRules.SiegeOne);
        fixture.PutInCapitalCargo(Strontium, 500);
        var ship = fixture.Db.Store.FindPilot(1000001)!;
        fixture.Db.Store.SetPosition(ship.ShipId, new(1_000_000_000, 0, 0), NullSystem);
        using Client pilot = fixture.Select(1000001);
        var activation = pilot.Call("dogmaIM", "Activate", module, "moduleBonusSiegeModule", null, 0L);
        Assert.True(activation is PilotCallResult.Success, activation.ToString());
        var travel = pilot.Context.Flight;
        Assert.Equal(ship.Flight.MaxSpeed / 10, pilot.Pilot.Flight.MaxSpeed, precision: 4);
        Assert.IsType<PilotCallResult.Success>(travel.Execute(new FlightCommand.Heading(1, 0, 0)));
        Assert.IsType<PilotCallResult.Success>(travel.Execute(new FlightCommand.Speed(1)));
        var origin = pilot.Pilot.Position;
        fixture.Clock.Seconds += 10;
        await pilot.TickAsync();
        Assert.NotEqual(origin, pilot.Pilot.Position);
        Assert.IsType<PilotCallResult.Failure>(travel.Execute(new FlightCommand.WarpToPoint(1, new(2_000_000_000, 0, 0), 0)));
    }

    [Fact]
    public async Task PirateAndSpecialCapitalsUsePinnedHullBonusesAndCaimanAndKomodoLaunchFighters()
    {
        using Fixture fixture = new(FighterDeckRules.Caiman, 17888, structures: true);
        using (CatalogDbContext catalog = new(new WorldDatabase(fixture.Db.Path).CatalogPath))
        {
            foreach (int type in new[] { 3514, 42124, 42125, 42126, 42241, 42242, 42243, 45645, 45647, 45649, 52907, 78576, 87381 })
            {
                Assert.True(HullCatalog.UsesCatalogBonuses(catalog, type), $"Hull {type} lacks pinned bonus support.");
                Assert.True(HullCatalog.CanAssemble(catalog, catalog.RequireItem(type), (int)InventoryCategory.Ship));
            }
        }

        await LaunchFromTwoTubes(1000001, Templar, Templar);
        var tender = fixture.Db.Store.FindPilot(1000002)!;
        var point = new SpacePosition(1_000_000_000, 0, 0);
        long keepstar = fixture.AddUpwell(Keepstar, NullSystem, point, tender.CorpId);
        fixture.Db.Store.SetPosition(tender.ShipId, point, NullSystem);
        var docking = fixture.Db.Services.GetRequiredService<IStationDocking>();
        long titan = fixture.AssembleAndBoard(docking.Dock(tender.PilotId, tender.ShipId, keepstar), FighterDeckRules.Komodo);
        fixture.Db.Store.SetPosition(titan, point + new SpacePosition(50_000, 0, 0), NullSystem);
        await LaunchFromTwoTubes(1000002, Templar, Cyclops);

        async Task LaunchFromTwoTubes(long account, int firstType, int secondType)
        {
            using Client pilot = fixture.Select(account);
            long owner = pilot.Pilot.PilotId;
            long ship = pilot.Pilot.ShipId;
            int tube = FighterDeckRules.FirstTube;
            long first = AddSquadron(firstType);
            long second = AddSquadron(secondType);
            Assert.False(Load(first, tube + 2));
            Assert.True(Load(first, tube));
            Assert.True(Load(second, tube + 1));
            var launch = pilot.Call("fighterMgr", "LaunchFightersFromTubes", new List<object?> { tube, tube + 1 });
            Assert.True(launch is PilotCallResult.Success, launch.ToString());
            await pilot.TickAsync();
            var deck = fixture.Db.Services.GetRequiredService<IPilotFighterDeck>().Read(owner, ship);
            Assert.Empty(deck.Tubes);
            Assert.Equal(new[] { first, second }, deck.Squadrons.Select(s => s.SquadronId).ToArray());
            Assert.Contains(pilot.Packets, packet => ContainsText(packet, "OnFighterAddedToController"));

            bool Load(long item, int flag) =>
                Assert.IsType<bool>(
                    Assert.IsType<PilotCallResult.Success>(pilot.Call("fighterMgr", "LoadFightersToTube", item, flag)).Reply.ToBlue()
                );

            long AddSquadron(int type)
            {
                var attributes = fixture.Db.Services.GetRequiredService<IPilotFitting>().Attributes(type);
                fixture.Train(
                    owner,
                    InventoryDefinitions
                        .RequiredSkills.Select(req =>
                            ((int)attributes.GetValueOrDefault(req.Skill), (int)attributes.GetValueOrDefault(req.Level))
                        )
                        .Where(req => req.Item1 > 0)
                        .ToArray()
                );
                return fixture.Grant(owner, ship, InventoryFlag.FighterBay, type, 1);
            }
        }
    }
}
