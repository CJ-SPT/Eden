using System.Text.Json;
using Eden.Data.World.Corporations;
using Eden.Data.World.Market;
using Eden.Gameplay.Characters;
using Eden.Gameplay.Corporations;
using Eden.Gameplay.Market;
using Eden.Protocol.Characters;
using Eden.Protocol.PilotSessions;
using Eden.Protocol.Serialization;
using Eden.Protocol.Tests.Persistence;
using Eden.Server;
using Eden.World.Runtime;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Eden.Protocol.Tests.PilotSessions;

public sealed class RemoteHomeStationTests
{
    private const long Pilot = HomeStationTestData.PilotId;
    private const int Origin = HomeStationTestData.Origin;
    private const int School = HomeStationTestData.School;
    private const int FirstOffice = HomeStationTestData.Other;
    private const int SecondOffice = 60004422;
    private const int UnleasedStation = 60004423;
    private const long Budget = 50_000_000;

    [Fact]
    public async Task NativeOfficeSelectionChargesOnceAndUsesAnExactSavedCooldown()
    {
        TestClock clock = new(new DateTimeOffset(2024, 2, 29, 8, 9, 10, TimeSpan.Zero).AddTicks(1_234_560));
        DateTimeOffset deadline = clock.UtcNow.AddDays(365);
        using TestWorld db = new(clock: clock, liveWorld: true);
        HomeStationTestData.Prepare(db, false, SecondOffice, UnleasedStation);
        var live = db.Services.GetRequiredService<LiveWorld>();
        long officeId = SeedMemberAndOffices(live, clock.Now);
        var homes = db.Services.GetRequiredService<IPilotHomeStation>();
        var wallet = db.Services.GetRequiredService<IPilotWalletReader>();

        using (CorporationTestClient client = new(db, 1000001))
        {
            PilotCallResult Call(string method, params object?[] args) => client.CallService("home_station", method, args);

            IReadOnlyList<Dictionary<string, object?>> Candidates() =>
                Assert
                    .IsType<List<object?>>(BlueMarshal.Decode(BlueMarshal.Encode(Reply(Call("get_home_station_candidates")))))
                    .Select(row => BlueRead.Fields(Assert.IsType<BlueInstance>(row).State))
                    .ToArray();

            Dictionary<string, object?> Candidate(int station) => Assert.Single(Candidates(), row => Equals(row["id"], (long)station));

            Assert.Same(PilotCallResult.None, Call("get_next_remote_change_time"));
            var office = Candidate(FirstOffice);
            Assert.Equal(false, office["is_current_station"]);
            Assert.Equal(false, office["is_school_hq"]);
            Assert.Empty(Assert.IsType<List<object?>>(office["errors"]));
            Assert.DoesNotContain(Candidates(), row => Equals(row["id"], (long)UnleasedStation));
            Assert.IsType<PilotCallResult.Failure>(Call("set_home_station", (long)UnleasedStation, true));
            var confirmation = NativeException(Call("set_home_station", (long)FirstOffice, false));
            Assert.Equal("homestation.error.RemoteChangeNotExpectedError", confirmation.Name);
            Assert.Empty(confirmation.Arguments);
            Assert.Equal(Budget, wallet.Balance(Pilot));
            Assert.Empty(client.Context.Notifications);

            Assert.Same(PilotCallResult.None, Call("set_home_station", (long)FirstOffice, true));
            Assert.Equal(FirstOffice, Assert.Single(client.Context.Notifications.OfType<HomeStationChanged>()).Id);
            Assert.Equal(FirstOffice, homes.Read(Pilot).Id);
            Assert.Equal(Origin, client.Context.Pilot.StationId);
            Assert.Equal(deadline.UtcDateTime.ToFileTimeUtc(), homes.NextRemoteChange(Pilot));
            var datetime = Assert.IsType<BlueConstructor>(Reply(Call("get_next_remote_change_time")).ToBlue());
            Assert.Equal("datetime.datetime", datetime.Name);
            Assert.Equal(new byte[] { 7, 233, 2, 28, 8, 9, 10, 1, 226, 64 }, Assert.IsType<byte[]>(Assert.Single(datetime.Arguments)));
            Assert.NotEmpty(BlueMarshal.Encode(datetime));

            Assert.Equal(3L, Assert.Single(Assert.IsType<List<object?>>(Candidate(SecondOffice)["errors"])));
            var blocked = NativeException(Call("set_home_station", (long)SecondOffice, true));
            Assert.Equal("homestation.validation.ChangeHomeStationValidationError", blocked.Name);
            Assert.Equal(3L, Assert.Single(Assert.IsType<List<object?>>(Assert.Single(blocked.Arguments))));
            Assert.Equal(Budget - HomeStationRules.RelocationFeeCents, wallet.Balance(Pilot));

            using (var action = live.Economy.Begin(TransactionParticipants.None))
            {
                action.Remove(action.Set<SavedCorporationOffice>().Find(officeId)!);
                action.Commit();
            }
            Assert.DoesNotContain(Candidates(), row => Equals(row["id"], (long)FirstOffice));
            Assert.Equal(FirstOffice, homes.Read(Pilot).Id);
            Assert.Same(PilotCallResult.None, Call("set_home_station", (long)School, false));
            Assert.Same(PilotCallResult.None, Call("set_home_station", (long)Origin, false));
            Assert.Equal(deadline.UtcDateTime.ToFileTimeUtc(), homes.NextRemoteChange(Pilot));
        }

        await live.FlushAsync();
        db.Services.Dispose();
        using var reopened = ServerServices.Build(new(db.Path), clock, liveWorld: true);
        homes = reopened.GetRequiredService<IPilotHomeStation>();
        wallet = reopened.GetRequiredService<IPilotWalletReader>();
        Assert.Equal(Origin, homes.Read(Pilot).Id);
        Assert.Equal(Budget - 3 * HomeStationRules.RelocationFeeCents, wallet.Balance(Pilot));
        Assert.Equal(new[] { FirstOffice, School, Origin }, wallet.Journal(Pilot).HomeStationPayments.Select(payment => payment.StationId));
        clock.UtcNow = deadline.AddTicks(-1);
        Assert.Equal(deadline.UtcDateTime.ToFileTimeUtc(), homes.NextRemoteChange(Pilot));
        clock.UtcNow = deadline;
        Assert.Null(homes.NextRemoteChange(Pilot));
        Assert.Empty(Assert.Single(homes.Candidates(Pilot), option => option.Station.Id == SecondOffice).Errors);
        Assert.Equal(SecondOffice, homes.Set(Pilot, SecondOffice, true).Station.Id);
        Assert.Equal(deadline.AddDays(365).UtcDateTime.ToFileTimeUtc(), homes.NextRemoteChange(Pilot));
    }

