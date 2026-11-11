using Eden.Gameplay.Agents;
using Eden.Gameplay.Docking;
using Eden.Gameplay.Market;
using Eden.Protocol.PilotSessions;
using Eden.Protocol.Serialization;

namespace Eden.Protocol.Agents;

internal sealed record AgentMissionChanged(string Action, long AgentId) : PilotNotification("OnAgentMissionChange")
{
    public override object?[] Arguments => [Action, AgentId];
}

internal sealed record AgentLocation(StationDirectoryEntry Station) : BlueModel
{
    public override object ToBlue() =>
        new Dictionary<string, object?>
        {
            ["typeID"] = Station.TypeId,
            ["locationID"] = Station.Id,
            ["solarsystemID"] = Station.SystemId,
        };
}

internal sealed record MissionConversation(MissionOffer? Offer, string Event, string Greeting) : BlueModel
{
    private static object?[] Action(MissionOffer offer, MissionAction action) =>
        [checked(offer.Id * MissionRules.ActionStride + (int)action), (int)action];

    public override object ToBlue()
    {
        List<object?> actions = [];
        if (Offer?.State == MissionState.Offered)
        {
            actions.Add(Action(Offer, MissionAction.Accept));
            actions.Add(Action(Offer, MissionAction.Defer));
            actions.Add(Action(Offer, MissionAction.Decline));
        }
        else if (Offer?.State == MissionState.Accepted)
        {
            actions.Add(Action(Offer, MissionAction.Complete));
            actions.Add(Action(Offer, MissionAction.Defer));
            actions.Add(Action(Offer, MissionAction.Quit));
        }
        else
        {
            actions.Add(new object?[] { (int)MissionAction.Request, (int)MissionAction.Request });
        }
        // The legacy dialogue indexes these four keys directly when rendering objectives.
        var last = new Dictionary<string, object?>
        {
            ["missionCompleted"] = false,
            ["missionDeclined"] = false,
            ["missionQuit"] = false,
            ["missionCantReplay"] = false,
        };
        if (Event.Length != 0)
        {
            string? key = Event switch
            {
                "offered" => "missionOffered",
                "accepted" => "missionAccepted",
                "completed" => "missionCompleted",
                "offer_declined" => "missionDeclined",
                "quit" => "missionQuit",
                "failed" => "missionFailed",
                _ => null,
            };
            if (key is not null)
            {
                last[key] = true;
            }
        }
        // Pinned OldMissionController.update_mission unpacks ((message, actions), lastActionInfo).
        // A message is (localization ID or literal text, catalog mission ID).
        object?[] message = Offer is null ? [Greeting, null] : [Offer.BriefingId == 0 ? Offer.Briefing : Offer.BriefingId, Offer.MissionId];
        return new object?[] { new object?[] { message, actions }, last };
    }
}

internal sealed record MissionKeywords(MissionOffer Offer) : BlueModel
{
    public override object ToBlue() =>
        new Dictionary<string, object?>
        {
            ["dungeonLocationID"] = Offer.SystemId,
            ["dungeonSolarSystemID"] = Offer.SystemId,
            ["objectiveDestinationSystemID"] = Offer.SystemId,
            ["rewardQuantity"] = Offer.Rewards.IskCents / (double)MarketDefinitions.CentsPerIsk,
            ["player"] = Offer.PilotId,
            ["objectiveLocationID"] = Offer.Cargo?.DestinationStation ?? Offer.SystemId,
            ["objectiveDestinationID"] =
                Offer.Cargo?.DestinationStation ?? (Offer.Retrieval is null ? Offer.SystemId : Offer.Agent.StationId),
            ["objectivePickupLocationID"] = Offer.Cargo?.PickupStation ?? Offer.Agent.StationId,
            ["objectiveTypeID"] = Offer.Cargo?.TypeId ?? Offer.Retrieval?.TypeId,
            ["objectiveQuantity"] = Offer.Cargo?.Quantity ?? Offer.Retrieval?.Quantity ?? 0,
            ["agentID"] = Offer.Agent.Id,
            ["agentLocationID"] = Offer.Agent.StationId,
        };
}

