using Eden.Gameplay.Exploration;
using Eden.Gameplay.Flight;
using Eden.Protocol.Exploration;
using Eden.Protocol.Serialization;

namespace Eden.Protocol.Tests.Exploration;

public sealed class ProbeScanningRuleTests
{
    internal static CombatAnomaly Site =>
        new(
            CombatAnomalyRules.FirstSiteId,
            "AAA-000",
            CombatAnomalyRules.SystemId,
            CombatAnomalyRules.DungeonId,
            123,
            1,
            SpacePosition.Zero,
            false,
            [],
            IsSignature: true
        );

    internal static (ScanProbe Probe, ProbePlacement Placement)[] Formation(SpacePosition center, double strength = 40, int step = 1)
    {
        double offset = ProbeScanRules.AstronomicalUnit * 0.025;
        SpacePosition[] directions =
        [
            new(1, 0, 0),
            new(-1, 0, 0),
            new(0, 1, 0),
            new(0, -1, 0),
            new(0, 0, 1),
            new(0, 0, -1),
            new(1, 1, 1),
            new(-1, -1, -1),
        ];
        return directions
            .Select(
                (d, i) =>
                    (
                        new ScanProbe(i + 1, 30013, Site.SystemId, center, long.MaxValue, new(strength, 0.25, 2, 0.125, 4_000_000)),
                        new ProbePlacement(i + 1, center + d * offset, step)
                    )
            )
            .ToArray();
    }

    [Fact]
    public void ScannerIdentifiersRemainDistinctInNativeVisiblePrefix()
    {
        // The reported native column clipped the distinguishing final digit of
        // EDN-001/002/003/004. Prefixes must differ before that clipping boundary.
        var ids = Enumerable.Range(0, 40_000).Select(i => ScannerTargetIds.FromSequence(i)).ToArray();
        Assert.All(ids, id => Assert.Matches("^[A-Z]{3}-[0-9]{3}$", id));
        Assert.Equal(ids.Length, ids.Distinct().Count());
        Assert.Equal(ids.Length, ids.Select(id => id[..6]).Distinct().Count());
        Assert.Equal("AAA-000", ids[0]);
        Assert.Equal("BAA-000", ids[1]);
        Assert.Throws<ArgumentOutOfRangeException>(() => ScannerTargetIds.FromSequence(-1));
    }

    [Fact]
    public void MovingAndNarrowingAThreeDimensionalFormationResolvesTheSite()
    {
        var wide = Assert.IsType<ProbeScanResult>(ProbeScanRules.Scan(Site, Formation(SpacePosition.Zero, step: 8)));
        var narrow = Assert.IsType<ProbeScanResult>(ProbeScanRules.Scan(Site, Formation(SpacePosition.Zero)));
        Assert.InRange(wide.Certainty, 0.001, ProbeScanRules.GroupThreshold);
        Assert.Equal(1, narrow.Certainty);
        Assert.Equal(Site.Position, narrow.Position);
        Assert.Equal(0, narrow.Deviation);
        Assert.Equal(wide, ProbeScanRules.Scan(Site, Formation(SpacePosition.Zero, step: 8)));
        Assert.Null(ProbeScanRules.Scan(Site, Formation(new(100 * ProbeScanRules.AstronomicalUnit, 0, 0))));
    }

    [Fact]
    public void CoincidentAndCoplanarProbesCannotGiveAPerfectFix()
    {
        var coincident = Formation(SpacePosition.Zero)
            .Select(p => (p.Probe, p.Placement with { Destination = SpacePosition.Zero }))
            .ToArray();
        var coplanar = Formation(SpacePosition.Zero)
            .Select(p => (p.Probe, p.Placement with { Destination = p.Placement.Destination with { Z = 0 } }))
            .ToArray();
        Assert.True(ProbeScanRules.Scan(Site, coincident)!.Certainty < 1);
        Assert.True(ProbeScanRules.Scan(Site, coplanar)!.Certainty < 1);
        Assert.True(ProbeScanRules.Scan(Site, Formation(SpacePosition.Zero).Take(3).ToArray())!.Certainty < 1);
        Assert.True(ProbeScanRules.Scan(Site, Formation(SpacePosition.Zero, strength: 5))!.Certainty < 1);
    }

    [Fact]
    public void FollowingReportedPositionsAndReducingRangeCanResolveAnInitiallyUnknownSite()
    {
        var site = Site with { Position = new(0.33 * ProbeScanRules.AstronomicalUnit, 0.47 * ProbeScanRules.AstronomicalUnit, 0) };
        SpacePosition center = ProbeScanningWire.CoarsePosition(site.Position);
        ProbeScanResult? result = null;
        for (int step = ProbeScanRules.RangeSteps; step >= 1; step--)
        {
            for (int attempt = 0; attempt < 5; attempt++)
            {
                double scale = Math.Pow(2, step - 1) * 5;
                var probes = Formation(center, strength: 50, step: step)
                    .Select(p => (p.Probe, p.Placement with { Destination = center + (p.Placement.Destination - center) * scale }))
                    .ToArray();
                result = ProbeScanRules.Scan(site, probes);
                Assert.NotNull(result);
                center = result.Position;
                if (result.Certainty >= 1)
                {
                    Assert.Equal(site.Position, result.Position);
                    return;
                }
            }
        }
        Assert.True(result!.Certainty >= 1);
    }

    [Theory]
    [InlineData(-1, 0, 0)]
    [InlineData(9, 0, 0)]
    [InlineData(1, double.NaN, 0)]
    [InlineData(1, double.PositiveInfinity, 0)]
    [InlineData(1, 1e20, 0)]
    public void InvalidGeometryIsRejected(int step, double x, double y) =>
        Assert.Throws<InvalidDataException>(() => ProbeScanRules.Validate(new(1, new(x, y, 0), step), new(40, 0.25, 2, 0.125, 4_000_000)));

    [Theory]
    [InlineData(0.1, false, false, false)]
    [InlineData(0.25, true, false, false)]
    [InlineData(0.75, true, true, false)]
    [InlineData(1, true, true, true)]
    public void NativeResultsRevealOnlyTheInformationEarnedBySignalStrength(double certainty, bool group, bool name, bool perfect)
    {
        var row = ProbeScanningWire.Result(new(Site, certainty, new(1, 2, 3), perfect ? 0 : 1000));
        var decoded = Assert.IsType<BlueInstance>(BlueMarshal.Decode(BlueMarshal.Encode(row)));
        var fields = Assert.IsType<Dictionary<string, object?>>(decoded.State);
        Assert.Equal(group, fields["strengthAttributeID"] is not null);
        Assert.Equal(name, fields["dungeonNameID"] is not null);
        Assert.Equal(perfect, fields["data"] is object[]);
        Assert.Equal(!perfect, fields["data"] is double);
        Assert.Null(fields["itemID"]);
    }
}
