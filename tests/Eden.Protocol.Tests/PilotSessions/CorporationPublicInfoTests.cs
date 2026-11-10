using Eden.Data.Catalog;
using Eden.Data.World.Corporations;
using Eden.Gameplay.Corporations;
using Eden.Protocol.Flight;
using Eden.Protocol.PilotSessions;
using Eden.Protocol.Serialization;
using Eden.World.Catalog;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Eden.Protocol.Tests.PilotSessions;

public sealed class CorporationPublicInfoTests
{
    private const long Viewer = 90000002;
    private const int NpcCorporation = 1000044;

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ShowInfoReturnsPublicFieldsThroughTheSessionForNpcAndOtherPlayerCorporations(bool playerCorporation)
    {
        using TestWorld db = new();
        int corporation = CreateCorporation(db, playerCorporation);
        using var scope = db.Services.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<IPilotSession>();
        Queue<object?> requests = new([
            PilotServicesTests.Request("charUnboundMgr", "SelectCharacterID", [Viewer]),
            PilotServicesTests.Request("corpmgr", "GetPublicInfo", [(long)corporation]),
            PilotServicesTests.Request("corpmgr", "GetPublicInfo", [(long)corporation]),
        ]);
        List<BlueInstance> packets = [];
        await Assert.ThrowsAsync<EndOfStreamException>(() =>
            session.RunAsync(
                () => requests.TryDequeue(out var next) ? Task.FromResult(next) : throw new EndOfStreamException(),
                packet =>
                {
                    packets.Add(Assert.IsType<BlueInstance>(packet));
                    return Task.CompletedTask;
                },
                _ => { },
                1000002,
                42,
                SignedSettings.Empty,
                CancellationToken.None
            )
        );
        Assert.DoesNotContain(packets, p => p.Name.EndsWith(".ErrorResponse", StringComparison.Ordinal));
        var replies = packets.Where(p => p.Name.EndsWith(".CallRsp", StringComparison.Ordinal)).ToArray();
        Assert.Equal(3, replies.Length);
        foreach (var reply in replies.Skip(1))
        {
            var bytes = BlueRead.Bytes(BlueRead.Tuple(BlueRead.Tuple(reply.State, 14)[4], 1)[0]);
            AssertPublicFields(BlueMarshal.Decode(bytes), corporation, playerCorporation);
        }
        using WorldDbContext saved = new(db.Path);
        Assert.Equal(NpcCorporation, saved.Pilots.Find(Viewer)!.CorporationId);
        Assert.Equal(playerCorporation ? 1 : 0, saved.Set<SavedCorporation>().Count());
        Assert.Empty(saved.Set<SavedCorporationApplication>());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void PublicReadRequiresSelectionRejectsMalformedRequestsAndLeavesSavedStateUnchanged(bool playerCorporation)
    {
        using TestWorld db = new();
        int corporation = CreateCorporation(db, playerCorporation);
        using var scope = db.Services.CreateScope();
        var pilot = db.Store.FindPilot(1000002)!;
        using PilotCallContext context = new(
            1000002,
            pilot.Account,
            [pilot],
            scope.ServiceProvider.GetRequiredService<FlightSessionFactory>().Create,
            42,
            SignedSettings.Empty
        );
        var router = scope.ServiceProvider.GetRequiredService<PilotCallRouter>();
        PilotCallResult Call(object?[] args, Dictionary<string, object?>? keywords = null) =>
            router.Call(context, "corpmgr", "GetPublicInfo", BlueMarshal.Decode(BlueMarshal.Encode(args)), _ => { }, keywords);
        Assert.Same(PilotCallResult.Unsupported, Call([(long)corporation]));
        context.Select(pilot);
        foreach (
            object?[] args in new object?[][]
            {
                [],
                [null],
                [true],
                ["1000044"],
                [0L],
                [-1L],
                [long.MaxValue],
                [int.MaxValue],
                [(long)corporation, true],
            }
        )
        {
            Assert.IsType<PilotCallResult.Failure>(Call(args));
        }
        Assert.IsType<PilotCallResult.Failure>(Call([(long)corporation], new() { ["characterID"] = Viewer }));
        var result = Assert.IsType<PilotCallResult.Success>(Call([(long)corporation], new() { ["machoVersion"] = 1L }));
        AssertPublicFields(BlueMarshal.Decode(BlueMarshal.Encode(result.Reply)), corporation, playerCorporation);
        Assert.Empty(context.Notifications);
        Assert.Equal(pilot, context.Pilot);
        Assert.Equal(pilot, db.Store.FindPilot(1000002));
    }

    [Fact]
    public void PublicReadReflectsSavedDescriptionUpdates()
    {
        using TestWorld db = new();
        int id = CreateCorporation(db, true);
        using var scope = db.Services.CreateScope();
        var pilot = db.Store.FindPilot(1000002)!;
        using PilotCallContext context = new(
            1000002,
            pilot.Account,
            [pilot],
            scope.ServiceProvider.GetRequiredService<FlightSessionFactory>().Create,
            42,
            SignedSettings.Empty
        );
        context.Select(pilot);
        var router = scope.ServiceProvider.GetRequiredService<PilotCallRouter>();
        string Description()
        {
            var result = Assert.IsType<PilotCallResult.Success>(
                router.Call(context, "corpmgr", "GetPublicInfo", new object?[] { (long)id }, _ => { })
            );
            var reply = Assert.IsType<BlueInstance>(BlueMarshal.Decode(BlueMarshal.Encode(result.Reply)));
            return BlueMarshal.Text(BlueRead.Fields(reply.State)["description"]);
        }
        Assert.Equal(PlayerCorporationTests.Details().Description, Description());
        db.Services.GetRequiredService<ICorporationAdministration>().Update(90000001, new("Updated 星 description", "", 0.2, false, 0));
        Assert.Equal("Updated 星 description", Description());
        Assert.Empty(context.Notifications);
    }

    private static int CreateCorporation(TestWorld db, bool playerCorporation)
    {
        using (CatalogDbContext catalog = new(new WorldDatabase(db.Path).CatalogPath, writable: true))
        {
            catalog.Database.ExecuteSqlRaw(
                """
                UPDATE NpcCorporation SET Name='School of Applied Knowledge' WHERE Id=1000044;
                INSERT INTO SourceRecord(Dataset,Key,Data) VALUES ('client/npccorporations','1000044',
                    '{{"stationID":60003760,"solarSystemID":30000142,"tickerName":"SAK","ceoID":3004181}}');
                """
            );
        }
        if (!playerCorporation)
        {
            return NpcCorporation;
        }
        PlayerCorporationTests.Prepare(db);
        return db.Services.GetRequiredService<IPlayerCorporations>().Create(90000001, PlayerCorporationTests.Details()).Corporation.Id;
    }

    private static void AssertPublicFields(object? value, int corporation, bool playerCorporation)
    {
        var reply = Assert.IsType<BlueInstance>(value);
        Assert.Equal("utillib.KeyVal", reply.Name);
        var fields = BlueRead.Fields(reply.State);
        Assert.Equal((long)corporation, fields["corporationID"]);
        Assert.Equal(playerCorporation ? 90000001L : 3004181L, fields["ceoID"]);
        Assert.Equal(60003760L, fields["stationID"]);
        Assert.Equal(30000142L, fields["solarSystemID"]);
        Assert.Equal(playerCorporation ? "Eden Explorers" : "School of Applied Knowledge", BlueMarshal.Text(fields["corporationName"]));
        Assert.Equal(playerCorporation ? "EDEN" : "SAK", BlueMarshal.Text(fields["tickerName"]));
        Assert.Equal(playerCorporation ? "星間 explorers" : "", BlueMarshal.Text(fields["description"]));
        Assert.Null(fields["allianceID"]);
        Assert.False(Assert.IsType<bool>(fields["deleted"]));
        Assert.False(Assert.IsType<bool>(fields["allowWar"]));
        Assert.IsType<long>(fields["shares"]);
        Assert.DoesNotContain("roles", fields.Keys);
        Assert.DoesNotContain("walletDivision2", fields.Keys);
    }
}
