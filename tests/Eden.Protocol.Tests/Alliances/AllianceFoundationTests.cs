using Eden.Data.World.Alliances;
using Eden.Gameplay.Alliances;
using Eden.Infrastructure.Diagnostics;
using Eden.Protocol.Tests.PilotSessions;
using Eden.Server;
using Eden.World.Runtime;
using Eden.World.Storage;
using Microsoft.Extensions.DependencyInjection;

namespace Eden.Protocol.Tests.Alliances;

public sealed class AllianceFoundationTests
{
    [Fact]
    public async Task ResidentMembershipIsAtomicAndSurvivesCheckpointReopen()
    {
        using TestWorld world = new(liveWorld: true);
        int corporation = CorporationAdministrationTests.CreateAndJoin(world);
        var resident = world.Services.GetRequiredService<LiveWorld>();
        var alliances = world.Services.GetRequiredService<IAlliances>();
        using (var action = resident.Economy.Begin(TransactionParticipants.None))
        {
            action.Add(
                new SavedAlliance
                {
                    Id = AllianceRules.FirstAllianceId,
                    Name = "Foundation",
                    Ticker = "FND",
                }
            );
            action.Add(new SavedAllianceMember { CorporationId = corporation, AllianceId = AllianceRules.FirstAllianceId });
            action.Commit();
        }
        using (var action = resident.Economy.Begin(TransactionParticipants.None))
        {
            action.Remove(action.Set<SavedAllianceMember>().Find(corporation)!);
            action.Set<SavedAlliance>().Find(AllianceRules.FirstAllianceId)!.Name = "Rolled back";
        }
        Assert.Equal(AllianceRules.FirstAllianceId, alliances.Affiliation(corporation));
        var snapshot = alliances.Read(AllianceRules.FirstAllianceId)!;
        Assert.Equal(("Foundation", 1, 2), (snapshot.Details.Name, snapshot.CorporationCount, snapshot.MemberCount));
        using (var metrics = SessionPerformance.Begin("resident alliance affiliation", _ => { }, force: true)!)
        {
            for (int cycle = 0; cycle < 10; cycle++)
            {
                Assert.Equal(AllianceRules.FirstAllianceId, alliances.Affiliation(corporation));
                Assert.Null(alliances.Affiliation(1000044));
            }
            Assert.Equal(0, metrics.Commands);
            Assert.Equal(0, metrics.Connections);
            Assert.Equal(0, metrics.FittingCalculations);
        }
        await resident.FlushAsync();
        world.Services.Dispose();
        using var reopened = ServerServices.Build(new(world.Path), liveWorld: true);
        Assert.Equal(snapshot, reopened.GetRequiredService<IAlliances>().Read(snapshot.Id));
        Assert.Equal(snapshot.Id, Assert.Single(reopened.GetRequiredService<IAlliances>().Members(snapshot.Id)).AllianceId);
    }

    [Theory]
    [InlineData(" Bad\nName ", "GOOD")]
    [InlineData("Alliance", "TOOLONG")]
    [InlineData("Alliance", " ")]
    [InlineData("abc", "ABC")]
    public void InvalidIdentityRejects(string name, string ticker) =>
        Assert.Throws<InvalidDataException>(() => AllianceRules.Validate(new(name, ticker, "", "")));
}
