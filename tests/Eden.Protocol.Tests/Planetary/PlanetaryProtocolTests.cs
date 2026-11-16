using System.Buffers.Binary;
using System.Globalization;
using System.Text.Json;
using Eden.Data.World.Inventory;
using Eden.Data.World.Market;
using Eden.Data.World.Planetary;
using Eden.Gameplay.Configuration;
using Eden.Gameplay.Flight;
using Eden.Gameplay.Inventory;
using Eden.Gameplay.Planetary;
using Eden.Protocol.Docking;
using Eden.Protocol.Flight;
using Eden.Protocol.Loot;
using Eden.Protocol.Market;
using Eden.Protocol.PilotSessions;
using Eden.Protocol.Planetary;
using Eden.Protocol.Serialization;
using Eden.World.Configuration;
using Eden.World.Runtime;
using Eden.World.Storage;
using Microsoft.Extensions.DependencyInjection;
using Fixture = Eden.Protocol.Tests.Persistence.PassiveFittingTests.Fixture;

namespace Eden.Protocol.Tests.Planetary;

public sealed class PlanetaryProtocolTests
{
    [Fact]
    public void CustomsCapacityAndTaxRepliesExposeActiveConfiguration()
    {
        using var f = PlanetaryIndustryTests.Prepare();
        WorldDatabase database = new(f.Db.Path);
        WorldConfigurationStore configuration = new(database, f.Clock);
        var current = configuration.Read();
        configuration.Save(
            current.Generation,
            current.Active.Draft with
            {
                Settings = current.Active.Draft.Settings with
                {
                    PlanetaryIndustry = new(StorageCapacityMultiplier: 100, CustomsTaxPercent: 2.5m),
                },
            },
            "customs wire test"
        );
        WorldConfigurationStore.Activate(database);
        using LiveWorld live = new(database, f.Clock, combatObjects: false, simulationObjects: false);
        PlanetaryIndustryTests.Deploy(f);
        var industry = PlanetaryIndustryTests.Industry(f);
        long office = PlanetaryRules.OfficeId(PlanetaryIndustryTests.PlanetId);
        var contact = Assert.Single(industry.OrbitContacts(f.Db.Store.FindPilot(1000001)!.SystemId, f.Clock.Now));
        f.Db.Store.SetPosition(f.ShipId, contact.Position);
        long cargo = f.Grant(PlanetaryIndustryTests.Water, 100000, cargo: true);
        industry.MoveOrbitCargo(f.PilotId, office, true, new([cargo], f.ShipId, (int)InventoryFlag.Hangar, null));
        using var context = Context(f);
        context.Flight.Snapshot();
        var loot = new LootHandler(
            f.Db.Services.GetRequiredService<Eden.Gameplay.Loot.IWrecks>(),
            f.Db.Services.GetRequiredService<Eden.Gameplay.Loot.ICargoJettison>(),
            f.Clock
        );
        var capacity = Assert.IsType<JetcanCapacityReply>(
            Assert.IsType<PilotCallResult.Success>(loot.Call(context, WreckWireDefinitions.BindingPrefix + office, "GetCapacity", [])).Reply
        );
        Assert.Equal(3500000, capacity.Capacity);
        Assert.Equal(PlanetaryIndustryTests.Catalog(f).Volumes[PlanetaryIndustryTests.Water] * 100000, capacity.Used);
        var tax = new PlanetaryInventoryHandler(industry).Call(context, "planetOrbitalRegistryBroker", "GetTaxRate", [office]);
        Assert.Equal(.025, Assert.IsType<PilotCallResult.Success>(tax).Reply.ToBlue());
    }

    [Fact]
    public void CargoAdapterRejectsMalformedMapsAndRetainsEntryAndIntegerBounds()
    {
        object?[] malformed =
        [
            null,
            Array.Empty<object?>(),
            new List<object?>(),
            new Dictionary<string, object?> { ["bad"] = 1L },
            new Dictionary<long, object?> { [1L] = 1d },
            Enumerable.Range(1, 129).ToDictionary(i => (long)i, _ => (object?)1L),
        ];
        foreach (object? value in malformed)
        {
            Assert.Throws<InvalidDataException>(() => PlanetaryRequestWire.Cargo(value));
        }
        Assert.Throws<OverflowException>(() => PlanetaryRequestWire.Cargo(new Dictionary<long, object?> { [1L] = long.MaxValue }));
    }

