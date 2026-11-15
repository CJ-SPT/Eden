using System.Text.Json.Nodes;
using Eden.Gameplay.Configuration;
using Eden.Gameplay.Docking;
using Eden.Gameplay.Dogma;
using Eden.Gameplay.Flight;
using Eden.Gameplay.Inventory;
using Eden.Protocol.Flight;
using Eden.Protocol.Serialization;
using Eden.Protocol.Skills;
using Eden.World.Configuration;
using Eden.World.Runtime;
using Eden.World.Storage;
using Microsoft.Extensions.DependencyInjection;

namespace Eden.Protocol.Tests.Flight;

public sealed class TravelConfigurationTests
{
    [Theory]
    [InlineData(1.5, 200000)]
    [InlineData(3, 200000)]
    [InlineData(8, 200000)]
    [InlineData(1.5, 3e12)]
    [InlineData(3, 3e12)]
    [InlineData(8, 3e12)]
    public void TravelScalingShortensAlignmentAndWarpWhilePreservingHullAndLanding(double speed, double distance)
    {
        FlightProperties original = new(100000, 47, 1163000, 4.5f, 295, speed);
        Dictionary<DogmaAttributeId, double> attributes = new()
        {
            [DogmaAttributeId.WarpSpeedMultiplier] = speed,
            [DogmaAttributeId.Agility] = original.Agility,
            [DogmaAttributeId.MaxVelocity] = original.MaxSpeed,
        };
        WorldTuningRules.ScaleTravel(new(), attributes);
        Assert.Equal(speed, attributes[DogmaAttributeId.WarpSpeedMultiplier]);
        WorldTuningRules.ScaleTravel(new(0, 2, 2), attributes);
        FlightProperties tuned = original with
        {
            WarpSpeed = attributes[DogmaAttributeId.WarpSpeedMultiplier],
            Agility = (float)attributes[DogmaAttributeId.Agility],
        };
        Assert.Equal(original.InertiaTime / 2, tuned.InertiaTime);
        Assert.Equal(original.MaxSpeed, attributes[DogmaAttributeId.MaxVelocity]);
        Assert.Equal(original.Mass, tuned.Mass);
        SpacePosition destination = new(distance, 0, 0);
        WarpCourse baseline = new(SpacePosition.Zero, destination, 0, original, 230);
        WarpCourse faster = new(SpacePosition.Zero, destination, 0, tuned, 230);
        Assert.Equal(baseline.WireFactor * 2, faster.WireFactor);
        Assert.True(faster.DurationSeconds(100) < baseline.DurationSeconds(100));
        Assert.Equal(baseline.Destination, faster.Destination);
        Assert.True(AlignmentTicks(tuned, destination) < AlignmentTicks(original, destination));
        Assert.Equal(
            WarpCourse.LandingPoint(1, original.ShipRadius, SpacePosition.Zero, destination, 0),
            WarpCourse.LandingPoint(1, tuned.ShipRadius, SpacePosition.Zero, destination, 0)
        );
        Dictionary<DogmaAttributeId, double> missing = [];
        WorldTuningRules.ScaleTravel(new(0, 2, 2), missing);
        Assert.Empty(missing);
    }

