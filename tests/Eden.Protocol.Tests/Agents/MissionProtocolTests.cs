using Eden.Data.World.Characters;
using Eden.Data.World.Inventory;
using Eden.Data.World.Market;
using Eden.Gameplay.Agents;
using Eden.Gameplay.Fleets;
using Eden.Protocol.Agents;
using Eden.Protocol.Characters;
using Eden.Protocol.PilotSessions;
using Eden.Protocol.Serialization;
using Eden.World.Runtime;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Eden.Protocol.Tests.Agents;

public sealed class MissionProtocolTests
{
    [Theory]
    [InlineData("IsCheatingWithAgent", false)]
    [InlineData("IsCheatingWithAgent", true)]
    [InlineData("ShouldAlwaysAllowReplay", false)]
    [InlineData("ShouldAlwaysAllowReplay", true)]
    public void OpportunitiesThreeDotMenuReadsDisabledCheatsWithoutChangingMissions(string method, bool accepted)
    {
        using MissionFixture f = new();
        string service = $"@agent:{MissionFixture.AgentId}";
        Assert.False(Assert.IsType<bool>(MissionFixture.Reply(f.Call(service, method, []))));
        Assert.Empty(f.Missions.Journal(MissionFixture.PilotId));
        var mission = f.Request();
        if (accepted)
        {
            mission = f.Accept(mission);
        }
        for (int cycle = 0; cycle < 2; cycle++)
        {
            Assert.False(Assert.IsType<bool>(MissionFixture.Reply(f.Call(service, method, [], new() { ["machoVersion"] = 1L }))));
        }
        foreach (object?[] args in new object?[][] { [null], [MissionFixture.PilotId], [false] })
        {
            Assert.IsType<PilotCallResult.Failure>(f.Call(service, method, args));
        }
        Assert.IsType<PilotCallResult.Failure>(f.Call(service, method, [], new() { ["enabled"] = true }));
        Assert.IsType<PilotCallResult.Failure>(f.Call($"@agent:{MissionFixture.PilotId}", method, []));
        Assert.Equal(mission, f.Missions.Current(MissionFixture.PilotId, MissionFixture.AgentId));
        Assert.Empty(f.Context.Notifications);
    }

    [Theory]
    [InlineData(MissionRules.SecurityDivision, false)]
    [InlineData(MissionRules.SecurityDivision, true)]
    [InlineData(MissionRules.DistributionDivision, false)]
    [InlineData(MissionRules.DistributionDivision, true)]
    public void OpportunitiesObjectiveReadsAcceptNativeKeywordsAndPreserveMissionState(int division, bool accepted)
    {
        using MissionFixture f = new(division: division);
        string service = $"@agent:{MissionFixture.AgentId}";
        Dictionary<string, object?> keywords = new() { ["ignoreLocateCheck"] = true };
        Assert.Empty(BlueRead.Fields(MissionFixture.Reply(f.Call(service, "GetMissionObjectiveInfo", [], keywords))));
        Assert.Empty(f.Missions.Journal(MissionFixture.PilotId));
        var mission = f.Request();
        if (accepted)
        {
            mission = f.Accept(mission);
        }
        var expected = MissionFixture.Reply(f.Call(service, "GetMissionObjectiveInfo", []));
        var opportunities = MissionFixture.Reply(f.Call(service, "GetMissionObjectiveInfo", [], keywords));
        Assert.Equal(BlueMarshal.Encode(expected), BlueMarshal.Encode(opportunities));
        foreach (bool ignoreLocateCheck in new[] { false, true })
        {
            foreach (object? character in new object?[] { null, MissionFixture.PilotId })
            {
                keywords = new()
                {
                    ["ignoreLocateCheck"] = ignoreLocateCheck,
                    ["charID"] = character,
                    ["machoVersion"] = 1L,
                };
                var native = MissionFixture.Reply(f.Call(service, "GetMissionObjectiveInfo", [], keywords));
                Assert.Equal(BlueMarshal.Encode(expected), BlueMarshal.Encode(native));
            }
        }
        Assert.Equal(mission, f.Missions.Current(MissionFixture.PilotId, MissionFixture.AgentId));
        Assert.Empty(f.Context.Notifications);
    }