    [Fact]
    public void Version234HomeStationMigratesWithNoRemoteCooldown()
    {
        using TestWorld db = new();
        using (WorldDbContext old = new(db.Path))
        {
            old.Database.ExecuteSqlRaw("ALTER TABLE WorldHomeStation DROP COLUMN NextRemoteChange; PRAGMA user_version=234");
        }
        WorldInitialization.Open(db.Path);
        using var reopened = ServerServices.Build(new(db.Path), liveWorld: true);
        var homes = reopened.GetRequiredService<IPilotHomeStation>();
        Assert.Equal(Origin, homes.Read(Pilot).Id);
        Assert.Null(homes.NextRemoteChange(Pilot));
    }

    private static BlueModel Reply(PilotCallResult result) => Assert.IsType<PilotCallResult.Success>(result).Reply;

    private static BlueConstructor NativeException(PilotCallResult result) =>
        Assert.IsType<BlueConstructor>(Assert.IsType<PilotCallResult.NativeFailure>(result).Exception.ToBlue());

    private static long SeedMemberAndOffices(LiveWorld live, long now)
    {
        const int corporation = CorporationRules.FirstCorporationId;
        const long ceo = 90000002;
        CorporationDetails details = new(
            "Office Members",
            "OFFIC",
            "",
            "",
            0,
            0,
            new(null, null, null, null, null, null, null),
            true,
            true
        );
        using var action = live.Economy.Begin(new(pilots: [Pilot, ceo]));
        action.Add(
            new SavedCorporation
            {
                Id = corporation,
                CeoId = ceo,
                CreatorId = ceo,
                Name = details.Name,
                Ticker = details.Ticker,
                DetailsJson = JsonSerializer.Serialize(details),
                StationId = Origin,
                SystemId = 30000142,
                MemberLimit = 20,
            }
        );
        action.Pilots.Find(Pilot)!.CorporationId = corporation;
        action.Pilots.Find(ceo)!.CorporationId = corporation;
        action.Add(new PilotWallet { PilotId = Pilot, Cents = Budget });
        for (int index = 0; index < 2; index++)
        {
            action.Add(
                new SavedCorporationOffice
                {
                    Id = CorporationStorageRules.FirstOfficeId + index,
                    CorporationId = corporation,
                    StationId = index == 0 ? FirstOffice : SecondOffice,
                    SystemId = HomeStationTestData.System,
                    RentedAt = now,
                }
            );
        }
        action.Commit();
        return CorporationStorageRules.FirstOfficeId;
    }
}