    [Fact]
    public async Task ResidentTravelMatchesNativeModifiersAndSurvivesCheckpointRestartAndRestore()
    {
        TestClock clock = new();
        using TestWorld fixture = new(clock: clock);
        WorldDatabase database = new(fixture.Path);
        WorldConfigurationStore configuration = new(database, clock);
        var initial = configuration.Read();
        var legacy = JsonNode.Parse(configuration.Export(initial.Active.Id))!;
        legacy["settings"]!["navigation"]!.AsObject().Remove("warpSpeedMultiplier");
        legacy["settings"]!["navigation"]!.AsObject().Remove("alignSpeedMultiplier");
        Assert.Equal(new NavigationPolicy(), ConfigurationValidation.Decode(legacy.ToJsonString()).Settings.Navigation);
        var fitting = fixture.Services.GetRequiredService<IPilotFitting>();
        DockedPilot baseline;
        using (var live = new LiveWorld(database, clock, combatObjects: false, simulationObjects: false))
        {
            baseline = fixture.Store.FindPilot(1000001)!;
        }
        NavigationPolicy policy = new(0, 2, 1.25m);
        var pending = configuration.Save(
            initial.Generation,
            initial.Active.Draft with
            {
                Settings = initial.Active.Draft.Settings with { Navigation = policy },
            },
            "travel test"
        );
        Assert.Equal(new NavigationPolicy(), database.Settings.Navigation);
        WorldInitialization.Initialize(database, clock);
        Assert.Equal(pending.Pending!.Id, configuration.Read().Active.Id);
        var position = baseline.Home.Position + new SpacePosition(1_000_000_000, 0, 0);
        using (var live = new LiveWorld(database, clock, combatObjects: false, simulationObjects: false))
        {
            var pilot = fixture.Store.FindPilot(1000001)!;
            AssertTuned(pilot);
            var attributes = fitting.ShipAttributes(pilot.PilotId);
            Assert.Equal(pilot.Flight.Agility, attributes[DogmaAttributeId.Agility], 6);
            Assert.Equal(
                pilot.Flight.WarpSpeed,
                attributes[DogmaAttributeId.BaseWarpSpeed] * attributes[DogmaAttributeId.WarpSpeedMultiplier]
            );
            Assert.Equal(attributes, fitting.ShipAttributes(pilot.PilotId));
            var brain = fixture.Services.GetRequiredService<PilotBrain>().Read(pilot.PilotId, pilot.ShipId);
            foreach (var (attribute, factor) in WorldTuningRules.TravelMultipliers(policy))
            {
                var effect = Assert.Single(
                    brain.ShipEffects,
                    effect => effect.Attribute == attribute && effect.Operation == BrainOperation.PostMultiply
                );
                Assert.Equal(pilot.ShipId, effect.TargetId);
                Assert.Equal(BrainModifier.Item, effect.Modifier);
                Assert.Equal(factor, effect.Value);
                var wire = Assert.IsType<BlueNewObject>(BlueMarshal.Decode(BlueMarshal.Encode(effect)));
                Assert.Equal(factor, Assert.IsType<Dictionary<string, object?>>(wire.State)["value"]);
            }
            fixture.Store.SetPosition(pilot.ShipId, position);
            await live.FlushAsync();
        }
        WorldInitialization.Initialize(new(fixture.Path), clock);
        using (var reopened = new LiveWorld(new(fixture.Path), clock, combatObjects: false, simulationObjects: false))
        {
            var pilot = fixture.Store.FindPilot(1000001)!;
            AssertTuned(pilot);
            Assert.Equal(position, pilot.Position);
        }
        var state = configuration.Read();
        configuration.Restore(state.Generation, initial.Active.Id, "restore travel");
        WorldInitialization.Initialize(database, clock);
        using var restored = new LiveWorld(database, clock, combatObjects: false, simulationObjects: false);
        Assert.Equal(baseline.Flight, fixture.Store.FindPilot(1000001)!.Flight);

        void AssertTuned(DockedPilot pilot)
        {
            Assert.Equal(baseline.Flight.WarpSpeed * 2, pilot.Flight.WarpSpeed);
            Assert.Equal(baseline.Flight.Agility / 1.25, pilot.Flight.Agility, 6);
            Assert.Equal(baseline.Flight.Mass, pilot.Flight.Mass);
            Assert.Equal(baseline.Flight.MaxSpeed, pilot.Flight.MaxSpeed);
        }
    }

    private static int AlignmentTicks(FlightProperties hull, SpacePosition destination)
    {
        NavigationState navigation = new();
        SpacePosition position = SpacePosition.Zero;
        Assert.True(navigation.StartWarpPoint(1, hull, position, destination, 42, 0, 0));
        for (int stamp = 1; stamp < 180; stamp++)
        {
            position = navigation.Advance(1, stamp, hull, position, _ => null, []).Position;
            if (navigation.Warp is not null)
            {
                return stamp;
            }
        }
        throw new InvalidOperationException("The unobstructed ship did not enter warp.");
    }
}
