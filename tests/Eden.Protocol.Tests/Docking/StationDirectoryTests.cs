using Eden.Gameplay.Docking;
using Eden.Gameplay.Flight;
using Eden.Protocol.Docking;
using Eden.Protocol.PilotSessions;
using Eden.Protocol.Serialization;
using Eden.Server;
using Eden.World.Catalog;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Eden.Protocol.Tests.Docking;

public sealed class StationDirectoryTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void NativeDirectoryIncludesRemoteStationsAndCatalogServiceMasksFromAnyPilotLocation(bool inSpace)
    {
        using TestWorld db = new();
        TestCatalog.AddSchool(new WorldDatabase(db.Path).CatalogPath, 18, 1, 1, 30001392, 60015159, 601);
        using (CatalogDbContext catalog = new(new WorldDatabase(db.Path).CatalogPath, writable: true))
        {
            catalog.Database.ExecuteSqlRaw(
                """
                INSERT OR REPLACE INTO SourceRecord(Dataset,Key,Data) VALUES
                    ('client/stationservices','13','{{}}'), ('client/stationservices','5','{{}}'), ('client/stationservices','7','{{}}'),
                    ('client/stationoperations','14','{{"services":[7,13]}}');
                """
            );
        }
        using var scope = db.Services.CreateScope();
        var pilot = db.Store.FindPilot(1000001)!;
        if (inSpace)
        {
            pilot = pilot with { Position = new SpacePosition(1_000_000, 0, 0) };
        }
        using PilotCallContext context = new(
            1000001,
            pilot.Account,
            [pilot],
            p => new(db.Store, p, TimeProvider.System, new(), db.Objects),
            42,
            SignedSettings.Empty
        );
        var router = scope.ServiceProvider.GetRequiredService<PilotCallRouter>();
        Assert.IsType<PilotCallResult.UnsupportedCall>(router.Call(context, "map", "GetStationInfo", Array.Empty<object?>(), _ => { }));
        Assert.Same(PilotCallResult.Unsupported, router.Call(context, "stationSvc", "GetStation", new object?[] { 60015159L }, _ => { }));
        context.Select(pilot);
        var reply = Assert.IsType<PilotCallResult.Success>(router.Call(context, "map", "GetStationInfo", Array.Empty<object?>(), _ => { }));
        var rowset = Assert.IsType<BlueInstance>(BlueMarshal.Decode(BlueMarshal.Encode(reply.Reply)));
        Assert.Equal("eve.common.script.sys.rowset.Rowset", rowset.Name);
        var fields = BlueRead.Fields(rowset.State);
        var header = Assert.IsType<List<object?>>(fields["header"]).Select(BlueMarshal.Text).ToArray();
        Assert.Equal(
            new[]
            {
                "stationID",
                "stationTypeID",
                "stationName",
                "solarSystemID",
                "constellationID",
                "regionID",
                "corporationID",
                "ownerID",
                "operationID",
                "orbitID",
                "x",
                "y",
                "z",
                "serviceMask",
            },
            header
        );
        var rows = Assert.IsType<List<object?>>(fields["lines"]).Select(value => BlueRead.Tuple(value, 14)).ToArray();
        var remote = Assert.Single(rows, row => Equals(row[0], 60015159L));
        Assert.Equal(
            new object?[]
            {
                60015159L,
                52678L,
                "School system I - Caldari Navy",
                30001392L,
                20000020L,
                10000002L,
                1000035L,
                1000035L,
                14L,
                40009087L,
                1d,
                2d,
                3d,
                6L,
            },
            remote.Select((value, index) => index == 2 ? BlueMarshal.Text(value) : value)
        );
        Assert.Contains(rows, row => Equals(row[0], 60003760L));
        var directory = db.Services.GetRequiredService<IStationDirectory>();
        Assert.Same(directory.Read(), directory.Read());
        using CatalogDbContext check = new(new WorldDatabase(db.Path).CatalogPath);
        Assert.Equal(check.Set<Station>().Count(), rows.Length);
        Assert.Equal(rows.Length, rows.Select(row => row[0]).Distinct().Count());
        foreach (var row in rows)
        {
            var details = Assert.IsType<PilotCallResult.Success>(
                router.Call(context, "stationSvc", "GetStation", new object?[] { row[0] }, _ => { }, new() { ["machoVersion"] = 1L })
            );
            var native = Assert.IsType<BlueInstance>(BlueMarshal.Decode(BlueMarshal.Encode(details.Reply)));
            Assert.Equal("utillib.KeyVal", native.Name);
            var values = BlueRead.Fields(native.State);
            Assert.Equal(header.Length, values.Count);
            for (int index = 0; index < header.Length; index++)
            {
                Assert.Equal(index == 2 ? BlueMarshal.Text(row[index]) : row[index], values[header[index]]);
            }
        }
        Assert.Equal(PilotCallResult.None, router.Call(context, "stationSvc", "GetStation", new object?[] { 60000000L }, _ => { }));
        foreach (object?[] args in new object?[][] { [], [0L], [-1L], [long.MaxValue], ["60015159"], [60015159L, 60003760L] })
        {
            Assert.IsType<PilotCallResult.Failure>(router.Call(context, "stationSvc", "GetStation", args, _ => { }));
        }
        Assert.IsType<PilotCallResult.Failure>(
            router.Call(context, "stationSvc", "GetStation", new object?[] { 60015159L }, _ => { }, new() { ["ownerID"] = 90000002L })
        );
        Assert.IsType<PilotCallResult.Failure>(router.Call(context, "map", "GetStationInfo", new object?[] { 60015159L }, _ => { }));
        Assert.IsType<PilotCallResult.Failure>(
            router.Call(context, "map", "GetStationInfo", Array.Empty<object?>(), _ => { }, new() { ["stationId"] = 60015159L })
        );
        using var reopened = ServerServices.Build(new(db.Path));
        Assert.Equal(
            directory.Read().Stations.Select(station => station.Name),
            reopened.GetRequiredService<IStationDirectory>().Read().Stations.Select(station => station.Name)
        );
        Assert.Empty(context.Notifications);
    }

    [Fact]
    public void ServiceBitsFollowSortedCatalogKeysIncludingGapsDuplicatesAndTopSignedBit()
    {
        StationDirectoryEntry station = new(1, 2, "Station", 3, 4, 5, 6, 7, 8, new(1, 2, 3), [13, 5, 13]);
        Assert.Equal(5L, Mask(new([station], [13, 5, 7])));
        Assert.Equal(0L, Mask(new([station with { Services = [] }], [13, 5, 7])));
        int[] serviceIds = Enumerable.Range(1, 63).ToArray();
        Assert.Equal(1L << 62, Mask(new([station with { Services = [63] }], serviceIds)));
        Assert.Throws<InvalidDataException>(() => Mask(new([station], Enumerable.Range(1, 64).ToArray())));
    }

    private static object? Mask(StationDirectorySnapshot directory)
    {
        var reply = Assert.IsType<BlueInstance>(BlueMarshal.Decode(BlueMarshal.Encode(new StationDirectoryReply(directory))));
        return BlueRead.Tuple(Assert.Single(Assert.IsType<List<object?>>(BlueRead.Fields(reply.State)["lines"])), 14)[13];
    }
}
