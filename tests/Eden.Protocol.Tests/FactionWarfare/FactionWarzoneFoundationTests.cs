using Eden.Data.World.FactionWarfare;
using Eden.Gameplay.FactionWarfare;
using Eden.Infrastructure.Diagnostics;
using Eden.Protocol.Tests.PilotSessions;
using Eden.Server;
using Eden.World.FactionWarfare;
using Eden.World.Runtime;
using Eden.World.Storage;
using Microsoft.Extensions.DependencyInjection;

namespace Eden.Protocol.Tests.FactionWarfare;

public sealed class FactionWarzoneFoundationTests
{
    private const int CapturedSystem = 30002087;
    private const int NeighborSystem = 30002084;

    [Fact]
    public void RosterIncludesOnlyContestedSystemsAndReciprocalWarzoneGates()
    {
        var roster = FactionWarzoneCatalog.Roster;
        var index = FactionWarzoneCatalog.Systems;
        Assert.Equal(160, roster.Length);
        Assert.Equal(70, roster.Count(seed => FactionWarzoneCatalog.Warzone(seed.OriginalOccupier).Id == WarzoneId.AmarrMinmatar));
        Assert.Equal(90, roster.Count(seed => FactionWarzoneCatalog.Warzone(seed.OriginalOccupier).Id == WarzoneId.CaldariGallente));
        Assert.Equal(440, roster.Sum(seed => seed.Neighbors.Length));
        Assert.All(FactionWarzoneCatalog.Headquarters, hq => Assert.False(index.ContainsKey(hq.SystemId)));
        Assert.All(
            roster,
            seed =>
            {
                Assert.Equal(seed.Neighbors.Length, seed.Neighbors.Distinct().Count());
                Assert.All(
                    seed.Neighbors,
                    neighborId =>
                    {
                        var neighbor = index[neighborId];
                        Assert.NotEqual(seed.SystemId, neighborId);
                        Assert.Contains(seed.SystemId, neighbor.Neighbors);
                        Assert.Equal(
                            FactionWarzoneCatalog.Warzone(seed.OriginalOccupier),
                            FactionWarzoneCatalog.Warzone(neighbor.OriginalOccupier)
                        );
                    }
                );
            }
        );
    }

    [Fact]
    public async Task OccupationRollbackAndCaptureKeepWarzoneReadsCoherentAcrossReopen()
    {
        TestClock clock = new();
        using TestWorld fixture = new(clock: clock, liveWorld: true);
        var live = fixture.Services.GetRequiredService<LiveWorld>();
        var warzones = fixture.Services.GetRequiredService<IFactionWarzones>();
        var initial = warzones.Systems();
        Assert.Equal(15, initial.Count(system => system.Adjacency == WarzoneAdjacency.Frontline));
        Assert.Equal(34, initial.Count(system => system.Adjacency == WarzoneAdjacency.CommandOperations));
        Assert.All(initial, system => Assert.Equal(0, system.VictoryPoints));
        Assert.Equal(MilitiaFaction.Minmatar, warzones.System(CapturedSystem)!.Occupier);
        Assert.Equal(WarzoneAdjacency.CommandOperations, warzones.System(NeighborSystem)!.Adjacency);
        Assert.Null(warzones.System(30003788));

        using (var action = live.Economy.Begin(TransactionParticipants.None))
        {
            action.Add(Capture());
        }
        Assert.Equal(MilitiaFaction.Minmatar, warzones.System(CapturedSystem)!.Occupier);

        using (var action = live.Economy.Begin(TransactionParticipants.None))
        {
            action.Add(Capture());
            action.Commit();
        }
        var occupied = warzones.System(CapturedSystem)!;
        Assert.Equal(MilitiaFaction.Amarr, occupied.Occupier);
        Assert.Equal(clock.Now, occupied.CapturedAt);
        Assert.Equal(WarzoneAdjacency.Frontline, warzones.System(NeighborSystem)!.Adjacency);
        Assert.Equal(MilitiaFaction.Minmatar, initial.Single(system => system.SystemId == CapturedSystem).Occupier);

        using (var snapshot = Assert.IsType<WorldSnapshot>(live.CaptureSnapshot()))
        {
            using (var action = live.Economy.Begin(TransactionParticipants.None))
            {
                action.Set<SavedWarzoneOccupation>().Find(CapturedSystem)!.VictoryPoints = FactionWarzoneRules.VictoryPointThreshold;
                action.Commit();
            }
            snapshot.Project();
            using WorldDbContext projected = new(snapshot.Database);
            Assert.Equal(0, projected.Set<SavedWarzoneOccupation>().Find(CapturedSystem)!.VictoryPoints);
        }
        var updated = occupied with { VictoryPoints = FactionWarzoneRules.VictoryPointThreshold };
        using (var metrics = SessionPerformance.Begin("resident faction warfare reads", _ => { }, force: true)!)
        {
            for (int read = 0; read < 5; read++)
            {
                Assert.Equal(updated, warzones.System(CapturedSystem));
                Assert.Equal(updated, warzones.Systems().Single(system => system.SystemId == CapturedSystem));
                Assert.Null(warzones.System(30000142));
            }
            Assert.Equal(0, metrics.Commands);
            Assert.Equal(0, metrics.Connections);
            Assert.Equal(0, metrics.FittingCalculations);
        }

        await live.FlushAsync();
        fixture.Services.Dispose();
        using var reopened = ServerServices.Build(new(fixture.Path), clock, liveWorld: true);
        var restored = reopened.GetRequiredService<IFactionWarzones>();
        Assert.Equal(updated, restored.System(CapturedSystem));
        Assert.Equal(WarzoneAdjacency.Frontline, restored.System(NeighborSystem)!.Adjacency);

        SavedWarzoneOccupation Capture() =>
            new()
            {
                SystemId = CapturedSystem,
                Occupier = MilitiaFaction.Amarr,
                CapturedAt = clock.Now,
            };
    }
}