    [Fact]
    public async Task CustomsWireTransfersAcceptEmptyOppositeMapsAndPreserveGoodsTaxesAndRejections()
    {
        using var f = PlanetaryIndustryTests.Prepare();
        using LiveWorld live = new(new(f.Db.Path), f.Clock, _ => { });
        var colony = PlanetaryIndustryTests.Deploy(f);
        var industry = PlanetaryIndustryTests.Industry(f);
        long office = PlanetaryRules.OfficeId(PlanetaryIndustryTests.PlanetId);
        var contact = Assert.Single(industry.OrbitContacts(f.Db.Store.FindPilot(1000001)!.SystemId, f.Clock.Now));
        f.Db.Store.SetPosition(f.ShipId, contact.Position);
        long cargo = f.Grant(PlanetaryResources.AqueousLiquids, 6000, cargo: true);
        industry.MoveOrbitCargo(f.PilotId, office, true, new([cargo], f.ShipId, (int)InventoryFlag.Hangar, null));
        var importedStack = Assert.Single(industry.OrbitInventory(f.PilotId, office).Items);
        long port = colony.Pins.Single(p => p.TypeId == PlanetaryIndustryTests.Launchpad).Id;
        using var context = Context(f);
        context.Flight.Snapshot();
        var handler = new PlanetaryInventoryHandler(industry);
        PilotCallResult Transfer(object imports, object exports)
        {
            // Exercise the native marshal boundary: an empty Python dictionary has no key type.
            var args = BlueRead.Tuple(BlueMarshal.Decode(BlueMarshal.Encode(new object?[] { port, imports, exports, 0d })), 4);
            return handler.Call(context, WreckWireDefinitions.BindingPrefix + office, "ImportExportWithPlanet", args);
        }
        Dictionary<long, object?> empty = [];
        Assert.Same(PilotCallResult.None, Transfer(new Dictionary<long, object?> { [importedStack.Id] = 6000L }, empty));
        Assert.Empty(industry.OrbitInventory(f.PilotId, office).Items);
        Assert.Equal(
            0,
            PlanetarySimulation.Quantity(
                industry.Read(f.PilotId, PlanetaryIndustryTests.PlanetId).Colony!.Pins.Single(p => p.Id == port),
                PlanetaryResources.AqueousLiquids
            )
        );
        double afterImport = Assert.Single(context.Notifications.OfType<AccountBalanceChanged>()).Balance;
        Assert.Single(context.Notifications.OfType<PlanetaryStateChanged>());
        Assert.Contains(context.Notifications, n => n is ItemChanged);
        context.Notifications.Clear();
        f.Clock.Seconds += 1800;
        colony = industry.Read(f.PilotId, PlanetaryIndustryTests.PlanetId).Colony!;
        Assert.Equal(20, PlanetarySimulation.Quantity(colony.Pins.Single(p => p.Id == port), PlanetaryIndustryTests.Water));
        Assert.Same(PilotCallResult.None, Transfer(empty, new Dictionary<long, object?> { [PlanetaryIndustryTests.Water] = 5L }));
        colony = industry.Read(f.PilotId, PlanetaryIndustryTests.PlanetId).Colony!;
        Assert.Equal(15, PlanetarySimulation.Quantity(colony.Pins.Single(p => p.Id == port), PlanetaryIndustryTests.Water));
        var exportedStack = Assert.Single(industry.OrbitInventory(f.PilotId, office).Items);
        Assert.Equal(PlanetaryIndustryTests.Water, exportedStack.TypeId);
        Assert.Equal(5, exportedStack.Quantity);
        // P1 export: 5 units * 400 ISK base value * 17% authoritative tax, despite the zero client quote.
        double afterExport = Assert.Single(context.Notifications.OfType<AccountBalanceChanged>()).Balance;
        Assert.Equal(afterImport - 340, afterExport);
        Assert.Single(context.Notifications.OfType<PlanetaryStateChanged>());
        context.Notifications.Clear();
        string before = JsonSerializer.Serialize(colony);
        Assert.IsType<PilotCallResult.Failure>(Transfer(empty, new Dictionary<long, object?> { [PlanetaryIndustryTests.Water] = 20L }));
        Assert.IsType<PilotCallResult.Failure>(Transfer(new Dictionary<long, object?> { [exportedStack.Id] = 6L }, empty));
        Assert.IsType<PilotCallResult.Failure>(Transfer(empty, new Dictionary<long, object?> { [PlanetaryIndustryTests.Water] = -1L }));
        Assert.IsType<PilotCallResult.Failure>(Transfer(new Dictionary<string, object?> { ["bad"] = 1L }, empty));
        Assert.IsType<PilotCallResult.Failure>(Transfer(empty, new Dictionary<string, object?> { ["bad"] = 1L }));
        Assert.IsType<PilotCallResult.Failure>(Transfer(empty, empty));
        Assert.Empty(context.Notifications);
        Assert.Equal(before, JsonSerializer.Serialize(industry.Read(f.PilotId, PlanetaryIndustryTests.PlanetId).Colony));
        Assert.Equal(exportedStack, Assert.Single(industry.OrbitInventory(f.PilotId, office).Items));
        await live.FlushAsync();
        live.Dispose();
        using WorldDbContext saved = new(f.Db.Path);
        Assert.Equal(before, saved.Set<SavedPlanetaryColony>().Find(f.PilotId, PlanetaryIndustryTests.PlanetId)!.StateJson);
        Assert.Equal(Convert.ToInt64(afterExport * 100), saved.Set<PilotWallet>().Find(f.PilotId)!.Cents);
        Assert.Equal(5, saved.Set<OwnedItem>().Find(exportedStack.Id)!.Quantity);
    }

