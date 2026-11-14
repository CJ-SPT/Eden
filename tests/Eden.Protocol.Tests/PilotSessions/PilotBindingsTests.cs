using Eden.Protocol.Flight;
using Eden.Protocol.PilotSessions;
using Microsoft.Extensions.DependencyInjection;

namespace Eden.Protocol.Tests.PilotSessions;

public sealed class PilotBindingsTests
{
    [Fact]
    public void CorporationRegistrySurvivesTravelAndReleasesWhenCorporationChanges()
    {
        using TestWorld world = new();
        PlayerCorporationTests.Prepare(world);
        var pilot = world.Store.FindPilot(1000001)!;
        using var scope = world.Services.CreateScope();
        var router = scope.ServiceProvider.GetRequiredService<PilotCallRouter>();
        using PilotCallContext context = new(
            1000001,
            pilot.Account,
            [pilot],
            scope.ServiceProvider.GetRequiredService<FlightSessionFactory>().Create,
            42,
            SignedSettings.Empty
        );
        context.Select(pilot);
        var binding = Assert.IsType<BoundPilotReply>(
            Assert
                .IsType<PilotCallResult.Success>(
                    router.Call(context, "corpRegistry", "MachoBindObject", new object?[] { (long)pilot.CorpId, null }, _ => { })
                )
                .Reply
        );
        object?[] address = [1L, (long)PilotLogin.NodeId, null, null];
        context.ReleaseLocationReferences();
        string service = router.ResolveService(context, address, binding.Reference.Id);
        Assert.Equal("corpRegistry", service);
        var applications = Assert.IsType<PilotCallResult.Success>(
            router.Call(context, service, "GetMyApplications", Array.Empty<object?>(), _ => { })
        );
        Assert.Empty(Assert.IsType<Dictionary<long, object?>>(applications.Reply.ToBlue()));
        Assert.IsType<PilotCallResult.Success>(
            router.Call(context, service, "AddCorporation", PlayerCorporationTests.CreateArguments(), _ => { })
        );
        Assert.NotEqual(pilot.CorpId, context.Pilot.CorpId);
        Assert.Equal("unknown binding", router.ResolveService(context, address, binding.Reference.Id));
        Assert.IsType<PilotCallResult.Failure>(
            router.Call(context, "corpRegistry", "MachoBindObject", new object?[] { (long)pilot.CorpId, null }, _ => { })
        );
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void DockedBoardingRetainsLocationBindingsUntilDeparture(bool boarding)
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
            Assert.Equal("@hangar", context.ResolveReference(hangar.Id));
            Assert.Equal($"@ship:{pilot.ShipId}", context.ResolveReference(ship.Id));
        }
        context.ReleaseLocationReferences();
        Assert.Null(context.ResolveReference(hangar.Id));
        Assert.Null(context.ResolveReference(ship.Id));
        Assert.Equal("@skills", context.ResolveReference(skills.Id));
        Assert.NotEqual(hangar.Id, context.Reference("@hangar", PilotBindingLifetime.Location).Id);
    }
}
