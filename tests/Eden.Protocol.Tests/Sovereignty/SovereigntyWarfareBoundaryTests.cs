using Eden.Data.World.Alliances;
using Eden.Data.World.Characters;
using Eden.Data.World.Sovereignty;
using Eden.Gameplay.Sovereignty;
using Eden.Infrastructure.Diagnostics;
using Eden.Protocol.Flight;
using Eden.Protocol.Serialization;
using Eden.World.Catalog;
using Eden.World.Runtime.Economy;
using Eden.World.Storage;
using Microsoft.Extensions.DependencyInjection;

namespace Eden.Protocol.Tests.Sovereignty;

public sealed class SovereigntyWarfareBoundaryTests
{
    [Fact]
    public void UnalliedPilotsCanAttackOwnedHubsWithoutReceivingClaims()
    {
        using SovereigntyCaptureFixture fixture = new();
        long defender = fixture.Fit();
        long attacker = fixture.Fit(pilot: SovereigntyCaptureFixture.OtherPilotId);
        fixture.Grant(SovereigntyCaptureFixture.Strontium, 500);
        fixture.Grant(SovereigntyCaptureFixture.Strontium, 500, pilot: SovereigntyCaptureFixture.OtherPilotId);
        long hub = SovereigntyCampaignTests.Claim(fixture, defender);
        using (var action = fixture.Live.Economy.Begin(TransactionParticipants.None))
        {
            int corporation = action.Pilots.Find(SovereigntyCaptureFixture.OtherPilotId)!.CorporationId;
            action.Remove(action.Set<SavedAllianceMember>().Find(corporation)!);
            action.Commit();
        }
        SovereigntyCampaignTests.Reinforce(fixture, attacker, hub);
        Assert.Equal(fixture.AllianceId, fixture.Claims.Read(SovereigntyCaptureFixture.SystemId)!.AllianceId);
    }

