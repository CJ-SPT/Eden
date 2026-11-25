using Eden.Data.World.Market;
using Eden.Gameplay.Characters;
using Eden.Gameplay.Corporations;
using Eden.Gameplay.Fleets;
using Eden.Gameplay.Market;
using Eden.Protocol.Fleets;
using Eden.Protocol.PilotSessions;
using Eden.Protocol.Serialization;
using Eden.Server;
using Eden.Server.Launcher;
using Eden.World.Runtime;
using Eden.World.Storage;
using Microsoft.Extensions.DependencyInjection;

namespace Eden.Protocol.Tests.PilotSessions;

public sealed partial class CooperativeFleetTests
{
    [Fact]
    public void NativeMassInviteDeliversEligiblePilotsAndReportsPartialFailure()
    {
        using TestWorld world = new(liveWorld: true);
        TestCatalog.AddSchool(new WorldDatabase(world.Path).CatalogPath, 18, 1, 1, 30001392, 60015159, 601);
        long recruitAccount = AddFleetRecruit(world, "Fleet Recruit");
        long wingmateAccount = AddFleetRecruit(world, "Fleet Wingmate");
        using FleetClient boss = new(world, 1000001);
        using FleetClient member = new(world, 1000002);
        using FleetClient recruit = new(world, recruitAccount);
        using FleetClient wingmate = new(world, wingmateAccount);
        long fleet = Join(boss, member);
        long wing = Assert.IsType<long>(boss.Reply("CreateWing", []));
        long squad = Assert.IsType<long>(boss.Reply("CreateSquad", [wing]));
        object?[] batch =
        [
            new List<object?> { boss.PilotId, recruit.PilotId, member.PilotId, recruit.PilotId, wingmate.PilotId, long.MaxValue },
            wing,
            squad,
            (long)FleetRole.Member,
        ];
        var denied = Assert.IsType<PilotCallResult.NamedFailure>(member.CallBound(fleet, "MassInvite", batch));
        Assert.Equal("FleetNotAllowedMany", denied.Name);
        recruit.Advance();
        wingmate.Advance();
        Assert.Empty(recruit.Context.Notifications.OfType<FleetInviteNotification>());
        Assert.Empty(wingmate.Context.Notifications.OfType<FleetInviteNotification>());

        var partial = Assert.IsType<PilotCallResult.NamedFailure>(boss.CallBound(fleet, "MassInvite", batch));
        Assert.Equal("FleetErrorMany", partial.Name);
        var values = BlueRead.Fields(BlueMarshal.Decode(BlueMarshal.Encode(partial.Values)));
        Assert.Equal(new object?[] { member.PilotId }, Assert.IsType<List<object?>>(values["notInvited"]));
        foreach (var recipient in new[] { recruit, wingmate })
        {
            recipient.Advance();
            var invite = Assert.Single(recipient.Context.Notifications.OfType<FleetInviteNotification>());
            Assert.Equal(fleet, invite.FleetId);
            Assert.Equal(boss.PilotId, invite.Inviter);
            recipient.Bind(fleet, ["AcceptInvite", new object?[] { (long)recipient.Context.Pilot.ShipTypeId }, null]);
            var identity = recipient.Context.Pilot.Identity.Fleet!;
            Assert.Equal(wing, identity.WingId);
            Assert.Equal(squad, identity.SquadId);
            Assert.Equal(FleetRole.Member, identity.Role);
        }
    }

    [Fact]
    public async Task NativeMassInviteRequotesAndCommitsTheWalletOnceAcrossReopen()
    {
        using TestWorld world = new(liveWorld: true);
        long sender = 90000001;
        long guest = 90000002;
        var live = world.Services.GetRequiredService<LiveWorld>();
        using (var action = live.Economy.Begin(new(pilots: [sender, guest])))
        {
            action.Pilots.Find(guest)!.CorporationId = 1000035;
            action.Add(new PilotWallet { PilotId = sender, Cents = 10_000 });
            action.Commit();
        }
        using (FleetClient boss = new(world, 1000001))
        using (FleetClient recruit = new(world, 1000002))
        {
            long fleet = boss.Create();
            object?[] batch = [new List<object?> { recruit.PilotId, long.MaxValue }, null, null, null];
            Assert.Null(recruit.ServiceReply("account", "SetContactCost", [40L]));
            AssertFleetInviteQuote(boss.CallBound(fleet, "MassInvite", batch), recruit.PilotId, 40);
            Assert.Null(recruit.ServiceReply("account", "SetContactCost", [60L]));
            var approved = new Dictionary<string, object?> { ["approvedCost"] = 40L };
            string service = $"{CooperativeFleetHandler.Prefix}{fleet}";
            AssertFleetInviteQuote(boss.CallService(service, "MassInvite", batch, approved), recruit.PilotId, 60);
            recruit.Advance();
            Assert.Empty(recruit.Context.Notifications.OfType<FleetInviteNotification>());
            Assert.Equal(100d, boss.ServiceReply("account", "GetCashBalance", []));
            Assert.Empty(FleetInviteJournal(world.Services, sender));

            approved["approvedCost"] = 60L;
            Assert.Equal(new object?[] { recruit.PilotId }, Assert.IsType<List<object?>>(boss.Reply("MassInvite", batch, approved)));
            Assert.Empty(Assert.IsType<List<object?>>(boss.Reply("MassInvite", batch, approved)));
            recruit.Advance();
            Assert.Single(recruit.Context.Notifications.OfType<FleetInviteNotification>());
            Assert.Equal(40d, boss.ServiceReply("account", "GetCashBalance", []));
        }
        await live.FlushAsync();
        world.Services.Dispose();
        using var reopened = ServerServices.Build(new(world.Path), liveWorld: true);
        var entry = Assert.Single(FleetInviteJournal(reopened, sender));
        Assert.Equal(WalletEntryType.Cspa, entry.EntryType);
        Assert.Equal(guest, entry.ReferenceId);
        Assert.Equal(-6_000L, entry.AmountCents);
        Assert.Equal(4_000L, entry.BalanceCents);
    }

    private static long AddFleetRecruit(TestWorld world, string name)
    {
        long account = world.Services.GetRequiredService<LauncherStore>().Register(new(name, "Fleet Recruit Password 1")).AccountId;
        world
            .Services.GetRequiredService<ICharacterCreation>()
            .Create(account, new(name, 1, 1, 0, CharacterRules.UnspecifiedAncestry, [], [], 18));
        return account;
    }

    private static IReadOnlyList<CorporationWalletEntry> FleetInviteJournal(IServiceProvider services, long pilot) =>
        services.GetRequiredService<ICorporationWallet>().Journal(pilot, false, (int)WalletAccountKey.Cash);

    private static void AssertFleetInviteQuote(PilotCallResult result, long pilot, long cost)
    {
        var failure = Assert.IsType<PilotCallResult.NamedFailure>(result);
        Assert.Equal("ContactCostNotApprovedForMany", failure.Name);
        var values = BlueRead.Fields(BlueMarshal.Decode(BlueMarshal.Encode(failure.Values)));
        var charge = BlueRead.Fields(Assert.Single(Assert.IsType<List<object?>>(values["costNotApprovedFor"])));
        Assert.Equal(pilot, charge["charID"]);
        Assert.Equal(cost, charge["totalCost"]);
        Assert.Equal(0L, charge["approvedCost"]);
    }
}
