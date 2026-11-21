using Eden.Data.World.Characters;
using Eden.Gameplay.Docking;
using Eden.Gameplay.Dogma;
using Eden.Gameplay.Fleets;
using Eden.Gameplay.Flight;
using Eden.Gameplay.Inventory;
using Eden.Gameplay.Ships;
using Eden.Protocol.PilotSessions;
using Microsoft.Extensions.DependencyInjection;

namespace Eden.Protocol.Tests.Flight;

public sealed partial class CapitalTravelTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task AmarrPhenomenaPaysIsotopesAndBuffsAllShipsWithinRangeUntilTheSavedDeadline(bool withinRange)
    {
        const int fuel = 40_000;
        using Fixture fixture = new(structures: true);
        var source = fixture.Db.Store.FindPilot(1000001)!;
        using (var account = fixture.Live.Economy.Begin(new(pilots: [source.PilotId])))
        {
            account.Set<Account>().Find(1000001)!.CloneGrade = AccountCloneGrade.Omega;
            account.Commit();
        }
        var recipient = fixture.Db.Store.FindPilot(1000002)!;
        var origin = new SpacePosition(1_500_000_000, 0, 0);
        var station = origin - new SpacePosition(500_000, 0, 0);
        long keepstar = fixture.AddUpwell(Keepstar, NullSystem, station, source.CorpId);
        var docking = fixture.Db.Services.GetRequiredService<IStationDocking>();
        fixture.Db.Store.SetPosition(source.ShipId, station, NullSystem);
        long ship = fixture.AssembleAndBoard(docking.Dock(source.PilotId, source.ShipId, keepstar), Avatar);
        long module = fixture.FitCapitalModule(CommandBurstRules.AmarrPhenomena);
        fixture.Train(source.PilotId, (CommandBurstRules.SpatialPhenomenaGeneration, 3));
        fixture.Grant(source.PilotId, ship, InventoryFlag.FuelBay, Helium, fuel - 1);
        fixture.Db.Store.SetPosition(ship, origin, NullSystem);
        fixture.Db.Store.SetPosition(recipient.ShipId, origin + new SpacePosition(withinRange ? 1_000_000 : 21_000_000, 0, 0), NullSystem);
        using Client titan = fixture.Select(1000001);
        using Client target = fixture.Select(1000002);
        var bursts = fixture.Db.Services.GetRequiredService<IPilotCommandBursts>();
        var resources = fixture.Db.Services.GetRequiredService<IPilotShipResources>();
        var baseline = resources.Read(recipient.PilotId, recipient.ShipId, fixture.Clock.Now).Attributes;
        Assert.IsType<PilotCallResult.Failure>(Activate());
        Assert.Equal(fuel - 1, Quantity(fixture, titan, Helium));
        Assert.Empty(bursts.Incoming(ship, fixture.Clock.Now));
        fixture.Grant(source.PilotId, ship, InventoryFlag.Cargo, Helium, 2);
        long start = fixture.Clock.Now;
        var activated = Activate();
        Assert.True(activated is PilotCallResult.Success, activated.ToString());
        Assert.Equal(1, Quantity(fixture, titan, Helium));
        var applied = bursts.Incoming(ship, start);
        Assert.Equal(4, applied.Count);
        Assert.All(applied, buff => Assert.Equal(start + 390 * TimeSpan.TicksPerSecond, buff.ExpiresAt));
        Assert.Equal(withinRange ? applied.ToArray() : [], bursts.Incoming(recipient.ShipId, start).ToArray());
        double speed = resources.Read(recipient.PilotId, recipient.ShipId, start).Attributes[DogmaAttributeId.MaxVelocity];
        Assert.Equal(baseline[DogmaAttributeId.MaxVelocity] * (withinRange ? 0.9 : 1), speed, precision: 3);
        Assert.Contains(
            "Phenomena Generator",
            Assert.Throws<InvalidDataException>(() => docking.Dock(source.PilotId, ship, keepstar)).Message
        );
        Assert.IsType<PilotCallResult.Success>(titan.Context.Flight.Execute(new FlightCommand.Heading(1, 0, 0)));
        await target.TickAsync();
        Assert.Equal(withinRange, target.Packets.Any(packet => ContainsText(packet, "OnDbuffUpdated")));

        titan.Dispose();
        target.Dispose();
        await fixture.Live.FlushAsync();
        fixture.Reopen(restart: true);
        Assert.Equal(applied.ToArray(), bursts.Incoming(ship, start + 390 * TimeSpan.TicksPerSecond - 1).ToArray());
        Assert.Empty(bursts.Incoming(ship, start + 390 * TimeSpan.TicksPerSecond));
        Assert.Contains(
            "Phenomena Generator",
            Assert.Throws<InvalidDataException>(() => docking.Dock(source.PilotId, ship, keepstar)).Message
        );

        PilotCallResult Activate() => titan.Call("dogmaIM", "Activate", module, "moduleTitanEffectGenerator", null, 0L);
    }
}