    [Fact]
    public void ExtraSpawnsRespectThePerCampaignCapAndKeepDistinctLocations()
    {
        using SovereigntyCaptureFixture fixture = new();
        long defender = fixture.Fit();
        long attacker = fixture.Fit(pilot: SovereigntyCaptureFixture.OtherPilotId);
        fixture.Grant(SovereigntyCaptureFixture.Strontium, 500);
        fixture.Grant(SovereigntyCaptureFixture.Strontium, 500, pilot: SovereigntyCaptureFixture.OtherPilotId);
        long hub = SovereigntyCampaignTests.Claim(fixture, defender);
        SovereigntyCampaignTests.Reinforce(fixture, attacker, hub);
        long at = fixture.Entosis.Hub(SovereigntyCaptureFixture.SystemId)!.Conflict!.CampaignAt!.Value;
        fixture.Clock.AdvanceWallTime(TimeSpan.FromTicks(at - fixture.Clock.Now));
        fixture.Entosis.AdvanceAll();
        using (var action = fixture.Live.Economy.Begin(TransactionParticipants.None))
        {
            var original = SovereigntyCampaignTests.Nodes(fixture);
            foreach (var node in original)
            {
                action.Add(
                    new SavedSovereigntyNode
                    {
                        Id = Eden.World.Inventory.InventoryPersistence.NextId(action),
                        SystemId = node.SystemId,
                        SourceSystemId = node.SourceSystemId,
                        HubId = node.HubId,
                        CorporationId = node.CorporationId,
                        AllianceId = node.AllianceId,
                        UpdatedAt = at,
                        X = node.X + 2_000_000,
                        Y = node.Y,
                        Z = node.Z,
                    }
                );
            }
            action.Commit();
        }
        fixture.Clock.AdvanceWallTime(TimeSpan.FromMinutes(30));
        fixture.Entosis.AdvanceAll();
        var nodes = SovereigntyCampaignTests.Nodes(fixture);
        Assert.Equal(10, nodes.Length);
        Assert.All(
            nodes.GroupBy(row => row.SystemId),
            group => Assert.Equal(group.Count(), group.Select(row => (row.X, row.Y, row.Z)).Distinct().Count())
        );
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public void FreshWarmupCannotCreateOvertimeAndUnobservedNewDayCanStillBeAttacked(int days)
    {
        using SovereigntyCaptureFixture fixture = new();
        long defender = fixture.Fit();
        long attacker = fixture.Fit(pilot: SovereigntyCaptureFixture.OtherPilotId);
        fixture.Grant(SovereigntyCaptureFixture.Strontium, 500);
        fixture.Grant(SovereigntyCaptureFixture.Strontium, 500, pilot: SovereigntyCaptureFixture.OtherPilotId);
        long hub = SovereigntyCampaignTests.Claim(fixture, defender);
        fixture.Position(new(2_000_070_000, 0, 0), SovereigntyCaptureFixture.OtherPilotId);
        fixture.Lock(hub, SovereigntyCaptureFixture.OtherPilotId);
        var window = fixture.Entosis.Hub(SovereigntyCaptureFixture.SystemId)!.Conflict!.Window;
        if (days == 0)
        {
            fixture.Clock.AdvanceWallTime(TimeSpan.FromTicks(window.EndsAt - TimeSpan.TicksPerMinute - fixture.Clock.Now));
        }
        else
        {
            fixture.Clock.AdvanceWallTime(TimeSpan.FromDays(days));
        }
        fixture.Entosis.Activate(
            SovereigntyCaptureFixture.OtherPilotId,
            fixture.Ship(SovereigntyCaptureFixture.OtherPilotId),
            attacker,
            hub,
            10,
            fixture.Clock.Now
        );
        fixture.Clock.AdvanceWallTime(TimeSpan.FromMinutes(2));
        fixture.Entosis.Renew(SovereigntyCaptureFixture.OtherPilotId, attacker, fixture.Clock.Now);
        fixture.Clock.AdvanceWallTime(TimeSpan.FromMinutes(2));
        double value = fixture.Entosis.Hub(SovereigntyCaptureFixture.SystemId)!.Conflict!.Capture.Value;
        Assert.Equal(days == 0, value == 1);
        if (days == 0)
        {
            Assert.False(fixture.Entosis.TravelLocked(fixture.Ship(SovereigntyCaptureFixture.OtherPilotId), fixture.Clock.Now));
        }
    }

    [Fact]
    public void ClosedWindowRejectsAtomicallyAndPartialAttackContinuesOvertime()
    {
        using SovereigntyCaptureFixture fixture = new();
        long defender = fixture.Fit();
        long attacker = fixture.Fit(pilot: SovereigntyCaptureFixture.OtherPilotId);
        fixture.Grant(SovereigntyCaptureFixture.Strontium, 500);
        long fuel = fixture.Grant(SovereigntyCaptureFixture.Strontium, 500, pilot: SovereigntyCaptureFixture.OtherPilotId);
        long hub = SovereigntyCampaignTests.Claim(fixture, defender);
        fixture.Position(new(2_000_070_000, 0, 0), SovereigntyCaptureFixture.OtherPilotId);
        fixture.Lock(hub, SovereigntyCaptureFixture.OtherPilotId);
        var window = fixture.Entosis.Hub(SovereigntyCaptureFixture.SystemId)!.Conflict!.Window;
        fixture.Clock.AdvanceWallTime(TimeSpan.FromTicks(window.EndsAt - TimeSpan.FromMinutes(3).Ticks - fixture.Clock.Now));
        fixture.Entosis.Activate(
            SovereigntyCaptureFixture.OtherPilotId,
            fixture.Ship(SovereigntyCaptureFixture.OtherPilotId),
            attacker,
            hub,
            10,
            fixture.Clock.Now
        );
        fixture.Clock.AdvanceWallTime(TimeSpan.FromMinutes(2));
        fixture.Entosis.Renew(SovereigntyCaptureFixture.OtherPilotId, attacker, fixture.Clock.Now);
        fixture.Clock.AdvanceWallTime(TimeSpan.FromMinutes(2));
        fixture.Entosis.Renew(SovereigntyCaptureFixture.OtherPilotId, attacker, fixture.Clock.Now);
        Assert.True(fixture.Entosis.Hub(SovereigntyCaptureFixture.SystemId)!.Conflict!.Capture.Value < 1);
        Assert.Equal(window, fixture.Entosis.Hub(SovereigntyCaptureFixture.SystemId)!.Conflict!.Window);
        fixture.Entosis.Disconnect(SovereigntyCaptureFixture.OtherPilotId, fixture.Clock.Now);
        fixture.Clock.AdvanceWallTime(TimeSpan.FromHours(1));
        fixture.Entosis.AdvanceAll();
        Assert.Equal(1, fixture.Entosis.Hub(SovereigntyCaptureFixture.SystemId)!.Conflict!.Capture.Value);
        int quantity = fixture.Quantity(fuel);
        double capacitor = fixture.Capacitor(SovereigntyCaptureFixture.OtherPilotId);
        Assert.Throws<InvalidDataException>(() =>
            fixture.Entosis.Activate(
                SovereigntyCaptureFixture.OtherPilotId,
                fixture.Ship(SovereigntyCaptureFixture.OtherPilotId),
                attacker,
                hub,
                10,
                fixture.Clock.Now
            )
        );
        Assert.Equal(quantity, fixture.Quantity(fuel));
        Assert.Equal(capacitor, fixture.Capacitor(SovereigntyCaptureFixture.OtherPilotId));
    }

    [Fact]
    public void PrimeHourChangesRequireExecutorAndDoNotShortenExistingTransition()
    {
        using SovereigntyCaptureFixture fixture = new();
        Assert.Throws<InvalidDataException>(() => fixture.Entosis.SetPrimeHour(SovereigntyCaptureFixture.PilotId, 24));
        fixture.Entosis.SetPrimeHour(SovereigntyCaptureFixture.PilotId, 0);
        long at = fixture.Entosis.PrimeTime(SovereigntyCaptureFixture.PilotId).ValidAfter!.Value;
        using (var action = fixture.Live.Economy.Begin(TransactionParticipants.None))
        {
            action.Set<SavedAlliance>().Find(fixture.AllianceId)!.ExecutorCorporationId = null;
            action.Commit();
        }
        Assert.Throws<InvalidDataException>(() => fixture.Entosis.SetPrimeHour(SovereigntyCaptureFixture.PilotId, 6));
        Assert.Equal(at, fixture.Entosis.PrimeTime(SovereigntyCaptureFixture.PilotId).ValidAfter);
        fixture.Clock.AdvanceWallTime(TimeSpan.FromTicks(at - fixture.Clock.Now));
        Assert.Equal(0, fixture.Entosis.PrimeTime(SovereigntyCaptureFixture.PilotId).CurrentHour);
    }

    [Fact]
    public void NativeNodeSlimCarriesCampaignSourceAndWarmReadsUseNoSqlOrFits()
    {
        using SovereigntyCaptureFixture fixture = new();
        long defender = fixture.Fit();
        long attacker = fixture.Fit(pilot: SovereigntyCaptureFixture.OtherPilotId);
        fixture.Grant(SovereigntyCaptureFixture.Strontium, 500);
        fixture.Grant(SovereigntyCaptureFixture.Strontium, 500, pilot: SovereigntyCaptureFixture.OtherPilotId);
        long hub = SovereigntyCampaignTests.Claim(fixture, defender);
        SovereigntyCampaignTests.Reinforce(fixture, attacker, hub);
        long at = fixture.Entosis.Hub(SovereigntyCaptureFixture.SystemId)!.Conflict!.CampaignAt!.Value;
        fixture.Clock.AdvanceWallTime(TimeSpan.FromTicks(at - fixture.Clock.Now));
        fixture.Entosis.AdvanceAll();
        var node = SovereigntyCampaignTests.Nodes(fixture)[0];
        var contact = fixture.Entosis.CommandNodes(node.SystemId).Single(row => row.Id == node.Id);
        var slim = Assert.IsType<BlueInstance>(BlueMarshal.Decode(BlueMarshal.Encode(new StaticSlimItem(contact))));
        var fields = BlueRead.Fields(slim.State);
        var source = BlueRead.Tuple(fields["campaign_sourceInfo"], 4);
        Assert.Equal(new object?[] { (long)SovereigntyCaptureFixture.SystemId, 2L, hub, (long)fixture.AllianceId }, source);
        Assert.Equal((long)fixture.AllianceId, BlueRead.Tuple(fields["entosis_score"], 4)[3]);
        _ = fixture.Entosis.AllianceHubs(SovereigntyCaptureFixture.PilotId);
        using var measure = SessionPerformance.Begin("warm sovereignty campaign", _ => { }, force: true)!;
        for (int read = 0; read < 4; read++)
        {
            fixture.Entosis.CommandNodes(node.SystemId);
            fixture.Entosis.Hub(SovereigntyCaptureFixture.SystemId);
            fixture.Entosis.PrimeTime(SovereigntyCaptureFixture.PilotId);
        }
        Assert.Equal(0, measure.Commands);
        Assert.Equal(0, measure.Connections);
        Assert.Equal(0, measure.FittingCalculations);
    }
}
