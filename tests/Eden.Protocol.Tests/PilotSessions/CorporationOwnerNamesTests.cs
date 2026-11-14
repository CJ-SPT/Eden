using System.Diagnostics;
using Eden.Data.World.Characters;
using Eden.Gameplay.Corporations;
using Eden.Infrastructure.Diagnostics;
using Eden.Protocol.Docking;
using Eden.Protocol.PilotSessions;
using Eden.Protocol.Serialization;
using Eden.Protocol.Tests.Persistence;
using Eden.Server;
using Eden.World.Runtime;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit.Abstractions;

namespace Eden.Protocol.Tests.PilotSessions;

public sealed class CorporationOwnerNamesTests(ITestOutputHelper output)
{
    private const long Viewer = 90000001;
    private const long Member = 90000002;

    [Fact]
    public void OwnerReplyProjectsNamesWithoutMaterializingMembersOrReadingSql()
    {
        using TestWorld db = new(liveWorld: true);
        CalendarTestData.AddPilots(db, 128);
        var corporations = db.Services.GetRequiredService<IPlayerCorporations>();
        using CorporationTestClient client = new(db, 1000001);
        var expected = corporations.Members(Viewer).Select(m => new CorporationMemberName(m.PilotId, m.Name)).ToArray();
        var live = db.Services.GetRequiredService<LiveWorld>();
        long started = Stopwatch.GetTimestamp();
        for (int i = 0; i < 5; i++)
        {
            corporations.Members(Viewer);
        }
        output.WriteLine($"Five full member reads: {Stopwatch.GetElapsedTime(started).TotalMilliseconds:F2} ms.");
        started = Stopwatch.GetTimestamp();
        using (var metrics = SessionPerformance.Begin("corporation owner names", _ => { }, force: true)!)
        using (var transaction = live.Economy.Begin(TransactionParticipants.None, inspect: true))
        {
            for (int i = 0; i < 5; i++)
            {
                Assert.Equal(expected, corporations.MemberNames(Viewer));
            }
            Assert.Equal(0, transaction.Materializations);
            Assert.Equal(0, metrics.Commands);
            Assert.Equal(0, metrics.Connections);
            Assert.Equal(0, metrics.FittingCalculations);
        }
        output.WriteLine($"Five narrow reads and assertions: {Stopwatch.GetElapsedTime(started).TotalMilliseconds:F2} ms.");
        var expectedWire = new CompatibilityRowset(
            ["ownerID", "ownerName", "typeID"],
            expected.Select(p => new EveOwnerRow(p.PilotId, p.Name)).ToArray()
        );
        var actual = Assert.IsType<PilotCallResult.Success>(client.Call("GetEveOwners", [])).Reply;
        Assert.Equal(BlueMarshal.Encode(expectedWire), BlueMarshal.Encode(actual));
        Assert.IsType<PilotCallResult.Failure>(client.Call("GetEveOwners", [Viewer]));
        Assert.Throws<InvalidDataException>(() => corporations.MemberNames(long.MaxValue));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void NamesFollowMembershipAndRenamesWithoutPublishingRollbackAndSurviveReopen(bool resident)
    {
        using TestWorld db = new(liveWorld: resident);
        var corporations = db.Services.GetRequiredService<IPlayerCorporations>();
        var initial = corporations.MemberNames(Viewer).ToArray();
        Assert.Contains(initial, p => p.PilotId == Member);
        CorporationAdministrationTests.CreateAndJoin(db);
        Assert.Equal(new[] { Viewer, Member }, corporations.MemberNames(Viewer).Select(p => p.PilotId));
        var membership = db.Services.GetRequiredService<ICorporationMembership>();
        membership.Remove(Member, Member);
        Assert.Equal(Viewer, Assert.Single(corporations.MemberNames(Viewer)).PilotId);
        Assert.DoesNotContain(corporations.MemberNames(Member), p => p.PilotId == Viewer);
        if (resident)
        {
            var live = db.Services.GetRequiredService<LiveWorld>();
            using (var transaction = live.Economy.Begin(TransactionParticipants.None))
            {
                transaction.Pilots.Find(Viewer)!.Name = "Uncommitted name";
            }
            Assert.NotEqual("Uncommitted name", Assert.Single(corporations.MemberNames(Viewer)).Name);
        }
        using (WorldDbContext world = new(db.Path))
        {
            world.Pilots.Find(Viewer)!.Name = "Committed 探索";
            world.SaveChanges();
        }
        Assert.Equal("Committed 探索", Assert.Single(corporations.MemberNames(Viewer)).Name);
        Assert.DoesNotContain(initial, p => p.Name == "Committed 探索");
        db.Services.Dispose();
        WorldInitialization.Open(db.Path);
        using var reopened = ServerServices.Build(new(db.Path));
        Assert.Equal(new(Viewer, "Committed 探索"), Assert.Single(reopened.GetRequiredService<IPlayerCorporations>().MemberNames(Viewer)));
    }
}