internal sealed record MissionBriefing(MissionOffer Offer) : BlueModel
{
    public override object ToBlue() =>
        new Dictionary<string, object?>
        {
            ["ContentID"] = Offer.MissionId,
            ["Decline Time"] = null,
            ["Expiration Time"] = Offer.ExpiresAt,
            ["Mission Title ID"] = Offer.NameId == 0 ? Offer.Name : Offer.NameId,
            ["Mission Briefing ID"] = Offer.BriefingId == 0 ? Offer.Briefing : Offer.BriefingId,
            ["Mission Keywords"] = new MissionKeywords(Offer),
            ["Mission Image"] = null,
            ["AcceptTimestamp"] = Offer.AcceptedAt,
        };
}

internal sealed record MissionLocation(MissionOffer Offer, bool Pickup = false) : BlueModel
{
    internal const int SolarSystemType = 5; // Build 3662168 appConst.typeSolarSystem.

    public override object ToBlue()
    {
        bool combat = Offer.Kind == MissionKind.Combat;
        var values = new Dictionary<string, object?>
        {
            ["typeID"] =
                combat ? SolarSystemType
                : Pickup ? Offer.Cargo!.PickupTypeId
                : Offer.Cargo!.DestinationTypeId,
            ["locationID"] =
                combat ? Offer.SystemId
                : Pickup ? Offer.Cargo!.PickupStation
                : Offer.Cargo!.DestinationStation,
            ["solarsystemID"] = Pickup ? Offer.Cargo!.PickupSystemId : Offer.SystemId,
            ["agentID"] = Offer.Agent.Id,
            ["locationType"] =
                combat ? "dungeon"
                : Pickup ? "objective.source"
                : "objective.destination",
            ["locationNumber"] = 0,
        };
        if (combat)
        {
            values["coords"] = new object?[] { Offer.Position.X, Offer.Position.Y, Offer.Position.Z };
        }
        return values;
    }
}

internal sealed record MissionObjectives(MissionOffer Offer) : BlueModel
{
    private const int CreditsType = 29; // Build 3662168 appConst.typeCredits.

    public override object ToBlue()
    {
        List<object?> objectives = [];
        List<object?> dungeons = [];
        bool combatComplete = Offer.Retrieval is null ? Offer.ObjectiveComplete : Offer.CombatComplete;
        if (Offer.Cargo is { } cargo)
        {
            objectives.Add(
                new object?[]
                {
                    "transport",
                    new object?[]
                    {
                        false,
                        new MissionLocation(Offer, true),
                        false,
                        new MissionLocation(Offer),
                        new Dictionary<string, object?>
                        {
                            ["typeID"] = cargo.TypeId,
                            ["quantity"] = cargo.Quantity,
                            ["volume"] = cargo.Volume * cargo.Quantity,
                            ["hasCargo"] = Offer.ObjectiveComplete,
                        },
                    },
                }
            );
        }
        else
        {
            var dungeon = new Dictionary<string, object?>
            {
                ["dungeonID"] = Offer.DungeonId,
                ["location"] = new MissionLocation(Offer),
                ["objectiveCompleted"] = combatComplete ? 1 : null,
                ["optional"] = false,
            };
            // Pinned agentDialogueUtil._ProcessDungeonData branches on key presence:
            // shipRestrictions must be an integer index, and completionStatus=0
            // marks a failed, struck-out site. Omit unavailable optional values.
            if (combatComplete)
            {
                dungeon["completionStatus"] = 1;
            }
            dungeons.Add(dungeon);
        }
        if (Offer.Retrieval is { } retrieval)
        {
            // Build 3662168 agentDialogueUtil._ProcessObjectiveEntry unpacks fetch as
            // (dropoffOwnerID, dropoffLocation, cargo), alongside a separate dungeon entry.
            objectives.Add(
                new object?[]
                {
                    "fetch",
                    new object?[]
                    {
                        Offer.Agent.Id,
                        new Dictionary<string, object?>
                        {
                            ["typeID"] = retrieval.StationTypeId,
                            ["locationID"] = Offer.Agent.StationId,
                            ["solarsystemID"] = Offer.Agent.SystemId,
                            ["agentID"] = Offer.Agent.Id,
                            ["locationType"] = "objective.destination",
                            ["locationNumber"] = 0,
                        },
                        new Dictionary<string, object?>
                        {
                            ["typeID"] = retrieval.TypeId,
                            ["quantity"] = retrieval.Quantity,
                            ["volume"] = retrieval.Volume * retrieval.Quantity,
                            ["hasCargo"] = retrieval.HasCargo,
                        },
                    },
                }
            );
        }
        return new Dictionary<string, object?>
        {
            ["contentID"] = Offer.MissionId,
            ["missionState"] = (int)Offer.State,
            ["missionTitleID"] = Offer.NameId == 0 ? Offer.Name : Offer.NameId,
            ["completionStatus"] = Offer.ObjectiveComplete ? 1 : 0,
            ["importantStandings"] = Offer.Storyline,
            ["objectives"] = objectives,
            ["dungeons"] = dungeons,
            ["locations"] = new List<object?> { Offer.SystemId },
            ["agentGift"] = Offer.Cargo is { } gift
                ? new List<object?> { new object?[] { gift.TypeId, gift.Quantity, null } }
                : new List<object?>(),
            ["normalRewards"] = new List<object?>
            {
                new object?[] { CreditsType, Offer.Rewards.IskCents / (double)MarketDefinitions.CentsPerIsk, null },
            },
            ["loyaltyPoints"] = Offer.Rewards.LoyaltyPoints,
            ["bonusRewards"] = new List<object?>(),
            ["collateral"] = new List<object?>(),
            ["researchPoints"] = 0,
        };
    }
}