    [Fact]
    public void OpportunitiesObjectiveKeywordsRejectForeignPilotsMalformedValuesAndOtherMethods()
    {
        using MissionFixture f = new();
        var mission = f.Accept(f.Request());
        string service = $"@agent:{MissionFixture.AgentId}";
        foreach (
            var keywords in new Dictionary<string, object?>[]
            {
                new() { ["ignoreLocateCheck"] = true, ["charID"] = MissionFixture.PilotId + 1 },
                new() { ["ignoreLocateCheck"] = true, ["charID"] = MissionFixture.PilotId.ToString() },
                new() { ["ignoreLocateCheck"] = true, ["charID"] = true },
                new() { ["ignoreLocateCheck"] = null },
                new() { ["ignoreLocateCheck"] = 1L },
                new() { ["ignoreLocateCheck"] = "true" },
                new() { ["ignoreLocateCheck"] = true, ["reward"] = 1L },
            }
        )
        {
            Assert.IsType<PilotCallResult.Failure>(f.Call(service, "GetMissionObjectiveInfo", [], keywords));
        }
        Dictionary<string, object?> valid = new() { ["ignoreLocateCheck"] = true };
        foreach (string method in new[] { "DoAction", "GetMissionBriefingInfo", "GetAgentLocationWrap", "RemoveOfferFromJournal" })
        {
            Assert.IsType<PilotCallResult.Failure>(f.Call(service, method, [], valid));
        }
        Assert.IsType<PilotCallResult.Failure>(f.Call("agentMgr", "GetMyJournalDetails", [], valid));
        Assert.IsType<PilotCallResult.Failure>(f.Call(service, "GetMissionObjectiveInfo", [MissionFixture.PilotId], valid));
        Assert.IsType<PilotCallResult.Failure>(f.Call($"@agent:{MissionFixture.PilotId}", "GetMissionObjectiveInfo", [], valid));
        Assert.Equal(mission, f.Missions.Current(MissionFixture.PilotId, MissionFixture.AgentId));
        Assert.Empty(f.Context.Notifications);
    }

