using Eden.Protocol.Flight;
using Eden.Protocol.PilotSessions;
using Eden.Protocol.Tests.Fixtures;

namespace Eden.Protocol.Tests.PilotSessions;

public sealed class PilotBindingsTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void LocationChangesAndBoardingRetireSyntheticInventoryBindings(bool boarding)
    {
        using TestWorld world = new();
        var pilot = world.Store.FindPilot(1000001)!;
        using PilotCallContext context = new(
            1000001,
            pilot.Account,
            [pilot],
            selected => new FlightSession(world.Store, selected, TimeProvider.System, new(), world.Objects),
            42,
            SignedSettings.Empty
        );
        context.Select(pilot);
        var hangar = context.Reference("@hangar", PilotBindingLifetime.Location);
        var ship = context.Reference($"@ship:{pilot.ShipId}", PilotBindingLifetime.Location);
        var skills = context.Reference("@skills", PilotBindingLifetime.Pilot);
        if (boarding)
        {
            context.ChangeShip(pilot);
        }
        else
        {
            context.ReleaseLocationReferences();
        }
        Assert.Null(context.ResolveReference(hangar.Id));
        Assert.Null(context.ResolveReference(ship.Id));
        Assert.Equal("@skills", context.ResolveReference(skills.Id));
        Assert.NotEqual(hangar.Id, context.Reference("@hangar", PilotBindingLifetime.Location).Id);
    }
}