internal sealed record MissionJournalInfo(MissionOffer Offer) : BlueModel
{
    public override object ToBlue() =>
        new Dictionary<string, object?>
        {
            ["missionNameID"] = Offer.NameId == 0 ? Offer.Name : Offer.NameId,
            ["briefingTextID"] = Offer.BriefingId == 0 ? Offer.Briefing : Offer.BriefingId,
            ["contentID"] = Offer.MissionId,
            ["iconID"] = null,
            ["expirationTime"] = Offer.ExpiresAt,
            ["missionState"] = (int)Offer.State,
            ["objectives"] = new MissionObjectives(Offer),
        };
}

internal sealed record MissionBookmark(MissionOffer Offer) : BlueModel
{
    public override object ToBlue() =>
        new BlueInstance(
            "utillib.KeyVal",
            new Dictionary<string, object?>
            {
                ["agentID"] = Offer.Agent.Id,
                ["bookmarkID"] = MissionRules.FirstSiteId + Offer.Id,
                ["locationID"] = Offer.SystemId,
                ["itemID"] = Offer.SystemId,
                ["typeID"] = MissionLocation.SolarSystemType,
                ["locationType"] = "dungeon",
                ["locationNumber"] = 0,
                ["solarsystemID"] = Offer.SystemId,
                ["hint"] = "UI/Agents/Bookmarks/Encounter/Deadspace",
                ["memo"] = Offer.Name,
                ["x"] = Offer.Position.X,
                ["y"] = Offer.Position.Y,
                ["z"] = Offer.Position.Z,
            }
        );
}

internal sealed record MissionJournal(IReadOnlyList<MissionOffer> Offers, IReadOnlyList<ResearchProject>? Research = null) : BlueModel
{
    public override object ToBlue() =>
        new object?[]
        {
            Offers
                .Select(o =>
                    (object?)
                        new object?[]
                        {
                            (int)o.State,
                            o.Storyline,
                            o.Storyline ? "UI/Agents/MissionTypes/StorylineCourier"
                            : o.Kind == MissionKind.Combat ? "UI/Agents/MissionTypes/Encounter"
                            : "UI/Agents/MissionTypes/Courier",
                            o.NameId,
                            o.Agent.Id,
                            o.ExpiresAt,
                            o.Kind == MissionKind.Combat
                                ? new List<object?> { new MissionBookmark(o) }
                                : new List<object?>(),
                            false,
                            o.Kind == MissionKind.Courier,
                            o.MissionId,
                        }
                )
                .ToList(),
            (Research ?? []).Select(p => (object?)new ResearchJournalRow(p)).ToList(),
        };
}
