using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Eden.Data.Catalog.Capsuleers;
using Eden.Data.World.Capsuleers;
using Eden.Gameplay.Inventory;
using Eden.Server.Capsuleers;
using Eden.World.Capsuleers;
using Eden.World.Catalog;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;

namespace Eden.Protocol.Tests.Capsuleers;

public sealed class TrafficImportTests
{
    [Fact]
    public async Task ImportDeduplicatesArchivesResolvesNamesAndPublishesVerifiedOfflineSnapshot()
    {
        using CapsuleerHaulerTests.Fixture fixture = new(legacyFounders: false, isolateRoute: true);
        using ArchiveHandler handler = new();
        using HttpClient client = new(handler);
        string output = Path.Combine(Path.GetDirectoryName(fixture.Database.Path)!, "traffic.sqlite");
        var metadata = await TrafficImportCommand.Import(
            client,
            fixture.Database.CatalogPath,
            output,
            new(2026, 11, 1),
            new(2026, 11, 2),
            _ => { }
        );
        Assert.Equal(2, metadata.Records);
        Assert.Equal(1, metadata.Duplicates);
        Assert.Equal(1, metadata.Accepted);
        Assert.Equal(1, metadata.Pilots);
        Assert.Equal(2, metadata.Archives.Length);
        Assert.All(metadata.Archives, archive => Assert.Equal(64, archive.Sha256.Length));
        Assert.Equal(3, handler.Calls);
        fixture.Database.LoadTraffic();
        Assert.Equal("Real Traffic Pilot", Assert.Single(fixture.Database.Traffic.Profiles).Name);
        // Completed archives and name resolutions are reused without HTTP.
        handler.Fail = true;
        var repeated = await TrafficImportCommand.Import(
            client,
            fixture.Database.CatalogPath,
            output,
            new(2026, 11, 1),
            new(2026, 11, 2),
            _ => { }
        );
        Assert.Equal(metadata.Identity, repeated.Identity);
        Assert.Equal(3, handler.Calls);
        using WorldDbContext world = new(fixture.Database.Path);
        Assert.Empty(world.Set<SavedTrafficIdentity>());
    }

