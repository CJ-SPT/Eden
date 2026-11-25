using Eden.Data.World.Market;
using Eden.Gameplay.Characters;
using Eden.Gameplay.Market;
using Eden.Protocol.Fleets;
using Eden.Protocol.PilotSessions;
using Eden.Protocol.Serialization;
using Eden.Server;
using Eden.World.Runtime;
using Microsoft.Extensions.DependencyInjection;

namespace Eden.Protocol.Tests.PilotSessions;

public sealed partial class CooperativeFleetTests
{
    [Fact]
    public async Task NativeSingleInviteRequotesAndChargesOnceAcrossCheckpoint()
    {
        using TestWorld world = new(liveWorld: true);
        var live = world.Services.GetRequiredService<LiveWorld>();
        long sender;
        long recipient;
        using (FleetClient boss = new(world, 1000001))
        using (FleetClient recruit = new(world, 1000002))
        {
            sender = boss.PilotId;
            recipient = recruit.PilotId;
            using (var action = live.Economy.Begin(new(pilots: [sender, recipient])))
            {
                action.Pilots.Find(recipient)!.CorporationId = 1000035;
                action.Add(new PilotWallet { PilotId = sender, Cents = 6_000 });
                action.Commit();
            }
            long fleet = boss.Create();
            string service = $"{CooperativeFleetHandler.Prefix}{fleet}";
            object?[] arguments = [recipient, null, null, null];
            Assert.Null(recruit.ServiceReply("account", "SetContactCost", [40L]));
            AssertSingleInviteQuote(boss.CallBound(fleet, "Invite", arguments), 40);
            Assert.Null(recruit.ServiceReply("account", "SetContactCost", [70L]));
            Dictionary<string, object?> approval = new() { ["approvedCost"] = 40L };
            AssertSingleInviteQuote(boss.CallService(service, "Invite", arguments, approval), 70);
            approval["approvedCost"] = 70L;
            Assert.IsType<PilotCallResult.Failure>(boss.CallService(service, "Invite", arguments, approval));
            Assert.Equal(60d, boss.ServiceReply("account", "GetCashBalance", []));
            Assert.Empty(FleetInviteJournal(world.Services, sender));
            recruit.Advance();
            Assert.Empty(recruit.Context.Notifications.OfType<FleetInviteNotification>());

            using (var action = live.Economy.Begin(new(pilots: [sender])))
            {
                action.Set<PilotWallet>().Find(sender)!.Cents = 10_000;
                action.Commit();
            }
            Assert.Null(boss.Reply("Invite", arguments, approval));
            Assert.Null(boss.Reply("Invite", arguments, approval));
            recruit.Advance();
            var invitation = Assert.Single(recruit.Context.Notifications.OfType<FleetInviteNotification>());
            Assert.Equal(fleet, invitation.FleetId);
            Assert.Equal(sender, invitation.Inviter);
            Assert.Equal(30d, boss.ServiceReply("account", "GetCashBalance", []));
            recruit.Bind(fleet, ["AcceptInvite", new object?[] { (long)recruit.Context.Pilot.ShipTypeId }, null]);
            Assert.Equal(fleet, recruit.Context.Pilot.Identity.Fleet!.FleetId);
        }
        await live.FlushAsync();
        world.Services.Dispose();
        using var reopened = ServerServices.Build(new(world.Path), liveWorld: true);
        var entry = Assert.Single(FleetInviteJournal(reopened, sender));
        Assert.Equal(WalletEntryType.Cspa, entry.EntryType);
        Assert.Equal(recipient, entry.ReferenceId);
        Assert.Equal(-7_000L, entry.AmountCents);
        Assert.Equal(3_000L, entry.BalanceCents);
    }

    [Fact]
    public void NativeSingleInviteHonorsKnownSenderPolicyAndBlocksWithoutDebiting()
    {
        using TestWorld world = new(liveWorld: true);
        using FleetClient boss = new(world, 1000001);
        using FleetClient recruit = new(world, 1000002);
        long sender = boss.PilotId;
        long recipient = recruit.PilotId;
        using (var action = world.Services.GetRequiredService<LiveWorld>().Economy.Begin(new(pilots: [sender, recipient])))
        {
            action.Pilots.Find(recipient)!.CorporationId = 1000035;
            action.Commit();
        }
        long fleet = boss.Create();
        object?[] arguments = [recipient, null, null, null];
        Assert.Null(recruit.ServiceReply("account", "SetContactCost", [null]));
        Assert.IsType<PilotCallResult.Failure>(boss.CallBound(fleet, "Invite", arguments));

        var contacts = world.Services.GetRequiredService<IPilotContacts>();
        contacts.Add(recipient, sender, 0, false);
        Assert.Null(recruit.ServiceReply("charMgr", "BlockOwners", [new List<object?> { sender }]));
        Assert.IsType<PilotCallResult.Failure>(boss.CallBound(fleet, "Invite", arguments));
        recruit.Advance();
        Assert.Empty(recruit.Context.Notifications.OfType<FleetInviteNotification>());

        Assert.Null(recruit.ServiceReply("charMgr", "UnblockOwners", [new List<object?> { sender }]));
        Assert.Null(boss.Reply("Invite", arguments));
        recruit.Advance();
        Assert.Single(recruit.Context.Notifications.OfType<FleetInviteNotification>());
        Assert.Empty(FleetInviteJournal(world.Services, sender));
    }

    private static void AssertSingleInviteQuote(PilotCallResult result, long cost)
    {
        var quote = Assert.IsType<PilotCallResult.NamedFailure>(result);
        Assert.Equal("ContactCostNotApproved", quote.Name);
        var values = BlueRead.Fields(BlueMarshal.Decode(BlueMarshal.Encode(quote.Values)));
        Assert.Equal(cost, values["totalCost"]);
        Assert.EndsWith(" ISK", BlueMarshal.Text(values["totalCostISK"]));
    }
}