    [Theory]
    [InlineData(MissionRules.SecurityDivision)]
    [InlineData(MissionRules.DistributionDivision)]
    public void TrackerRefreshAndReadsUseNativeShapesWithoutChangingMissionState(int division)
    {
        using MissionFixture f = new(division: division);
        List<object?> agents = [MissionFixture.AgentId, MissionFixture.StorylineId];
        Assert.Null(MissionFixture.Reply(f.Call("missionTrackerMgr", "UpdateAllMissions", [agents], new())));
        Assert.Empty(f.Missions.Journal(MissionFixture.PilotId));
        var accepted = f.Accept(f.Request());
        for (int cycle = 0; cycle < 2; cycle++)
        {
            Assert.Null(MissionFixture.Reply(f.Call("missionTrackerMgr", "UpdateAllMissions", [agents])));
            Assert.Null(MissionFixture.Reply(f.Call("missionTrackerMgr", "UpdateAllMissions", [new List<object?>()])));
            var current = Assert.IsType<List<object?>>(
                MissionFixture.Reply(f.Call("missionTrackerMgr", "GetMissionInfoItems", [MissionFixture.AgentId]))
            );
            Assert.Equal(division == MissionRules.SecurityDivision ? "TravelTo" : "TransportItemsMissing", BlueMarshal.Text(current[0]));
            Assert.NotEmpty(
                Assert.IsType<List<object?>>(
                    MissionFixture.Reply(f.Call("missionTrackerMgr", "GetAllMissionObjectives", [MissionFixture.AgentId]))
                )
            );
            Assert.False(
                Assert.IsType<bool>(MissionFixture.Reply(f.Call("missionTrackerMgr", "IsInActiveDungeonID", [MissionFixture.AgentId])))
            );
        }
        foreach (string method in new[] { "GetMissionInfoItems", "GetAllMissionObjectives", "IsInActiveDungeonID" })
        {
            foreach (object?[] args in new object?[][] { [], [null], [MissionFixture.PilotId], ["3012018"], [MissionFixture.AgentId, 1L] })
            {
                Assert.IsType<PilotCallResult.Failure>(f.Call("missionTrackerMgr", method, args));
            }
            Assert.IsType<PilotCallResult.Failure>(
                f.Call("missionTrackerMgr", method, [MissionFixture.AgentId], new() { ["pilotID"] = MissionFixture.PilotId })
            );
        }
        foreach (
            object?[] args in new object?[][]
            {
                [],
                [MissionFixture.AgentId],
                [new object?[] { MissionFixture.AgentId }],
                [new List<object?> { MissionFixture.AgentId, MissionFixture.PilotId }],
                [new List<object?> { MissionFixture.AgentId, null }],
            }
        )
        {
            Assert.IsType<PilotCallResult.Failure>(f.Call("missionTrackerMgr", "UpdateAllMissions", args));
        }
        Assert.IsType<PilotCallResult.Failure>(
            f.Call("missionTrackerMgr", "UpdateAllMissions", [agents], new() { ["pilotID"] = MissionFixture.PilotId })
        );
        Assert.Same(PilotCallResult.Unsupported, f.Call("missionTrackerMgr", "UnknownTrackerMethod", []));
        Assert.Equal(accepted, f.Missions.Current(MissionFixture.PilotId, MissionFixture.AgentId));
        Assert.All(f.Context.Notifications, notification => Assert.IsType<MissionsUpdated>(notification));
        var objectives = BlueRead.Fields(MissionFixture.Reply(f.Call($"@agent:{MissionFixture.AgentId}", "GetMissionObjectiveInfo", [])));
        Assert.Equal((long)accepted.MissionId, objectives["contentID"]);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CombatObjectiveOmitsUnavailableNativeOptionalFieldsAndOnlyReportsSettledCompletion(bool completed)
    {
        using MissionFixture f = new(division: MissionRules.SecurityDivision);
        var offer = f.Request() with { ObjectiveComplete = completed };
        var objectives = BlueRead.Fields(MissionFixture.Reply(new MissionObjectives(offer)));
        var dungeon = BlueRead.Fields(Assert.Single(Assert.IsType<List<object?>>(objectives["dungeons"])));
        // The native renderer branches on key presence, then uses restrictions as
        // a list index and treats completionStatus=0 as a failed, struck-out site.
        Assert.DoesNotContain("shipRestrictions", dungeon.Keys);
        Assert.DoesNotContain("briefingMessage", dungeon.Keys);
        Assert.DoesNotContain("ownerID", dungeon.Keys);
        if (completed)
        {
            Assert.Equal(1L, dungeon["completionStatus"]);
            Assert.Equal(1L, dungeon["objectiveCompleted"]);
        }
        else
        {
            Assert.DoesNotContain("completionStatus", dungeon.Keys);
            Assert.Null(dungeon["objectiveCompleted"]);
        }
    }

    [Fact]
    public void OrdinaryAgentConversationProvidesNativeLocationHeaderAndEveryObjectiveStatusFlag()
    {
        using MissionFixture f = new(division: MissionRules.SecurityDivision);
        var binding = Assert.IsType<BoundPilotReply>(
            Assert
                .IsType<PilotCallResult.Success>(
                    f.Call("agentMgr", "MachoBindObject", [MissionFixture.AgentId, new object?[] { "DoAction", new object?[] { null } }])
                )
                .Reply
        );
        string service = f.Context.ResolveReference(binding.Reference.Id)!;
        var initialLocation = BlueRead.Fields(MissionFixture.Reply(f.Call(service, "GetAgentLocationWrap", [])));
        Assert.Equal((long)MissionFixture.StationId, initialLocation["locationID"]);
        Assert.Empty(f.Missions.Journal(MissionFixture.PilotId));
        var greeting = Conversation(new PilotCallResult.Success(binding.Reply));
        var request = Assert.IsType<object?[]>(Assert.Single(greeting.Actions));
        var offered = Conversation(f.Call(service, "DoAction", [request[0]]));
        var location = BlueRead.Fields(MissionFixture.Reply(f.Call(service, "GetAgentLocationWrap", [])));
        Assert.Equal(52678L, location["typeID"]);
        Assert.Equal((long)MissionFixture.StationId, location["locationID"]);
        Assert.Equal(30000142L, location["solarsystemID"]);
        Assert.DoesNotContain("coords", location.Keys);
        foreach (string flag in new[] { "missionCompleted", "missionDeclined", "missionQuit", "missionCantReplay" })
        {
            Assert.Equal(false, offered.Last[flag]);
        }
        Assert.NotEmpty(BlueRead.Fields(MissionFixture.Reply(f.Call(service, "GetMissionObjectiveInfo", []))));
        Assert.IsType<PilotCallResult.Failure>(f.Call(service, "GetAgentLocationWrap", [MissionFixture.PilotId]));
        Assert.Single(f.Missions.Journal(MissionFixture.PilotId));
        Assert.Equal(MissionState.Offered, f.Missions.Current(MissionFixture.PilotId, MissionFixture.AgentId)!.State);
    }

    [Theory]
    [InlineData("", null)]
    [InlineData("offered", "missionOffered")]
    [InlineData("accepted", "missionAccepted")]
    [InlineData("completed", "missionCompleted")]
    [InlineData("offer_declined", "missionDeclined")]
    [InlineData("quit", "missionQuit")]
    public void ConversationStatusFlagsPreserveTheNativeObjectiveVisibilityContract(string action, string? activeFlag)
    {
        var conversation = Conversation(new MissionConversation(null, action, "Greeting"));
        foreach (string flag in new[] { "missionCompleted", "missionDeclined", "missionQuit", "missionCantReplay" })
        {
            Assert.Equal(flag == activeFlag, conversation.Last[flag]);
        }
        if (activeFlag is not null)
        {
            Assert.Equal(true, conversation.Last[activeFlag]);
        }
    }

    [Fact]
    public void StationAgentPanelCanBindAndReadReplayTimersBeforeAnyMissionOfferExists()
    {
        using MissionFixture f = new();
        foreach (long agent in new[] { MissionFixture.AgentId, MissionFixture.StorylineId, Persistence.AgentTestData.OtherAgent })
        {
            var bound = Assert.IsType<BoundPilotReply>(
                Assert
                    .IsType<PilotCallResult.Success>(
                        f.Call(
                            "agentMgr",
                            "MachoBindObject",
                            [agent, new object?[] { "GetReplayTimestamp", new object?[] { MissionFixture.PilotId } }]
                        )
                    )
                    .Reply
            );
            Assert.Null(MissionFixture.Reply(new PilotCallResult.Success(bound.Reply)));
            string service = f.Context.ResolveReference(bound.Reference.Id)!;
            Assert.Null(MissionFixture.Reply(f.Call(service, "GetReplayTimestamp", [MissionFixture.PilotId])));
            Assert.IsType<PilotCallResult.Failure>(f.Call(service, "GetReplayTimestamp", [MissionFixture.PilotId + 1]));
        }
        Assert.Empty(f.Missions.Journal(MissionFixture.PilotId));
    }

    [Fact]
    public void BoundAgentRequestAcceptanceJournalAndNativeObjectiveShapesUseCatalogIds()
    {
        using MissionFixture f = new();
        var binding = Assert.IsType<BoundPilotReply>(
            Assert
                .IsType<PilotCallResult.Success>(
                    f.Call("agentMgr", "MachoBindObject", [MissionFixture.AgentId, new object?[] { "DoAction", new object?[] { null } }])
                )
                .Reply
        );
        string service = f.Context.ResolveReference(binding.Reference.Id)!;
        Assert.Equal($"@agent:{MissionFixture.AgentId}", service);
        var requested = Conversation(f.Call(service, "DoAction", [(long)MissionAction.Request]));
        var message = Assert.IsType<object?[]>(requested.Message);
        Assert.Equal(2912L, message[1]);
        var accept = Assert.IsType<object?[]>(requested.Actions[0]);
        Assert.Equal((long)MissionAction.Accept, accept[1]);
        Assert.Equal(35L, accept[0]);
        Assert.Equal(true, requested.Last["missionOffered"]);
        _ = Conversation(f.Call(service, "DoAction", [accept[0]]));
        Assert.Contains(f.Context.Notifications, n => n is AgentMissionChanged { Action: "accepted" });
        var journal = Assert.IsType<object?[]>(MissionFixture.Reply(f.Call("agentMgr", "GetMyJournalDetails", [])));
        var row = Assert.IsType<object?[]>(Assert.Single(Assert.IsType<List<object?>>(journal[0])));
        Assert.Equal(10, row.Length);
        Assert.Equal((long)MissionState.Accepted, row[0]);
        Assert.Equal(MissionFixture.AgentId, row[4]);
        Assert.Equal(2912L, row[9]);
        var objective = BlueRead.Fields(MissionFixture.Reply(f.Call(service, "GetMissionObjectiveInfo", [])));
        Assert.Equal(2912L, objective["contentID"]);
        Assert.Equal(1000L, objective["loyaltyPoints"]);
        var rewards = Assert.IsType<List<object?>>(objective["normalRewards"]);
        var isk = Assert.IsType<object?[]>(Assert.Single(rewards));
        Assert.Equal(29L, isk[0]);
        Assert.Equal(200000d, isk[1]);
        var transport = Assert.IsType<object?[]>(Assert.Single(Assert.IsType<List<object?>>(objective["objectives"])));
        Assert.Equal("transport", BlueMarshal.Text(transport[0]));
        Assert.Equal(5, Assert.IsType<object?[]>(transport[1]).Length);
        var briefing = BlueRead.Fields(MissionFixture.Reply(f.Call(service, "GetMissionBriefingInfo", [])));
        Assert.Equal(2912L, briefing["ContentID"]);
        Assert.NotNull(briefing["AcceptTimestamp"]);
        Assert.IsType<PilotCallResult.Failure>(f.Call(service, "GetMissionKeywords", [2913L]));
    }

    [Fact]
    public void CompletionNotifiesWalletLpStandingsAndJournalOnlyAfterCommittedSettlement()
    {
        using MissionFixture f = new();
        var offer = f.Accept(f.Request());
        _ = f.Context;
        using (WorldDbContext world = new(f.Db.Path))
        {
            var cargo = world.Set<OwnedItem>().Single(i => i.TypeId == offer.Cargo!.TypeId);
            cargo.LocationId = MissionFixture.DestinationId;
            world.SaveChanges();
        }
        f.Dock(MissionFixture.DestinationId);
        string service = $"@agent:{MissionFixture.AgentId}";
        long token = offer.Id * MissionRules.ActionStride + (int)MissionAction.Complete;
        var complete = Conversation(f.Call(service, "DoAction", [token]));
        Assert.Equal(true, complete.Last["missionCompleted"]);
        Assert.Equal(2, f.Context.Notifications.OfType<NpcStandingSet>().Count());
        Assert.Contains(f.Context.Notifications, n => n.Name == "OnAccountChange");
        Assert.Contains(f.Context.Notifications, n => n.Name == "OnLPChange");
        Assert.Contains(f.Context.Notifications, n => n is AgentMissionChanged { Action: "completed" });
        int notifications = f.Context.Notifications.Count;
        Assert.IsType<PilotCallResult.Failure>(f.Call(service, "DoAction", [token]));
        Assert.Equal(notifications, f.Context.Notifications.Count);
    }

    [Fact]
    public async Task FleetTurnInSplitsResidentRewardsOnceAndPersistsBothShares()
    {
        using MissionFixture f = new(live: true);
        var offer = f.Accept(f.Request());
        var live = f.Services.GetRequiredService<LiveWorld>();
        var owner = f.Context.Pilot;
        var member = f.Db.Store.FindPilot(1000002)!;
        var fleets = f.Services.GetRequiredService<ICooperativeFleets>();
        fleets.Update(Guid.NewGuid(), new(owner.PilotId, owner.CorpId, owner.ShipId, owner.ShipTypeId, owner.SystemId, null));
        fleets.Update(Guid.NewGuid(), new(member.PilotId, member.CorpId, member.ShipId, member.ShipTypeId, member.SystemId, null));
        var fleet = fleets.Create(owner.PilotId);
        fleets.Invite(owner.PilotId, fleet.Id, member.PilotId, fleet.Members[0].Placement with { Role = FleetRole.Member });
        fleets.Accept(member.PilotId, fleet.Id);
        using (var action = live.Economy.Begin(new(pilots: [owner.PilotId])))
        {
            var cargo = action.Set<OwnedItem>().Single(item => item.OwnerId == owner.PilotId && item.TypeId == offer.Cargo!.TypeId);
            cargo.LocationId = MissionFixture.DestinationId;
            action.Commit();
        }
        f.Dock(MissionFixture.DestinationId);
        string service = $"@agent:{MissionFixture.AgentId}";
        long token = offer.Id * MissionRules.ActionStride + (int)MissionAction.Complete;
        var cancelled = Assert.IsType<PilotCallResult.ClientCall>(f.Call(service, "DoAction", [token]));
        Assert.Equal("SingleChoiceBox", cancelled.Dialog.Method);
        Assert.IsType<PilotCallResult.Success>(cancelled.Dialog.Complete(new object?[] { false, null }));
        Assert.Equal(MissionState.Accepted, f.Missions.Current(owner.PilotId, offer.Agent.Id)!.State);
        var prompt = Assert.IsType<PilotCallResult.ClientCall>(f.Call(service, "DoAction", [token]));
        object?[] answer = [true, "radioboxOption1Selected"];
        Assert.IsType<PilotCallResult.Success>(prompt.Dialog.Complete(answer));
        Assert.IsType<PilotCallResult.Failure>(prompt.Dialog.Complete(answer));
        Assert.Equal(offer.Rewards.IskCents / 2, live.WalletBalance(member.PilotId));
        Assert.Equal(offer.Rewards.IskCents - offer.Rewards.IskCents / 2, live.WalletBalance(owner.PilotId));
        await live.FlushAsync();
        f.StopResident();
        using WorldDbContext saved = new(f.Db.Path);
        var payments = saved.Set<SavedMarketPayment>().Where(payment => payment.OrderId == offer.Id).ToArray();
        Assert.Equal(2, payments.Length);
        Assert.Equal(offer.Rewards.IskCents, payments.Sum(payment => payment.AmountCents));
        Assert.Equal(
            offer.Rewards.LoyaltyPoints,
            saved
                .Set<SavedLoyaltyBalance>()
                .Where(balance => balance.IssuerCorporationId == offer.Agent.CorporationId)
                .Sum(balance => balance.Points)
        );
        Assert.Equal(
            MissionRules.Gain(0, offer.Rewards.CorporationFraction / 2),
            saved.Set<SavedNpcStanding>().Find(member.PilotId, (long)offer.Agent.CorporationId)!.Value,
            10
        );
        Assert.Null(saved.Set<SavedNpcStanding>().Find(member.PilotId, offer.Agent.Id));
    }

    [Fact]
    public void ForgedBindingActionsCharacterFiltersAndKeywordsAreRejected()
    {
        using MissionFixture f = new();
        Assert.IsType<PilotCallResult.Failure>(f.Call("agentMgr", "MachoBindObject", [MissionFixture.PilotId, null]));
        Assert.IsType<PilotCallResult.Failure>(f.Call("agentMgr", "MachoBindObject", [new object?[] { MissionFixture.AgentId }, null]));
        string service = $"@agent:{MissionFixture.AgentId}";
        Assert.IsType<PilotCallResult.Failure>(f.Call(service, "DoAction", [(long)MissionAction.Accept]));
        Assert.IsType<PilotCallResult.Failure>(f.Call(service, "DoAction", [-1L]));
        Assert.IsType<PilotCallResult.Failure>(f.Call(service, "DoAction", [2L], new() { ["reward"] = 1_000_000_000L }));
        var offer = f.Request();
        Assert.IsType<PilotCallResult.Failure>(f.Call(service, "GetMissionJournalInfo", [MissionFixture.PilotId + 1, offer.MissionId]));
        Assert.IsType<PilotCallResult.Failure>(f.Call(service, "GetMissionObjectiveInfo", [MissionFixture.PilotId + 1]));
        Assert.IsType<PilotCallResult.Failure>(f.Call($"@agent:{MissionFixture.PilotId}", "DoAction", [null]));
        Assert.IsType<PilotCallResult.Failure>(f.Call(service, "DoAction", [offer.Id * MissionRules.ActionStride + 31]));
    }

    [Fact]
    public void CombatBookmarkAndObjectiveExposeNativeMissionWarpWithoutGrantingWarpForAnOffer()
    {
        using MissionFixture f = new(division: MissionRules.SecurityDivision);
        var offer = f.Request();
        string service = $"@agent:{MissionFixture.AgentId}";
        Assert.IsType<PilotCallResult.Failure>(f.Call(service, "WarpToLocation", ["dungeon", 0L, 0d, false, null, null]));
        f.Accept(offer);
        var journal = Assert.IsType<object?[]>(MissionFixture.Reply(f.Call("agentMgr", "GetMyJournalDetails", [])));
        var row = Assert.IsType<object?[]>(Assert.Single(Assert.IsType<List<object?>>(journal[0])));
        var bookmark = Assert.IsType<BlueInstance>(Assert.Single(Assert.IsType<List<object?>>(row[6])));
        var fields = BlueRead.Fields(bookmark.State);
        Assert.Equal(MissionFixture.AgentId, fields["agentID"]);
        Assert.Equal("dungeon", BlueMarshal.Text(fields["locationType"]));
        var objectives = BlueRead.Fields(MissionFixture.Reply(f.Call(service, "GetMissionObjectiveInfo", [])));
        var dungeon = BlueRead.Fields(Assert.Single(Assert.IsType<List<object?>>(objectives["dungeons"])));
        Assert.Equal(188L, dungeon["dungeonID"]);
        Assert.Null(dungeon["objectiveCompleted"]);
        Assert.Equal(30000142L, BlueRead.Fields(dungeon["location"])["solarsystemID"]);
        // Docking rejects the actual warp through the ordinary flight owner.
        Assert.IsType<PilotCallResult.Failure>(f.Call(service, "WarpToLocation", ["dungeon", 0L, 0d, false, null, null]));
        Assert.IsType<PilotCallResult.Success>(
            f.Context.Flight.Execute(new Eden.Gameplay.Flight.FlightCommand.Undock(f.Context.Pilot.ShipId))
        );
        f.Context.Flight.Snapshot();
        Assert.IsType<PilotCallResult.Success>(f.Call(service, "WarpToLocation", ["dungeon", 0L, 0d, false, null, null]));
    }

    private static (object? Message, List<object?> Actions, Dictionary<string, object?> Last) Conversation(PilotCallResult result)
    {
        var reply = Assert.IsType<object?[]>(MissionFixture.Reply(result));
        var dialogue = Assert.IsType<object?[]>(reply[0]);
        return (dialogue[0], Assert.IsType<List<object?>>(dialogue[1]), BlueRead.Fields(reply[1]));
    }
}