    [Theory]
    [InlineData("unknown type")]
    [InlineData("invalid name")]
    [InlineData("unavailable name")]
    [InlineData("cancelled")]
    [InlineData("malformed archive")]
    public async Task FailedImportPreservesInstalledSnapshot(string failure)
    {
        using CapsuleerHaulerTests.Fixture fixture = new(legacyFounders: false, isolateRoute: true);
        TrafficSnapshotTests.Install(fixture.Database, TrafficSnapshotTests.Profile());
        string output = Path.Combine(Path.GetDirectoryName(fixture.Database.Path)!, "traffic.sqlite");
        byte[] before = File.ReadAllBytes(output);
        using ArchiveHandler handler = new() { Failure = failure };
        using HttpClient client = new(handler);
        using CancellationTokenSource cancellation = new();
        if (failure == "cancelled")
        {
            cancellation.Cancel();
        }
        var error = await Record.ExceptionAsync(() =>
            TrafficImportCommand.Import(
                client,
                fixture.Database.CatalogPath,
                output,
                new(2026, 11, 1),
                new(2026, 11, 1),
                _ => { },
                cancellation.Token
            )
        );
        Assert.NotNull(error);
        if (failure == "cancelled")
        {
            Assert.IsAssignableFrom<OperationCanceledException>(error);
        }
        else if (failure == "malformed archive")
        {
            Assert.IsAssignableFrom<JsonException>(error);
        }
        else
        {
            Assert.IsType<InvalidDataException>(error);
        }
        Assert.Equal(before, File.ReadAllBytes(output));
        Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(output)!, "traffic.sqlite.*.sqlite"));
    }

    [Fact]
    public async Task ImportRefusesToReplaceWorldOrCatalogDatabase()
    {
        using CapsuleerHaulerTests.Fixture fixture = new(legacyFounders: false, isolateRoute: true);
        using ArchiveHandler handler = new();
        using HttpClient client = new(handler);
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            TrafficImportCommand.Import(
                client,
                fixture.Database.CatalogPath,
                fixture.Database.Path,
                new(2026, 11, 1),
                new(2026, 11, 1),
                _ => { }
            )
        );
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            TrafficImportCommand.Import(
                client,
                fixture.Database.CatalogPath,
                fixture.Database.CatalogPath,
                new(2026, 11, 1),
                new(2026, 11, 1),
                _ => { }
            )
        );
        Assert.Equal(0, handler.Calls);
    }

    [Fact]
    public async Task DefaultWindowUsesLatestPublishedCompleteDayAndIgnoresFutureOrEmptyDates()
    {
        using ArchiveHandler handler = new();
        using HttpClient client = new(handler);
        var downloader = new TrafficDownloader(client, "unused", _ => { });
        Assert.Equal(new DateOnly(2026, 11, 7), await downloader.LatestCompleteDate(new(2026, 11, 8), CancellationToken.None));
    }

    [Fact]
    public void ArchiveParserKeepsOnlyFittedItemsAndLoadedChargesIncludingNestedCharges()
    {
        var items = new TrafficKillItem[]
        {
            new(561, (int)InventoryFlag.HighSlot0, 1, 0, [new(215, (int)InventoryFlag.Cargo, 20, 30, null)]),
            new(1317, (int)InventoryFlag.LowSlot0, 0, 1, null),
            new(215, (int)InventoryFlag.Cargo, 1000, 0, null),
            new(2488, (int)InventoryFlag.DroneBay, 5, 0, null),
            new(1, (int)InventoryFlag.Implant, 1, 0, null),
        };
        var equipment = TrafficKillItem.Equipment(items);
        Assert.Equal(3, equipment.Length);
        Assert.Contains(new TrafficEquipment(215, (int)InventoryFlag.HighSlot0, 50), equipment);
        Assert.Contains(new TrafficEquipment(561, (int)InventoryFlag.HighSlot0, 1), equipment);
        Assert.Contains(new TrafficEquipment(1317, (int)InventoryFlag.LowSlot0, 1), equipment);
    }

    [Theory]
    [InlineData("--import-traffic")]
    [InlineData("--import-traffic traffic.sqlite --from 2026-11-02 --through 2026-11-01")]
    [InlineData("--import-traffic traffic.sqlite --catalog")]
    public async Task CommandRejectsMissingArgumentsAndReversedDatesBeforeAccessingData(string args) =>
        await Assert.ThrowsAsync<InvalidDataException>(() => TrafficImportCommand.TryRun(args.Split(' ')));

    private sealed class ArchiveHandler : HttpMessageHandler
    {
        internal int Calls { get; private set; }
        internal bool Fail { get; set; }
        internal string? Failure { get; init; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            if (Fail)
            {
                throw new HttpRequestException("Unexpected uncached request.");
            }
            if (request.Method == HttpMethod.Post)
            {
                return Task.FromResult(
                    Failure == "unavailable name"
                        ? new HttpResponseMessage(HttpStatusCode.NotFound)
                        : new HttpResponseMessage(HttpStatusCode.OK)
                        {
                            Content = JsonContent.Create(
                                new[]
                                {
                                    new TrafficCharacter(
                                        900000001,
                                        Failure == "invalid name" ? "Invalid@Name" : "Real Traffic Pilot",
                                        "character"
                                    ),
                                }
                            ),
                        }
                );
            }
            if (request.RequestUri!.AbsolutePath.EndsWith("totals.json", StringComparison.Ordinal))
            {
                return Task.FromResult(
                    new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = JsonContent.Create(
                            new Dictionary<string, long>
                            {
                                ["20261107"] = 100,
                                ["20261108"] = 0,
                                ["20261109"] = 100,
                                ["invalid"] = 100,
                            }
                        ),
                    }
                );
            }
            if (Failure == "malformed archive")
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("truncated{") });
            }
            var kill = new TrafficKillmail(
                123456789,
                DateTimeOffset.Parse("2026-11-01T12:00:00Z"),
                30000141,
                new(900000001, 648, [new(Failure == "unknown type" ? int.MaxValue : 1317, (int)InventoryFlag.LowSlot0, 1, 0, null)])
            );
            return Task.FromResult(
                new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(JsonSerializer.Serialize(new Dictionary<string, TrafficKillmail> { ["123456789"] = kill })),
                }
            );
        }
    }
}
