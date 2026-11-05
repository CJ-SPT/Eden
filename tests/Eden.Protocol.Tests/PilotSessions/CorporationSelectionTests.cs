using Eden.Gameplay.Corporations;
using Eden.Gameplay.Market;
using Eden.Protocol.PilotSessions;
using Eden.Protocol.Serialization;
using Eden.Protocol.Tests.Fixtures;
using Eden.Server;
using Eden.Server.Catalog;
using Eden.Server.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Eden.Protocol.Tests.PilotSessions;

public sealed class CorporationSelectionTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task LoginPrimesCompleteSavedLogoRowsBeforeSelectionAndAfterLogoff(bool customLogo, bool reopen)
    {
        using TestWorld world = new();
        PlayerCorporationTests.Prepare(world);
        using (CatalogDbContext catalog = new(new WorldDatabase(world.Path).CatalogPath, writable: true))
        {
            catalog.Database.ExecuteSqlRaw(
                """
                INSERT INTO SourceRecord(Dataset,Key,Data) VALUES ('client/npccorporations','1000044',
                    '{{"stationID":60003760,"solarSystemID":30000142,"tickerName":"SAK","ceoID":3004181}}');
                """
            );
        }
        CorporationLogo logo = customLogo ? new(415, null, 417, 671, null, 673, 0) : new(null, null, null, null, null, null, null);
        var details = PlayerCorporationTests.Details() with { Logo = logo };
        var created = world.Services.GetRequiredService<IPlayerCorporations>().Create(90000001, details);
        ServiceProvider? restarted = null;
        try
        {
            if (reopen)
            {
                world.Services.Dispose();
                WorldInitialization.Initialize(new(world.Path));
                restarted = ServerServices.Build(new(world.Path));
            }
            var services = restarted ?? world.Services;
            foreach (long account in new long[] { 1000001, 1000002 })
            {
                bool owner = account == 1000001;
                long pilot = owner ? 90000001 : 90000002;
                var packets = await Exchange(services, account, pilot, created.Corporation.Id);
                Assert.DoesNotContain(packets, p => p.Name.EndsWith(".ErrorResponse", StringComparison.Ordinal));
                var replies = packets.Where(p => p.Name.EndsWith(".CallRsp", StringComparison.Ordinal)).Select(Result).ToArray();
                Assert.Equal(8, replies.Length);
                foreach (int index in new[] { 0, 6 })
                {
                    var roster = BlueRead.Tuple(replies[index], 4);
                    var card = BlueRead.Fields(Assert.IsType<BlueInstance>(Assert.Single(Assert.IsType<List<object?>>(roster[2]))).State);
                    Assert.Equal(pilot, card["characterID"]);
                    Assert.Equal(owner ? (long)created.Corporation.Id : 1000044L, card["corporationID"]);
                    Assert.Equal(owner ? 8400200d : 0d, card["balance"]);
                    Assert.Equal(owner ? 8250L : 8000L, card["skillPoints"]);
                    Assert.Null(card["skillTypeID"]);
                }
                var owners = BlueRead.Tuple(replies[1], 2);
                var corporationOwner = Assert.Single(
                    Assert.IsType<List<object?>>(owners[1]).Select(o => BlueRead.Tuple(o, 5)),
                    o => Equals(o[0], (long)created.Corporation.Id)
                );
                Assert.Equal(details.Name, BlueMarshal.Text(corporationOwner[1]));
                Assert.Equal((long)CorporationRules.CorporationType, corporationOwner[2]);
                foreach (int index in new[] { 2, 4, 7 })
                {
                    var table = BlueRead.Tuple(replies[index], 2);
                    var header = BlueRead.Tuple(table[0], 8).Select(BlueMarshal.Text).ToArray();
                    Assert.Equal(
                        new[] { "corporationID", "tickerName", "shape1", "shape2", "shape3", "color1", "color2", "color3" },
                        header
                    );
                    var rows = Assert.IsType<List<object?>>(table[1]).Select(r => BlueRead.Tuple(r, header.Length)).ToArray();
                    Assert.Equal(new long[] { 1000044, created.Corporation.Id }, rows.Select(r => Assert.IsType<long>(r[0])));
                    Assert.Equal("SAK", BlueMarshal.Text(rows[0][1]));
                    var saved = rows[1];
                    Assert.Equal(details.Ticker, BlueMarshal.Text(saved[1]));
                    Assert.Equal(
                        new object?[] { logo.Shape1, logo.Shape2, logo.Shape3, logo.Color1, logo.Color2, logo.Color3 }.Select(v =>
                            v is int number ? (object)(long)number : null
                        ),
                        saved.Skip(2)
                    );
                    Assert.All(rows[0].Skip(2), Assert.Null);
                }
            }
            Assert.Equal(created.Corporation, services.GetRequiredService<IPlayerCorporations>().Read(created.Corporation.Id));
            var wallet = services.GetRequiredService<IPilotWalletReader>();
            Assert.Equal(created.BalanceCents, wallet.Balance(90000001));
            Assert.Single(wallet.Journal(90000001).CorporationPayments!);
        }
        finally
        {
            restarted?.Dispose();
        }
    }

    private static object? Result(BlueInstance packet) =>
        BlueMarshal.Decode(BlueRead.Bytes(BlueRead.Tuple(BlueRead.Tuple(packet.State, 14)[4], 1)[0]));

    private static async Task<List<BlueInstance>> Exchange(ServiceProvider services, long account, long pilot, int corporation)
    {
        using var scope = services.CreateScope();
        var ids = new List<object?> { (long)corporation, 1000044L, (long)corporation, -1L, long.MaxValue, 999999L };
        Queue<object?> input = new([
            PilotServicesTests.Request("charUnboundMgr", "GetCharacterSelectionData", []),
            PilotServicesTests.Request("config", "GetMultiOwnersEx", [new List<object?> { pilot, (long)corporation }]),
            PilotServicesTests.Request("config", "GetMultiCorpTickerNamesEx", [ids]),
            PilotServicesTests.Request("charUnboundMgr", "SelectCharacterID", [pilot]),
            PilotServicesTests.Request("config", "GetMultiCorpTickerNamesEx", [ids]),
            PilotServicesTests.Request("userSvc", "UserLogOffCharacter", []),
            PilotServicesTests.Request("charUnboundMgr", "GetCharacterSelectionData", []),
            PilotServicesTests.Request("config", "GetMultiCorpTickerNamesEx", [ids]),
        ]);
        List<BlueInstance> output = [];
        await Assert.ThrowsAsync<EndOfStreamException>(() =>
            scope
                .ServiceProvider.GetRequiredService<IPilotSession>()
                .RunAsync(
                    () => input.TryDequeue(out var packet) ? Task.FromResult(packet) : throw new EndOfStreamException(),
                    packet =>
                    {
                        output.Add(Assert.IsType<BlueInstance>(packet));
                        return Task.CompletedTask;
                    },
                    _ => { },
                    account,
                    42,
                    SignedSettings.Empty,
                    CancellationToken.None
                )
        );
        return output;
    }
}
