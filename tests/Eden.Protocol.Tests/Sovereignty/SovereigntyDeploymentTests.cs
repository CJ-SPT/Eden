using Eden.Data.World.Alliances;
using Eden.Data.World.Inventory;
using Eden.Gameplay.Flight;
using Eden.Gameplay.Inventory;
using Eden.Gameplay.Sovereignty;
using Eden.Gameplay.Structures;
using Eden.World.Storage;
using Microsoft.Extensions.DependencyInjection;

namespace Eden.Protocol.Tests.Sovereignty;

public sealed class SovereigntyDeploymentTests
{
    [Fact]
    public async Task ConcurrentDeploymentSettlesOnlyOneHullAndOneHub()
    {
        using SovereigntyCaptureFixture fixture = new();
        fixture.Position(new(2_000_000_000, 0, 0));
        long first = fixture.Grant(EntosisRules.HubTypeId);
        long second = fixture.Grant(EntosisRules.HubTypeId);
        async Task<bool> Deploy(long item) =>
            await Task.Run(() =>
            {
                try
                {
                    fixture.Entosis.Deploy(SovereigntyCaptureFixture.PilotId, item);
                    return true;
                }
                catch (InvalidDataException)
                {
                    return false;
                }
            });
        bool[] results = await Task.WhenAll(Deploy(first), Deploy(second));
        Assert.Single(results, succeeded => succeeded);
        Assert.Equal(1, fixture.Quantity(first) + fixture.Quantity(second));
        Assert.NotNull(fixture.Entosis.Hub(SovereigntyCaptureFixture.SystemId));
        Assert.Null(fixture.Claims.Read(SovereigntyCaptureFixture.SystemId));
    }

    [Fact]
    public void DeploymentConsumesOneHullAndPublishesOneNeutralHubAtTheCatalogStarDistance()
    {
        using SovereigntyCaptureFixture fixture = new();
        fixture.Position(new(20_000_000_000, 0, 0));
        long stack = fixture.Grant(EntosisRules.HubTypeId, 2);
        var deployed = fixture.Entosis.Deploy(SovereigntyCaptureFixture.PilotId, stack);
        Assert.Equal(1, fixture.Quantity(stack));
        Assert.Equal(new(2_000_000_000, 0, 0), deployed.Structure.Position);
        Assert.Equal(EntosisRules.NeutralCorporationId, deployed.Structure.CorporationId);
        Assert.Equal(StructureState.ShieldVulnerable, deployed.Structure.State);
        Assert.Equal(TimeSpan.FromSeconds(595).Ticks, fixture.Entosis.Hub(SovereigntyCaptureFixture.SystemId)!.CaptureDuration);
        Assert.Null(fixture.Claims.Read(SovereigntyCaptureFixture.SystemId));
        Assert.Throws<InvalidDataException>(() => fixture.Entosis.Deploy(SovereigntyCaptureFixture.PilotId, stack));
        Assert.Equal(1, fixture.Quantity(stack));
        Assert.Equal(deployed.Structure.Id, fixture.Entosis.Hub(SovereigntyCaptureFixture.SystemId)!.HubId);
        var change = Assert.Single(deployed.Changes);
        Assert.Equal(2, change.Previous!.Quantity);
        Assert.Equal(1, change.Item.Quantity);
    }

    [Theory]
    [InlineData("empire")]
    [InlineData("membership")]
    [InlineData("source")]
    public void RejectedDeploymentLeavesHullAndTerritorialStateUnchanged(string reason)
    {
        using SovereigntyCaptureFixture fixture = new();
        long stack = fixture.Grant(EntosisRules.HubTypeId);
        fixture.Position(new(2_000_000_000, 0, 0), system: reason == "empire" ? 30000142 : SovereigntyCaptureFixture.SystemId);
        using (var action = fixture.Live.Economy.Begin(TransactionParticipants.None))
        {
            if (reason == "membership")
            {
                action.Remove(action.Set<SavedAllianceMember>().Find(fixture.CorporationId)!);
            }
            if (reason == "source")
            {
                action.Set<OwnedItem>().Find(stack)!.LocationId = 60003760;
                action.Set<OwnedItem>().Find(stack)!.FlagId = (int)InventoryFlag.Hangar;
            }
            action.Commit();
        }
        Assert.Throws<InvalidDataException>(() => fixture.Entosis.Deploy(SovereigntyCaptureFixture.PilotId, stack));
        Assert.Equal(1, fixture.Quantity(stack));
        Assert.Null(fixture.Claims.Read(SovereigntyCaptureFixture.SystemId));
        Assert.Null(fixture.Entosis.Hub(SovereigntyCaptureFixture.SystemId));
    }
}