    [Theory]
    [InlineData(PlanetaryResources.Temperate)]
    [InlineData(PlanetaryResources.Ice)]
    [InlineData(PlanetaryResources.Gas)]
    [InlineData(PlanetaryResources.Oceanic)]
    [InlineData(PlanetaryResources.Lava)]
    [InlineData(PlanetaryResources.Barren)]
    [InlineData(PlanetaryResources.Storm)]
    [InlineData(PlanetaryResources.Plasma)]
    public void ResourceAbundanceUsesNativeQualityScaleAndMatchesTheSurveyedField(int planetType)
    {
        // Build 3662168 appConst.MAX_DISPLAY_QUALITY = 1.21 * 255 * 0.5.
        const double NativeMaximumDisplayQuality = 154.275;
        var planet = new PlanetaryPlanet(PlanetaryIndustryTests.PlanetId, 1, planetType, 1, 6000000, SpacePosition.Zero, true);
        var encoded = BlueMarshal.Encode(new PlanetaryResourceInfoReply(planet));
        var cached = Assert.IsType<BlueInstance>(BlueMarshal.Decode(encoded));
        Assert.Equal("carbon.common.script.net.objectCaching.CachedMethodCallResult", cached.Name);
        var state = BlueRead.Tuple(cached.State, 3);
        var resources = Assert.IsType<Dictionary<long, object?>>(BlueMarshal.Decode(BlueRead.Bytes(state[1])));
        Assert.Equal(PlanetaryResources.Types(planetType).Order(), resources.Keys.Select(id => checked((int)id)).Order());
        foreach (int type in PlanetaryResources.Types(planetType))
        {
            double quality = Convert.ToDouble(resources[type], CultureInfo.InvariantCulture);
            double fraction = quality / NativeMaximumDisplayQuality;
            Assert.InRange(fraction, 0.3, 0.6);
            Assert.IsType<double>(resources[type]);
            double mean = (PlanetaryResources.Density(planet.Id, type, 0, 0) + PlanetaryResources.Density(planet.Id, type, Math.PI, 0)) / 2;
            Assert.Equal(mean / 1.21, fraction, 6);
        }
        Assert.True(resources.Values.Select(value => Assert.IsType<double>(value)).Distinct().Count() > 1);
        Assert.Equal(encoded, BlueMarshal.Encode(new PlanetaryResourceInfoReply(planet)));
    }

