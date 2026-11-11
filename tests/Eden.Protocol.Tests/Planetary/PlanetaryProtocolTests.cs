using System.Buffers.Binary;
using Eden.Gameplay.Inventory;
using Eden.Gameplay.Planetary;
using Eden.Protocol.Docking;
using Eden.Protocol.Flight;
using Eden.Protocol.PilotSessions;
using Eden.Protocol.Planetary;
using Eden.Protocol.Serialization;
using Microsoft.Extensions.DependencyInjection;
using Fixture = Eden.Protocol.Tests.Persistence.PassiveFittingTests.Fixture;

namespace Eden.Protocol.Tests.Planetary;

public sealed class PlanetaryProtocolTests
{
    [Fact]
    public void CustomsContactSuppliesTheRequiredNativeModelLevelAndOrbitalCategory()
    {
        using var f = PlanetaryIndustryTests.Prepare();
        PlanetaryIndustryTests.Deploy(f);
        int system = f.Db.Store.FindPilot(1000001)!.SystemId;
        var contact = Assert.Single(PlanetaryIndustryTests.Industry(f).OrbitContacts(system, f.Clock.Now));
        var slim = Assert.IsType<BlueInstance>(Ballpark.WreckSlim(contact).ToBlue());
        var fields = Assert.IsType<Dictionary<string, object?>>(slim.State);
        Assert.True(fields.ContainsKey("level"));
        Assert.Null(fields["level"]);
        Assert.Equal((int)InventoryCategory.Orbitals, fields["categoryID"]);
        Assert.Equal(PlanetaryIndustryTests.PlanetId, fields["planetID"]);
        Assert.Equal(PlanetaryRules.OrbitalIdleState, fields["orbitalState"]);
        Assert.NotEmpty(BlueMarshal.Encode(Ballpark.WreckSlim(contact)));
    }

    private static PilotCallContext Context(Fixture f)
    {
        var pilot = f.Db.Store.FindPilot(1000001)!;
        PilotCallContext context = new(
            1000001,
            pilot.Account,
            [pilot],
            p => new(f.Db.Store, p, f.Clock, new SharedSpace(), []),
            42,
            SignedSettings.Empty
        );
        context.Select(pilot);
        return context;
    }

    [Fact]
    public void RouterBindsEachPlanetSeparatelyAndReturnsTheNativeFirstCall()
    {
        using var f = PlanetaryIndustryTests.Prepare();
        using var context = Context(f);
        using var scope = f.Db.Services.CreateScope();
        var router = scope.ServiceProvider.GetRequiredService<PilotCallRouter>();
        var result = Assert.IsType<PilotCallResult.Success>(
            router.Call(
                context,
                "planetMgr",
                "MachoBindObject",
                new object?[] { (long)PlanetaryIndustryTests.PlanetId, new object?[] { "GetPlanetInfo", Array.Empty<object?>(), null } },
                _ => { }
            )
        );
        var bound = Assert.IsType<BoundPilotReply>(result.Reply);
        Assert.Equal($"@planet:{PlanetaryIndustryTests.PlanetId}", context.ResolveReference(bound.Reference.Id));
        var snapshot = Assert.IsType<PlanetaryPlanetReply>(bound.Reply).Snapshot;
        Assert.Null(snapshot.Colony);
        var firstReference = bound.Reference;
        context.ReleaseLocationReferences();
        Assert.Equal($"@planet:{PlanetaryIndustryTests.PlanetId}", context.ResolveReference(firstReference.Id));
        Assert.IsType<PilotCallResult.Failure>(router.Call(context, "planetMgr", "MachoBindObject", new object?[] { -1L, null }, _ => { }));
        context.LogOff();
        Assert.Null(context.ResolveReference(firstReference.Id));
    }

    [Fact]
    public void NativeCommandStreamConsumesCargoAndPreservesRowsTuplesListsAndIntegerCommodityKeys()
    {
        using var f = PlanetaryIndustryTests.Prepare();
        using var context = Context(f);
        long item = f.Grant(PlanetaryIndustryTests.CommandCenter, cargo: true);
        var handler = new PlanetaryIndustryHandler(PlanetaryIndustryTests.Industry(f));
        var stream = new List<object?>
        {
            new object?[] { 1L, new object?[] { item, (long)PlanetaryIndustryTests.CommandCenter, 1d, 1d } },
            new object?[] { 9L, new object?[] { item, 4L } },
            new object?[] { 1L, new object?[] { new object?[] { 1L, 0L }, (long)PlanetaryIndustryTests.Storage, 1d, 1.001d } },
            new object?[] { 3L, new object?[] { item, new object?[] { 1L, 0L }, 0L } },
        };
        var result = Assert.IsType<PilotCallResult.Success>(
            handler.Call(context, $"@planet:{PlanetaryIndustryTests.PlanetId}", "UserUpdateNetwork", [stream])
        );
        var colony = Assert.IsType<PlanetaryColonyReply>(result.Reply).Colony;
        Assert.Equal(2, colony.Pins.Count);
        Assert.NotEqual(item, colony.Pins[0].Id);
        Assert.Contains(context.Notifications, n => n is ItemChanged);
        var wire = Assert.IsType<BlueInstance>(BlueMarshal.Decode(BlueMarshal.Encode(result.Reply)));
        var fields = Assert.IsType<Dictionary<string, object?>>(wire.State);
        Assert.Equal(4L, fields["level"]);
        var pins = Assert.IsType<List<object?>>(fields["pins"]);
        var pin = Assert.IsType<Dictionary<string, object?>>(Assert.IsType<BlueInstance>(pins[0]).State);
        Assert.IsType<double>(pin["latitude"]);
        // Empty dictionaries have no key type until the first commodity is present.
        PlanetaryPin content = colony.Pins[0] with
        {
            Contents = [new(PlanetaryIndustryTests.Water, 20)],
        };
        var contentWire = Assert.IsType<BlueInstance>(BlueMarshal.Decode(BlueMarshal.Encode(PlanetaryWire.Pin(content, f.PilotId))));
        var contents = Assert.IsType<Dictionary<string, object?>>(contentWire.State)["contents"];
        Assert.Equal(20L, Assert.IsType<Dictionary<long, object?>>(contents)[PlanetaryIndustryTests.Water]);
        var planets = new PlanetaryPlanetsReply(PlanetaryIndustryTests.Industry(f).List(f.PilotId));
        var rows = Assert.IsType<BlueConstructor>(planets.ToBlue());
        Assert.Equal("carbon.common.script.sys.crowset.CRowset", rows.Name);
        Assert.NotEmpty(BlueMarshal.Encode(planets));
        Assert.NotEmpty(
            BlueMarshal.Encode(
                new PlanetaryPlanetReply(PlanetaryIndustryTests.Industry(f).Read(f.PilotId, PlanetaryIndustryTests.PlanetId))
            )
        );
    }

