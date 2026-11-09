using Eden.Gameplay.Capsuleers;
using Eden.Gameplay.Flight;
using Eden.World.Capsuleers;

namespace Eden.Protocol.Tests.Capsuleers;

public sealed class TravelerRoutineTests
{
    [Fact]
    public void HabitsThreatsAndShelterSurviveCheckpointWithoutChangingLegacyDeadlines()
    {
        var routine = TravelerRoutineRules.Create(90000042);
        Assert.Equal(routine, TravelerRoutineRules.Create(90000042));
        Assert.Equal(3, Enumerable.Range(1, 100).Select(id => TravelerRoutineRules.Create(id).Pace).Distinct().Count());
        for (int system = 1; system <= 6; system++)
        {
            routine = TravelerRoutineRules.Threatened(routine, system, system * TimeSpan.TicksPerSecond);
            routine = TravelerRoutineRules.Arrive(routine, system);
        }
        Assert.Equal([6, 5, 4, 3], routine.Threats.Select(threat => threat.SystemId));
        Assert.Equal([6, 5, 4], routine.RecentStations);
        Assert.True(routine.ShelterPending);
        Assert.InRange(TravelerRoutineRules.ShelterSeconds(routine, 3), 300, 900);
        var work = new HaulerProgress(HaulerActivity.Docked, 1, 18, 42, 6, 0, new NavigationState().Capture(), DueTicks: 180000000)
        {
            Routine = routine,
        };
        string json = System.Text.Json.JsonSerializer.Serialize(
            work,
            new System.Text.Json.JsonSerializerOptions { IgnoreReadOnlyProperties = true }
        );
        var restored = HaulerCheckpoint.Read(json)!;
        Assert.Equal(work.DueTicks, restored.DueTicks);
        Assert.Equal(routine.Seed, restored.Routine!.Seed);
        Assert.Equal(routine.Threats, restored.Routine.Threats);
        Assert.Equal(routine.RecentStations, restored.Routine.RecentStations);
        Assert.Equal(routine.Pace, restored.Routine.Pace);
        Assert.True(restored.Routine.ShelterPending);
        var expired = TravelerRoutineRules.Threatened(routine, 9, TravelerRoutineRules.ThreatMemory.Ticks + TimeSpan.TicksPerMinute);
        Assert.Equal(9, Assert.Single(expired.Threats).SystemId);
        Assert.Null(HaulerCheckpoint.Read(json.Replace("\"Routine\":", "\"UnknownLegacyRoutine\":"))!.Routine);
    }

    [Fact]
    public void DestinationAffinitiesAreStableAndRecentStopsAreLessLikely()
    {
        var routine = TravelerRoutineRules.Create(42);
        int original = TravelerRoutineRules.DestinationWeight(routine, 100, 4);
        var recent = TravelerRoutineRules.Arrive(routine, 100);
        Assert.Equal(original / 4, TravelerRoutineRules.DestinationWeight(recent, 100, 4));
        Assert.True(Enumerable.Range(0, 32).Select(sequence => TravelerRoutineRules.Draw(routine, sequence)).Distinct().Count() == 32);
        var waits = Enumerable
            .Range(0, 32)
            .Select(sequence => TravelerRoutineRules.LayoverSeconds(routine, TrafficJourneyKind.Regional, 42, sequence))
            .ToArray();
        Assert.True(waits.Distinct().Count() > 20);
        var brisk = routine with { Pace = TravelPace.Brisk };
        var leisurely = routine with { Pace = TravelPace.Leisurely };
        Assert.True(
            TravelerRoutineRules.LayoverSeconds(brisk, TrafficJourneyKind.Local, 42, 0)
                < TravelerRoutineRules.LayoverSeconds(leisurely, TrafficJourneyKind.Local, 42, 0)
        );
    }
}