    [Theory]
    [InlineData(PlanetaryResources.Temperate)]
    [InlineData(PlanetaryResources.Ice)]
    [InlineData(PlanetaryResources.Gas)]
    [InlineData(PlanetaryResources.Oceanic)]
    [InlineData(PlanetaryResources.Lava)]
    [InlineData(PlanetaryResources.Barren)]
    [InlineData(PlanetaryResources.Storm)]
    [InlineData(PlanetaryResources.Plasma)]
    public void ResourceMapBufferProducesVisibleNativeIntensityWithoutChangingAuthoritativeDensity(int planetType)
    {
        // The native renderer scales SH by 1 / 1.21, then derives alpha from max(sample) / 255.
        const double NativeMaximumResourceValue = 1.21;
        const double ConstantHarmonic = 0.28209479177387814;
        foreach (int type in PlanetaryResources.Types(planetType))
        {
            var field = new PlanetaryResourceField(30, 4, PlanetaryResources.Coefficients(PlanetaryIndustryTests.PlanetId, type, 30));
            float[] original = field.Coefficients.ToArray();
            var reply = Assert.IsType<BlueInstance>(BlueMarshal.Decode(BlueMarshal.Encode(new PlanetaryResourceReply(field))));
            var values = BlueRead.Fields(reply.State);
            byte[] buffer = BlueRead.Bytes(values["data"]);
            Assert.Equal(30L, values["numBands"]);
            Assert.Equal(4L, values["proximity"]);
            Assert.Equal(30 * 30 * sizeof(float), buffer.Length);
            for (int i = 0; i < original.Length; i++)
            {
                Assert.Equal(original[i] * byte.MaxValue, BinaryPrimitives.ReadSingleLittleEndian(buffer.AsSpan(i * sizeof(float))));
            }
            double mean = BinaryPrimitives.ReadSingleLittleEndian(buffer) * ConstantHarmonic / byte.MaxValue;
            double oppositePointMean =
                (
                    PlanetaryResources.Density(PlanetaryIndustryTests.PlanetId, type, 0, 0)
                    + PlanetaryResources.Density(PlanetaryIndustryTests.PlanetId, type, Math.PI, 0)
                ) / 2;
            Assert.Equal(oppositePointMean, mean, 6);
            Assert.InRange(mean / NativeMaximumResourceValue, 0.3, 0.6);
            Assert.Equal(original, field.Coefficients);
        }
    }

    [Fact]
    public void SurveyPreviewQuotesMatch100xInstalledExtractionBeforeAndAfterSubmit()
    {
        using var f = PlanetaryIndustryTests.Prepare();
        WorldDatabase database = new(f.Db.Path);
        WorldConfigurationStore configuration = new(database, f.Clock);
        var current = configuration.Read();
        configuration.Save(
            current.Generation,
            current.Active.Draft with
            {
                Settings = current.Active.Draft.Settings with { PlanetaryIndustry = new(ExtractionYieldMultiplier: 100) },
            },
            "preview test"
        );
        WorldConfigurationStore.Activate(database);
        using var context = Context(f);
        var industry = PlanetaryIndustryTests.Industry(f);
        var handler = new PlanetaryIndustryHandler(industry);
        string service = $"@planet:{PlanetaryIndustryTests.PlanetId}";
        var heads = new List<object?> { new object?[] { 0L, 1d, 1.002d } };
        PlanetaryProgramQuote Quote(object pin)
        {
            var result = Assert.IsType<PilotCallResult.Success>(
                handler.Call(
                    context,
                    service,
                    "GetProgramResultInfo",
                    [pin, (long)PlanetaryResources.AqueousLiquids, heads, PlanetaryRules.MinimumHeadRadius]
                )
            );
            return Assert.IsType<PlanetaryQuoteReply>(result.Reply).Quote;
        }
        var before = Quote(new object?[] { 1L, 0L });
        Assert.Null(industry.Read(f.PilotId, PlanetaryIndustryTests.PlanetId).Colony);
        var colony = PlanetaryIndustryTests.Deploy(f);
        var extractor = Assert.Single(colony.Pins, p => p.TypeId == PlanetaryIndustryTests.Extractor);
        var after = Quote(extractor.Id);
        Assert.Equal(before, after);
        Assert.Equal(after.BaseQuantity, extractor.Program!.BaseQuantity);
        Assert.Equal(after.CycleTicks, extractor.Program.CycleTicks);
        Assert.Equal(after.CycleTicks * after.Cycles, extractor.Program.ExpiresAt - extractor.Program.InstalledAt);
        var ordinary = PlanetaryRules.Quote(
            industry.Read(f.PilotId, PlanetaryIndustryTests.PlanetId).Planet,
            PlanetaryIndustryTests.Catalog(f),
            extractor,
            PlanetaryResources.AqueousLiquids,
            extractor.Heads!,
            PlanetaryRules.MinimumHeadRadius,
            colony.Pins
        );
        Assert.Equal(ordinary.BaseQuantity * 100, after.BaseQuantity);
    }

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
            PlanetaryResources.Coefficients(PlanetaryIndustryTests.PlanetId, PlanetaryResources.AqueousLiquids, 30)[0] * byte.MaxValue,
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
