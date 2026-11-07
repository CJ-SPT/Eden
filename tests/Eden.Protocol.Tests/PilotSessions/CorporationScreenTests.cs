using Eden.Data.Catalog;
using Eden.Data.World.Corporations;
using Eden.Gameplay.Corporations;
using Eden.Protocol.Flight;
using Eden.Protocol.PilotSessions;
using Eden.Protocol.Serialization;
using Eden.World.Catalog;
using Eden.World.Corporations;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Eden.Protocol.Tests.PilotSessions;

public sealed class CorporationScreenTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DetailsStartupRoutesTheNativeEnlistmentQueryAndItsSurroundingReads(bool playerCorporation)
    {
        using TestWorld db = new();
        using (CatalogDbContext catalog = new(new WorldDatabase(db.Path).CatalogPath, writable: true))
        {
            catalog.Database.ExecuteSqlRaw(
                """
                INSERT INTO SourceRecord(Dataset,Key,Data) VALUES ('client/npccorporations','1000044',
                    '{{"stationID":60003760,"solarSystemID":30000142,"tickerName":"SAK","ceoID":3004181}}');
                """
            );
        }
        if (playerCorporation)
        {
            PlayerCorporationTests.Prepare(db);
            db.Services.GetRequiredService<IPlayerCorporations>().Create(90000001, PlayerCorporationTests.Details());
        }
        var pilot = db.Store.FindPilot(1000001)!;
        using var scope = db.Services.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<IPilotSession>();
        Queue<object?> requests = new([
            PilotServicesTests.Request("charUnboundMgr", "SelectCharacterID", [pilot.PilotId]),
            PilotServicesTests.Request("voteManager", "CanViewVotes", [(long)pilot.CorpId]),
            PilotServicesTests.Request("corpRegistry", "GetCorporation", []),
            PilotServicesTests.Request("fwCharacterEnlistmentMgr", "GetCorpAllowedEnlistmentFactions", [(long)pilot.CorpId]),
            PilotServicesTests.Request("corpRegistry", "DoesMyCorpAcceptStructures", []),
            PilotServicesTests.Request("corpRegistry", "DoesCorpRestrictCorpMails", []),
            PilotServicesTests.Request("corpRegistry", "GetAggressionSettings", []),
            PilotServicesTests.Request("officeManager", "GetMyCorporationsOffices", []),
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
                1000001,
                42,
                SignedSettings.Empty,
                CancellationToken.None
            )
        );
        Assert.DoesNotContain(packets, p => p.Name.EndsWith(".ErrorResponse", StringComparison.Ordinal));
        var replies = packets.Where(p => p.Name.EndsWith(".CallRsp", StringComparison.Ordinal)).ToArray();
        Assert.Equal(8, replies.Length);
        Assert.False(Assert.IsType<bool>(Reply(replies[1])));
        Assert.NotEmpty(BlueRead.Bytes(BlueRead.Tuple(BlueRead.Tuple(replies[2].State, 14)[4], 1)[0]));
        Assert.Empty(Assert.IsType<List<object?>>(Reply(replies[3])));
        Assert.False(Assert.IsType<bool>(Reply(replies[4])));
        Assert.False(Assert.IsType<bool>(Reply(replies[5])));
        var aggression = Assert.IsType<BlueInstance>(Reply(replies[6]));
        Assert.Equal("crimewatch.corp_aggression.settings.AggressionSettings", aggression.Name);
        Assert.Equal(0L, BlueRead.Fields(aggression.State)["_disableAfter"]);
        Assert.Empty(Assert.IsType<List<object?>>(Reply(replies[7])));
        Assert.Equal(pilot, db.Store.FindPilot(1000001));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void EnlistmentReadRequiresSelectionAndValidArgumentsWithoutEnablingFactionWarfare(bool playerCorporation)
    {
        using TestWorld db = new();
        if (playerCorporation)
        {
            PlayerCorporationTests.Prepare(db);
            db.Services.GetRequiredService<IPlayerCorporations>().Create(90000001, PlayerCorporationTests.Details());
        }
        using var scope = db.Services.CreateScope();
        var pilot = db.Store.FindPilot(1000001)!;
        using PilotCallContext context = new(
            1000001,
            pilot.Account,
            [pilot],
            scope.ServiceProvider.GetRequiredService<FlightSessionFactory>().Create,
            42,
            SignedSettings.Empty
        );
        var router = scope.ServiceProvider.GetRequiredService<PilotCallRouter>();
        PilotCallResult Call(object?[] args, Dictionary<string, object?>? keywords = null) =>
            router.Call(
                context,
                "fwCharacterEnlistmentMgr",
                "GetCorpAllowedEnlistmentFactions",
                BlueMarshal.Decode(BlueMarshal.Encode(args)),
                _ => { },
                keywords
            );
        Assert.Same(PilotCallResult.Unsupported, Call([(long)pilot.CorpId]));
        context.Select(pilot);
        foreach (object?[] args in new object?[][] { [], [null], ["1000044"], [0L], [-1L], [long.MaxValue], [(long)pilot.CorpId, true] })
        {
            Assert.IsType<PilotCallResult.Failure>(Call(args));
        }
        Assert.IsType<PilotCallResult.Failure>(Call([(long)pilot.CorpId], new() { ["characterID"] = pilot.PilotId }));
        var result = Assert.IsType<PilotCallResult.Success>(Call([(long)pilot.CorpId], new() { ["machoVersion"] = 1L }));
        Assert.Empty(Assert.IsType<List<object?>>(BlueMarshal.Decode(BlueMarshal.Encode(result.Reply))));
        Assert.Same(
            PilotCallResult.Unsupported,
            router.Call(context, "fwCharacterEnlistmentMgr", "JoinFaction", new object?[] { 500001L }, _ => { })
        );
        Assert.Equal(pilot, context.Pilot);
        Assert.Empty(context.Notifications);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task WindowVotingCapabilityReturnsNativeFalseOnRepeatedOpensForNpcMembersAndPlayerCeos(bool playerCorporation)
    {
        using TestWorld db = new();
        if (playerCorporation)
        {
            PlayerCorporationTests.Prepare(db);
            db.Services.GetRequiredService<IPlayerCorporations>().Create(90000001, PlayerCorporationTests.Details());
        }
        int corporation = db.Store.FindPilot(1000001)!.CorpId;
        using var scope = db.Services.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<IPilotSession>();
        Queue<object?> requests = new([
            PilotServicesTests.Request("charUnboundMgr", "SelectCharacterID", [90000001L]),
            PilotServicesTests.Request("voteManager", "CanViewVotes", [(long)corporation]),
            PilotServicesTests.Request("voteManager", "CanViewVotes", [(long)corporation]),
            PilotServicesTests.Request("voteManager", "CanViewVotes", [(long)corporation]),
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
                1000001,
                42,
                SignedSettings.Empty,
                CancellationToken.None
            )
        );
        Assert.DoesNotContain(packets, p => p.Name.EndsWith(".ErrorResponse", StringComparison.Ordinal));
        var replies = packets.Where(p => p.Name.EndsWith(".CallRsp", StringComparison.Ordinal)).ToArray();
        Assert.Equal(4, replies.Length);
        foreach (var reply in replies.Skip(1))
        {
            var state = BlueRead.Tuple(reply.State, 14);
            var result = BlueMarshal.Decode(BlueRead.Bytes(BlueRead.Tuple(state[4], 1)[0]));
            Assert.False(Assert.IsType<bool>(result));
        }
        Assert.Equal(corporation, db.Store.FindPilot(1000001)!.CorpId);
        using WorldDbContext saved = new(db.Path);
        Assert.Equal(playerCorporation ? 1 : 0, saved.Set<SavedCorporation>().Count());
        Assert.Empty(saved.Set<SavedCorporationApplication>());
        Assert.Empty(saved.Set<SavedCorporationChange>().Where(c => c.ApplicationJson != null));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void VotingCapabilityRequiresSelectionAndValidArgumentsAndDoesNotEnableVoteOperations(bool playerCorporation)
    {
        using TestWorld db = new();
        if (playerCorporation)
        {
            PlayerCorporationTests.Prepare(db);
            db.Services.GetRequiredService<IPlayerCorporations>().Create(90000001, PlayerCorporationTests.Details());
        }
        using var scope = db.Services.CreateScope();
        var pilot = db.Store.FindPilot(1000001)!;
        using PilotCallContext context = new(
            1000001,
            pilot.Account,
            [pilot],
            scope.ServiceProvider.GetRequiredService<FlightSessionFactory>().Create,
            42,
            SignedSettings.Empty
        );
        var router = scope.ServiceProvider.GetRequiredService<PilotCallRouter>();
        PilotCallResult Call(string method, object?[] args, Dictionary<string, object?>? keywords = null) =>
            router.Call(context, "voteManager", method, BlueMarshal.Decode(BlueMarshal.Encode(args)), _ => { }, keywords);
        Assert.Same(PilotCallResult.Unsupported, Call("CanViewVotes", [(long)pilot.CorpId]));
        context.Select(pilot);
        foreach (object?[] args in new object?[][] { [], [null], ["1000044"], [0L], [-1L], [long.MaxValue], [(long)pilot.CorpId, true] })
        {
            Assert.IsType<PilotCallResult.Failure>(Call("CanViewVotes", args));
        }
        Assert.IsType<PilotCallResult.Failure>(Call("CanViewVotes", [(long)pilot.CorpId], new() { ["characterID"] = pilot.PilotId }));
        Assert.Same(PilotCallResult.Unsupported, Call("InsertVoteCase", []));
        Assert.Same(PilotCallResult.Unsupported, Call("GetVoteCasesByCorporation", [(long)pilot.CorpId]));
        Assert.False(
            Assert.IsType<bool>(
                Assert
                    .IsType<PilotCallResult.Success>(Call("CanViewVotes", [(long)pilot.CorpId], new() { ["machoVersion"] = 1L }))
                    .Reply.ToBlue()
            )
        );
        Assert.Equal(pilot, context.Pilot);
        Assert.Empty(context.Notifications);
    }

    private static object? Reply(BlueInstance packet) =>
        BlueMarshal.Decode(BlueRead.Bytes(BlueRead.Tuple(BlueRead.Tuple(packet.State, 14)[4], 1)[0]));
}
