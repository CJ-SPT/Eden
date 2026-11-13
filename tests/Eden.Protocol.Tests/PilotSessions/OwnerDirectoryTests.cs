using System.Text.Json;
using Eden.Data.World.Characters;
using Eden.Data.World.Corporations;
using Eden.Gameplay.Characters;
using Eden.Gameplay.Corporations;
using Eden.Infrastructure.Diagnostics;
using Eden.Protocol.PilotSessions;
using Eden.Protocol.Serialization;
using Eden.Protocol.Tests.Persistence;
using Eden.World.Runtime;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Eden.Protocol.Tests.PilotSessions;

public sealed class OwnerDirectoryTests
{
    [Theory]
    [InlineData(false, 2)]
    [InlineData(true, 0)]
    public void MixedOwnerBatchesUseConstantSqlAndPreserveNativeRowsAfterPilotChanges(bool liveWorld, int commands)
    {
        using TestWorld db = new(liveWorld: liveWorld);
        UniversalSearchTests.Prepare(db);
        PlayerCorporationTests.Prepare(db);
        int corporation = db
            .Services.GetRequiredService<IPlayerCorporations>()
            .Create(90000001, PlayerCorporationTests.Details())
            .Corporation.Id;
        var pilots = CalendarTestData.AddPilots(db, 100);
        object?[] arguments = [pilots.Concat([corporation, 1000035L, 500001L, long.MaxValue, pilots[0]]).Cast<object?>().ToList()];
        using CourierServiceTests.Client client = new(db, CalendarTestData.Clock(), 1000001);
        var initial = Read();
        Assert.Equal(pilots.Count + 3, initial.Length);
        Assert.Contains(initial, row => Equals(row[0], (long)corporation) && BlueMarshal.Text(row[1]) == "Eden Explorers");
        Assert.Contains(initial, row => Equals(row[0], 1000035L) && Equals(row[2], 2L));
        Assert.Contains(initial, row => Equals(row[0], 500001L) && Equals(row[2], 30L));
        Measure();
        using (WorldDbContext world = new(db.Path))
        {
            world.Database.ExecuteSqlRaw("UPDATE WorldPilot SET Name='Unrelated renamed pilot' WHERE Id=90000002");
        }
        Measure();

        object?[][] Read()
        {
            var reply = Assert.IsType<PilotCallResult.Success>(client.Call("GetMultiOwnersEx", arguments, "config")).Reply;
            var table = BlueRead.Tuple(BlueMarshal.Decode(BlueMarshal.Encode(reply)), 2);
            return Assert.IsType<List<object?>>(table[1]).Select(row => BlueRead.Tuple(row, 5)).ToArray();
        }

        void Measure()
        {
            using var metrics = SessionPerformance.Begin("owner batch", _ => { }, force: true)!;
            var rows = Read();
            Assert.Equal(initial.Length, rows.Length);
            for (int row = 0; row < rows.Length; row++)
            {
                Assert.Equal(initial[row], rows[row]);
            }
            Assert.Equal(commands, metrics.Commands);
            Assert.Equal(commands, metrics.Connections);
            Assert.Equal(0, metrics.FittingCalculations);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PublicNamesFollowCommitsButNeverExposeRolledBackNames(bool hydrateEconomy)
    {
        using TestWorld db = new(liveWorld: true);
        var live = db.Services.GetRequiredService<LiveWorld>();
        var directory = db.Services.GetRequiredService<ICharacterDirectory>();
        var local = db.Services.GetRequiredService<ILocalCharacterSearch>();
        var universal = db.Services.GetRequiredService<IUniversalSearch>();
        const long PilotId = 90000001;
        string original = Assert.Single(directory.Read([PilotId])).Name;
        int changedGender = Assert.Single(directory.Read([PilotId])).Gender == 0 ? 1 : 0;
        Assert.Contains(PilotId, local.Search(original, CharacterSearchMatch.ExactName));
        var originalIndex = live.OwnerDirectory.Snapshot.CharacterSearch.Value;
        if (hydrateEconomy)
        {
            using var action = live.Economy.Begin(TransactionParticipants.None);
        }
        using (WorldDbContext world = new(db.Path) { Participants = TransactionParticipants.None })
        {
            using var transaction = world.Database.BeginTransaction();
            world.Pilots.Find(PilotId)!.Name = "Uncommitted name";
            world.SaveChanges();
            Assert.Equal(original, Assert.Single(directory.Read([PilotId])).Name);
            Assert.Empty(local.Search("Uncommitted", CharacterSearchMatch.PartialTerms));
            transaction.Rollback();
        }
        Assert.Same(originalIndex, live.OwnerDirectory.Snapshot.CharacterSearch.Value);
        using (WorldDbContext world = new(db.Path) { Participants = TransactionParticipants.None })
        {
            world.Pilots.Find(PilotId)!.GenderId = changedGender;
            world.SaveChanges();
        }
        Assert.Equal(changedGender, Assert.Single(directory.Read([PilotId])).Gender);
        Assert.Same(originalIndex, live.OwnerDirectory.Snapshot.CharacterSearch.Value);
        using (WorldDbContext world = new(db.Path) { Participants = TransactionParticipants.None })
        {
            world.Pilots.Find(PilotId)!.Name = "Committed SQL name";
            world.SaveChanges();
        }
        Check("Committed SQL name");
        using (var action = live.Economy.Begin(TransactionParticipants.None))
        {
            action.Set<Pilot>().Find(PilotId)!.Name = "Rolled back resident name";
        }
        Check("Committed SQL name");
        using (var action = live.Economy.Begin(TransactionParticipants.None))
        {
            action.Set<Pilot>().Find(PilotId)!.Name = "Committed resident name";
            action.Commit();
        }
        Check("Committed resident name");
        Assert.Contains(PilotId, originalIndex.Select(new(original, CharacterSearchMatch.ExactName)).Select(entry => entry.Id));
        await live.FlushAsync();
        string savedPath = Path.Combine(Path.GetDirectoryName(db.Path)!, "directory-reopened.sqlite");
        File.Copy(db.Path, savedPath);
        using WorldDbContext reopened = new(savedPath);
        Assert.Equal("Committed resident name", reopened.Pilots.Find(PilotId)!.Name);

        void Check(string name)
        {
            SearchRequest request = new(name, [SearchCategory.Character], CharacterSearchMatch.ExactName);
            universal.Search(request);
            local.Search(name, CharacterSearchMatch.ExactName);
            using var metrics = SessionPerformance.Begin("committed names", _ => { }, force: true)!;
            Assert.Equal(name, Assert.Single(directory.Read([PilotId])).Name);
            Assert.Equal(new[] { PilotId }, local.Search(name, CharacterSearchMatch.ExactName));
            Assert.Equal(new[] { PilotId }, Assert.Single(universal.Search(request)).Ids);
            Assert.Empty(local.Search(original, CharacterSearchMatch.ExactName));
            Assert.Equal(0, metrics.Commands);
            Assert.Equal(0, metrics.Connections);
            Assert.Equal(0, metrics.FittingCalculations);
        }
    }

    [Fact]
    public void CorporationNamesAndLogosFollowCreationCommitRollbackAndDeletion()
    {
        using TestWorld db = new(liveWorld: true);
        var live = db.Services.GetRequiredService<LiveWorld>();
        var directory = db.Services.GetRequiredService<IPlayerCorporationDirectory>();
        var search = db.Services.GetRequiredService<IUniversalSearch>();
        const int CorporationId = CorporationRules.FirstCorporationId;
        SearchRequest request = new("Directory", [SearchCategory.Corporation]);
        Assert.Empty(directory.Read([CorporationId]));
        Assert.Empty(Assert.Single(search.Search(request)).Ids);
        var details = PlayerCorporationTests.Details("Directory Corporation", "DIR") with { Logo = new(1, 2, 3, 4, 5, 6, 7) };
        using (WorldDbContext world = new(db.Path))
        {
            world.Add(
                new SavedCorporation
                {
                    Id = CorporationId,
                    CeoId = 90000002,
                    CreatorId = 90000002,
                    Name = details.Name,
                    Ticker = details.Ticker,
                    DetailsJson = JsonSerializer.Serialize(details),
                }
            );
            world.SaveChanges();
        }
        Assert.Equal(
            new CorporationDirectoryEntry(CorporationId, details.Name, details.Ticker, details.Logo),
            Assert.Single(directory.Read([CorporationId, CorporationId]))
        );
        Assert.Equal(new long[] { CorporationId }, Assert.Single(search.Search(request)).Ids);
        var index = live.OwnerDirectory.Snapshot.CorporationSearch.Value;
        using (WorldDbContext world = new(db.Path))
        {
            world.Set<SavedCorporation>().Find(CorporationId)!.BalanceCents++;
            world.SaveChanges();
        }
        Assert.Same(index, live.OwnerDirectory.Snapshot.CorporationSearch.Value);
        var revised = details with { Name = "Renamed Directory", Ticker = "NEW", Logo = details.Logo with { Shape1 = 8 } };
        using (WorldDbContext world = new(db.Path))
        {
            using var transaction = world.Database.BeginTransaction();
            var row = world.Set<SavedCorporation>().Find(CorporationId)!;
            row.Name = revised.Name;
            row.DetailsJson = JsonSerializer.Serialize(revised);
            world.SaveChanges();
            Assert.Equal(details.Name, Assert.Single(directory.Read([CorporationId])).Name);
            transaction.Rollback();
        }
        Assert.Same(index, live.OwnerDirectory.Snapshot.CorporationSearch.Value);
        using (var action = live.Economy.Begin(TransactionParticipants.None))
        {
            var row = action.Set<SavedCorporation>().Find(CorporationId)!;
            row.Name = revised.Name;
            row.Ticker = revised.Ticker;
            row.DetailsJson = JsonSerializer.Serialize(revised);
            action.Commit();
        }
        Assert.Equal(new long[] { CorporationId }, Assert.Single(search.Search(request with { Query = "Renamed" })).Ids);
        using (var metrics = SessionPerformance.Begin("corporation public fields", _ => { }, force: true)!)
        {
            Assert.Equal(
                new CorporationDirectoryEntry(CorporationId, revised.Name, revised.Ticker, revised.Logo),
                Assert.Single(directory.Read([CorporationId]))
            );
            Assert.Equal(0, metrics.Commands);
            Assert.Equal(0, metrics.Connections);
            Assert.Equal(0, metrics.FittingCalculations);
        }
        using (WorldDbContext world = new(db.Path))
        {
            world.Remove(world.Set<SavedCorporation>().Find(CorporationId)!);
            world.SaveChanges();
        }
        Assert.Empty(directory.Read([CorporationId]));
        Assert.Empty(Assert.Single(search.Search(request)).Ids);
    }
}