    [Fact]
    public void SurveySupportsTemporaryExtractorsBeforeSubmitAndReturnsStableFloat32Harmonics()
    {
        using var f = PlanetaryIndustryTests.Prepare();
        using var context = Context(f);
        var industry = PlanetaryIndustryTests.Industry(f);
        var handler = new PlanetaryIndustryHandler(industry);
        string service = $"@planet:{PlanetaryIndustryTests.PlanetId}";
        var quote = Assert.IsType<PilotCallResult.Success>(
            handler.Call(
                context,
                service,
                "GetProgramResultInfo",
                [
                    new object?[] { 1L, 0L },
                    (long)PlanetaryResources.AqueousLiquids,
                    new List<object?> { new object?[] { 0L, 1d, 1d } },
                    0.01d,
                ]
            )
        );
        var estimate = Assert.IsType<PlanetaryQuoteReply>(quote.Reply).Quote;
        Assert.Equal(4, estimate.Cycles);
        Assert.Equal(15 * TimeSpan.TicksPerMinute, estimate.CycleTicks);
        Assert.Null(industry.Read(f.PilotId, PlanetaryIndustryTests.PlanetId).Colony);
        var request = PlanetaryWire.KeyVal(
            new()
            {
                ["resourceTypeID"] = (long)PlanetaryResources.AqueousLiquids,
                ["oldBand"] = 0L,
                ["newBand"] = 30L,
                ["proximity"] = 1L,
            }
        );
        var result = Assert.IsType<PilotCallResult.Success>(handler.Call(context, service, "GetResourceData", [request]));
        var fields = Assert.IsType<Dictionary<string, object?>>(Assert.IsType<BlueInstance>(result.Reply.ToBlue()).State);
        var bytes = Assert.IsType<byte[]>(fields["data"]);
        Assert.Equal(30 * 30 * sizeof(float), bytes.Length);
        Assert.Equal(
            PlanetaryResources.Coefficients(PlanetaryIndustryTests.PlanetId, PlanetaryResources.AqueousLiquids, 30)[0],
            BinaryPrimitives.ReadSingleLittleEndian(bytes)
        );
        Assert.Equal(4, fields["proximity"]);
        Assert.Equal(30, fields["numBands"]);
        Assert.All(
            industry.ResourceField(f.PilotId, PlanetaryIndustryTests.PlanetId, PlanetaryResources.AqueousLiquids, 30).Coefficients,
            coefficient => Assert.True(float.IsFinite(coefficient))
        );
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void MalformedCommandStreamsFailBeforeChangingColonyState(int variant)
    {
        using var f = PlanetaryIndustryTests.Prepare();
        using var context = Context(f);
        object? command = variant switch
        {
            0 => new object?[] { 99L, Array.Empty<object?>() },
            1 => new object?[] { 1L, new object?[] { 123L, (long)PlanetaryIndustryTests.CommandCenter, double.NaN, 1d } },
            2 => new object?[] { 9L, new object?[] { 123L, long.MaxValue } },
            _ => new object?[] { 1L, new object?[] { 123L } },
        };
        var industry = PlanetaryIndustryTests.Industry(f);
        var handler = new PlanetaryIndustryHandler(industry);
        Assert.IsType<PilotCallResult.Failure>(
            handler.Call(context, $"@planet:{PlanetaryIndustryTests.PlanetId}", "UserUpdateNetwork", [new List<object?> { command }])
        );
        Assert.Empty(industry.List(f.PilotId));
        Assert.Empty(context.Notifications);
        Assert.IsType<PilotCallResult.UnsupportedCall>(
            handler.Call(context, "planetMgr", "UserUpdateNetwork", [new List<object?> { command }])
        );
    }

    [Fact]
    public void PublicNetworkExposesPositionsWithoutPrivateInventoryOrPrograms()
    {
        using var f = PlanetaryIndustryTests.Prepare();
        var colony = PlanetaryIndustryTests.Deploy(f);
        var reply = new PlanetaryPublicNetworkReply(colony);
        var tuple = Assert.IsType<object?[]>(reply.ToBlue());
        var pins = Assert.IsType<List<object?>>(tuple[0]);
        foreach (var row in pins)
        {
            var fields = Assert.IsType<Dictionary<string, object?>>(Assert.IsType<BlueInstance>(row).State);
            Assert.Contains("latitude", fields.Keys);
            Assert.DoesNotContain("contents", fields.Keys);
            Assert.DoesNotContain("programType", fields.Keys);
        }
    }
}
