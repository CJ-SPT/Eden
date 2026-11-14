using Eden.Gameplay.Fleets;
using Eden.Protocol.Fleets;
using Eden.Protocol.PilotSessions;
using Microsoft.Extensions.DependencyInjection;
using Fixture = Eden.Protocol.Tests.Persistence.PassiveFittingTests.Fixture;

namespace Eden.Protocol.Tests.Propulsion;

public sealed partial class SpatialModuleOperationTests
{
    [Fact]
    public void CloakedCommanderCanTagVisibleContactsWithoutWeaponTargetEligibility()
    {
        using Fixture f = Prepare();
        long cloak = f.Fit(Prototype);
        Space(f);
        ObserverSpace(f);
        using var scope = f.Db.Services.CreateScope();
        using var source = Client(f, 1000001);
        using var observer = Client(f, 1000002);
        source.Flight.RefreshPeers();
        var fleets = scope.ServiceProvider.GetRequiredService<CooperativeFleetHandler>();
        Assert.IsType<PilotCallResult.Success>(fleets.Call(source, "fleetObjectHandler", "CreateFleet", []));
        var spatial = scope.ServiceProvider.GetRequiredService<Eden.Protocol.Propulsion.SpatialModuleHandler>();
        Assert.IsType<PilotCallResult.Success>(spatial.Call(source, "Activate", [cloak, "cloakingPrototype", null, 0L]));
        Assert.True(source.Flight.Cloaked);
        Assert.False(source.Flight.CanTarget);
        Assert.IsType<PilotCallResult.Success>(fleets.Call(source, "beyonce", "CmdFleetTagTarget", [observer.Pilot.ShipId, "A"]));
        var tag = Assert.Single(f.Db.Services.GetRequiredService<ICooperativeFleets>().TargetTags(source.Pilot.PilotId));
        Assert.Equal(observer.Pilot.ShipId, tag.ItemId);
        Assert.Equal("A", tag.Tag);
        Assert.Equal("A", Assert.Single(source.Notifications.OfType<FleetStateNotification>().Last().Tags).Tag);
    }
}
