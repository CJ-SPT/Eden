using Eden.Data.Catalog;
using Eden.Gameplay.Flight;
using Eden.Gameplay.Structures;
using Eden.Protocol.Flight;
using Eden.Protocol.PilotSessions;
using Eden.Protocol.Serialization;
using Eden.Protocol.Tests.Fixtures;
using Eden.World.Catalog;
using Eden.World.Storage;
using Microsoft.Extensions.DependencyInjection;

namespace Eden.Protocol.Tests.Structures;

public sealed class MoonMiningWarpTests
{
    private const int MoonId = 40009087;
    private const int BeaconlessMoonId = 40009088;
    private const int ForeignMoonId = 40009231;
    private const int SystemId = 30000142;
    private const int ForeignSystemId = 30000144;

    [Theory]
    [InlineData(false, 0)]
    [InlineData(true, 0)]
    [InlineData(false, 10000)]
    [InlineData(true, 10000)]
    public void NativeFirstBoundWarpUsesTheMoonsMiningBeacon(bool bytesSubject, int range)
    {
        using Fixture fixture = new();
        object subject = bytesSubject ? "moonMiningPoint"u8.ToArray() : "moonMiningPoint";
        object?[] firstCall =
        [
            "CmdWarpToStuff",
            new object?[] { subject, (long)MoonId },
            new Dictionary<string, object?> { ["minRange"] = (double)range, ["fleet"] = false },
        ];
        var result = Assert.IsType<PilotCallResult.Success>(fixture.Call("MachoBindObject", [(long)SystemId, firstCall]));
        var bound = Assert.IsType<BoundPilotReply>(result.Reply);
        Assert.Null(BlueMarshal.Decode(BlueMarshal.Encode(bound.Reply)));
        Assert.True(fixture.Context.Flight.PreparingWarp);
        fixture.Context.Flight.RefreshPeers();
        var pilot = fixture.Context.Pilot;
        var contact = Assert.Single(fixture.Space.Peers(pilot with { ShipId = pilot.ShipId + 1 }));
        var expected = WarpCourse.LandingPoint(pilot.ShipId, pilot.Flight.ShipRadius, pilot.Position!, fixture.Beacon.Position, range);
        Assert.Equal(expected, contact.AlignTo);

        fixture.Clock.Seconds += 300;
        fixture.Context.Flight.Checkpoint();
        Assert.False(fixture.Context.Flight.InWarp);
        Assert.False(fixture.Context.Flight.PreparingWarp);
        Assert.InRange(fixture.Context.Pilot.Position!.DistanceTo(fixture.Beacon.Position), 0, MoonMiningRules.PlacementRange);
    }

    [Fact]
    public void InvalidMoonTargetsAndRangesLeaveTheResidentShipUnchanged()
    {
        using Fixture fixture = new();
        Assert.Single(
            fixture.Db.Services.GetRequiredService<ISystemObjects>().Load(ForeignSystemId),
            body => body.TypeId == MoonMiningRules.BeaconType
        );
        var pilot = fixture.Context.Pilot;
        var origin = pilot.Position;
        var velocity = fixture.Context.Flight.Velocity;
        foreach (long target in new long[] { 0, -1, long.MaxValue, BeaconlessMoonId, ForeignMoonId, pilot.StationId, fixture.Beacon.Id })
        {
            Assert.IsType<PilotCallResult.Failure>(fixture.Call("CmdWarpToStuff", ["moonMiningPoint", target]));
        }
        foreach (double range in new[] { -1, NavigationState.MaximumWarpRange + 1, double.NaN, double.PositiveInfinity })
        {
            Assert.IsType<PilotCallResult.Failure>(
                fixture.Call("CmdWarpToStuff", ["moonMiningPoint", (long)MoonId], new() { ["minRange"] = range })
            );
        }
        Assert.Equal(origin, fixture.Context.Pilot.Position);
        Assert.Equal(velocity, fixture.Context.Flight.Velocity);
        Assert.False(fixture.Context.Flight.PreparingWarp);
        Assert.Empty(fixture.Context.Flight.Updates);
        Assert.Equal(origin, fixture.Db.Store.FindPilot(fixture.Context.AccountId, pilot.PilotId)!.Position);
    }

    private sealed class Fixture : IDisposable
    {
        public TestClock Clock { get; } = new();
        public SharedSpace Space { get; } = new();
        public TestWorld Db { get; }
        public PilotCallContext Context { get; }
        public StaticSpaceObject Beacon { get; }
        private readonly IServiceScope _scope;

        public Fixture()
        {
            Db = new(clock: Clock, liveWorld: true, simulationObjects: false);
            using (CatalogDbContext catalog = new(new WorldDatabase(Db.Path).CatalogPath, writable: true))
            {
                SetContent(
                    SystemId,
                    """
                    {"security":0.4,"planets":{"40009086":{"moons":{
                      "40009087":{"miningBeacon":{"position":{"x":100000000000,"y":200000000000,"z":300000000000}}},
                      "40009088":{}}}}}
                    """
                );
                SetContent(
                    ForeignSystemId,
                    """
                    {"security":0.4,"planets":{"40009230":{"moons":{
                      "40009231":{"miningBeacon":{"position":{"x":100000000000,"y":200000000000,"z":300000000000}}}}}}}
                    """
                );
                catalog.SaveChanges();

                void SetContent(int systemId, string data)
                {
                    string key = systemId.ToString(System.Globalization.CultureInfo.InvariantCulture);
                    var record = catalog.Set<SourceRecord>().Find("client/solarsystemcontent", key);
                    if (record is null)
                    {
                        catalog.Add(
                            new SourceRecord
                            {
                                Dataset = "client/solarsystemcontent",
                                Key = key,
                                Data = data,
                            }
                        );
                    }
                    else
                    {
                        record.Data = data;
                        catalog.Update(record);
                    }
                }
            }
            var pilot = Db.Store.FindPilot(1000001)!;
            Db.Store.SetPosition(pilot.ShipId, pilot.Home.Position + new SpacePosition(1_000_000_000, 0, 0));
            pilot = Db.Store.FindPilot(1000001)!;
            var objects = Db.Services.GetRequiredService<ISystemObjects>().Load(SystemId);
            Beacon = Assert.Single(objects, body => body.TypeId == MoonMiningRules.BeaconType);
            _scope = Db.Services.CreateScope();
            Context = new(1000001, pilot.Account, [pilot], p => new(Db.Store, p, Clock, Space, objects), 42, SignedSettings.Empty);
            Context.Select(pilot);
            Context.Flight.Snapshot();
            Context.Flight.Updates.Clear();
        }

        public PilotCallResult Call(string method, object?[] args, Dictionary<string, object?>? keywords = null) =>
            _scope
                .ServiceProvider.GetRequiredService<PilotCallRouter>()
                .Call(Context, "beyonce", method, BlueMarshal.Decode(BlueMarshal.Encode(args)), _ => { }, keywords);

        public void Dispose()
        {
            Context.Dispose();
            _scope.Dispose();
            Db.Dispose();
        }
    }
}
