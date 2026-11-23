using Eden.Gameplay.Agents;
using Eden.Gameplay.Docking;
using Eden.Gameplay.Flight;
using Eden.Gameplay.Market;
using Eden.Protocol.PilotSessions;
using Eden.Protocol.Serialization;

namespace Eden.Protocol.Agents;

internal sealed record MissionEntryPoint(SpacePosition Position) : BlueModel
{
    public override object ToBlue() => new object?[] { Position.X, Position.Y, Position.Z };
}

internal sealed record MissionShipRestrictionsReply(MissionShipRestrictions Restrictions) : BlueModel
{
    public override object ToBlue() =>
        new BlueInstance(
            "utillib.KeyVal",
            new Dictionary<string, object?>
            {
                ["allowedShipTypes"] = new BlueList<int>(Restrictions.AllowedShipTypes),
                ["restrictedShipTypes"] = new BlueList<int>(Restrictions.RestrictedShipTypes),
                ["nonDefaultShipRestrictions"] = Restrictions.NonDefaultShipRestrictions,
            }
        );
}

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

internal sealed record SpaceAgentLocation(CatalogAgent Agent) : BlueModel
{
    public override object ToBlue() =>
        new Dictionary<string, object?>
        {
            ["typeID"] = Agent.Space!.TypeId,
            ["locationID"] = Agent.Id,
            ["solarsystemID"] = Agent.Space.SystemId,
        };
}

internal sealed record MissionConversation(MissionOffer? Offer, string Event, string Greeting) : BlueModel
{
    internal long? ReplayDelay { get; init; }
    internal bool SuppressRequest { get; init; }
    internal IReadOnlyList<EpicArcChoice> EpicChoices { get; init; } = [];

    internal static object?[] Action(MissionOffer offer, MissionAction action) =>
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
            if (Offer.Kind == MissionKind.Choice)
            {
                actions.AddRange(EpicChoices.Select(choice => new EpicArcChoiceLink(Offer, choice)));
            }
            else if (Offer.Kind != MissionKind.AgentHandoff || Offer.EpicArcTargetAgentId == Offer.Agent.Id)
            {
                actions.Add(Action(Offer, MissionAction.Complete));
            }
            actions.Add(Action(Offer, MissionAction.Defer));
            actions.Add(Action(Offer, MissionAction.Quit));
        }
        else if (ReplayDelay is null && !SuppressRequest)
        {
            actions.Add(new object?[] { (int)MissionAction.Request, (int)MissionAction.Request });
        }
        // The legacy dialogue indexes these four keys directly when rendering objectives.
        var last = new Dictionary<string, object?>
        {
            ["missionCompleted"] = false,
            ["missionDeclined"] = false,
            ["missionQuit"] = false,
            ["missionCantReplay"] = (object?)ReplayDelay ?? false,
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
        object?[] message =
            Offer is null || SuppressRequest && Event.Length == 0
                ? [Greeting, null]
                : [Offer.BriefingId == 0 ? Offer.Briefing : Offer.BriefingId, Offer.MissionId];
        return new object?[] { new object?[] { message, actions }, last };
    }
}

internal sealed record MissionKeywords(MissionOffer Offer) : BlueModel
{
    public override object ToBlue()
    {
        long? handoff = Offer.EpicArcTargetStation is int station ? station : Offer.EpicArcTargetAgentId;
        return new Dictionary<string, object?>
        {
            ["dungeonLocationID"] = Offer.SystemId,
            ["dungeonSolarSystemID"] = Offer.SystemId,
            ["objectiveDestinationSystemID"] = Offer.EpicArcTargetSystem ?? Offer.SystemId,
            ["rewardQuantity"] = Offer.Rewards.IskCents / (double)MarketDefinitions.CentsPerIsk,
            ["player"] = Offer.PilotId,
            ["objectiveLocationID"] =
                handoff
                ?? (Offer.Kind == MissionKind.Trade ? Offer.Agent.MissionLocationId : Offer.Cargo?.DestinationStation ?? Offer.SystemId),
            ["objectiveDestinationID"] =
                handoff ?? Offer.Cargo?.DestinationStation ?? (Offer.Retrieval is null ? Offer.SystemId : Offer.Agent.MissionLocationId),
            ["objectivePickupLocationID"] = Offer.Cargo?.PickupStation ?? Offer.Agent.MissionLocationId,
            ["objectiveTypeID"] = Offer.Cargo?.TypeId ?? Offer.Retrieval?.TypeId,
            ["objectiveQuantity"] = Offer.Cargo?.Quantity ?? Offer.Retrieval?.Quantity ?? 0,
            ["agentID"] = Offer.Agent.Id,
            ["agentLocationID"] = Offer.Agent.MissionLocationId,
        };
    }
}

internal sealed record MissionBriefing(MissionOffer Offer, long Now) : BlueModel
{
    public override object ToBlue() =>
        new Dictionary<string, object?>
        {
            ["ContentID"] = Offer.MissionId,
            ["Decline Time"] = Offer.DeclineCooldownEndsAt is long end
                ? end > Now
                    ? end - Now
                    : -1L
                : null,
            ["Expiration Time"] = Offer.NonExpiring ? null : Offer.ExpiresAt,
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
        if (Offer.Kind == MissionKind.Trade)
        {
            return new Dictionary<string, object?>
            {
                ["typeID"] = Offer.Retrieval!.StationTypeId,
                ["locationID"] = Offer.Agent.MissionLocationId,
                ["solarsystemID"] = Offer.Agent.SystemId,
                ["agentID"] = Offer.Agent.Id,
                ["locationType"] = "objective.destination",
                ["locationNumber"] = 0,
            };
        }
        bool dungeon = Offer.DungeonId.HasValue;
        var values = new Dictionary<string, object?>
        {
            ["typeID"] =
                dungeon ? SolarSystemType
                : Pickup ? Offer.Cargo!.PickupTypeId
                : Offer.Cargo!.DestinationTypeId,
            ["locationID"] =
                dungeon ? Offer.SystemId
                : Pickup ? Offer.Cargo!.PickupStation
                : Offer.Cargo!.DestinationStation,
            ["solarsystemID"] = Pickup ? Offer.Cargo!.PickupSystemId : Offer.SystemId,
            ["agentID"] = Offer.Agent.Id,
            ["locationType"] =
                dungeon ? "dungeon"
                : Pickup ? "objective.source"
                : "objective.destination",
            ["locationNumber"] = 0,
        };
        if (dungeon)
        {
            values["coords"] = new object?[] { Offer.Position.X, Offer.Position.Y, Offer.Position.Z };
        }
        return values;
    }
}

internal sealed record MissionObjectives(MissionOffer Offer, long Now) : BlueModel
{
    private const int CreditsType = 29; // Build 3662168 appConst.typeCredits.

    public override object ToBlue()
    {
        List<object?> objectives = [];
        List<object?> dungeons = [];
        List<object?> rewards = [];
        if (Offer.Agent.Type is not (AgentType.Heraldry or AgentType.Research) && Offer.Rewards.IskCents > 0)
        {
            rewards.Add(new object?[] { CreditsType, Offer.Rewards.IskCents / (double)MarketDefinitions.CentsPerIsk, null });
        }
        if (Offer.ItemReward is { } itemReward)
        {
            rewards.Add(new object?[] { itemReward.TypeId, itemReward.Quantity, null });
        }
        List<object?> gifts = [];
        if (Offer.Cargo is { } courierGift)
        {
            gifts.Add(new object?[] { courierGift.TypeId, courierGift.Quantity, null });
        }
        if (Offer.CareerGift is { } careerGift)
        {
            gifts.Add(new object?[] { careerGift.TypeId, careerGift.Quantity, null });
        }
        gifts.AddRange(Offer.AdditionalGifts.Select(gift => new object?[] { gift.TypeId, gift.Quantity, null }));
        List<object?> bonuses = [];
        if (Offer.Bonus is { } duration)
        {
            long time = duration.Remaining(Now);
            long minutes = duration.WindowTicks / TimeSpan.TicksPerMinute;
            if (duration.IskCents > 0)
            {
                bonuses.Add(new object?[] { time, CreditsType, duration.IskCents / (double)MarketDefinitions.CentsPerIsk, null, minutes });
            }
            if (Offer.CareerBonusItem is { } careerBonus)
            {
                bonuses.Add(new object?[] { time, careerBonus.TypeId, careerBonus.Quantity, null, minutes });
            }
        }
        bool combatComplete = Offer.Retrieval is null ? Offer.ObjectiveComplete : Offer.CombatComplete;
        if (
            Offer.Kind == MissionKind.AgentHandoff
            && Offer.EpicArcTargetAgentId is long agent
            && Offer.EpicArcTargetTypeId is int type
            && Offer.EpicArcTargetSystem is int system
        )
        {
            var location = Offer.EpicArcTargetStation is int station
                ? new EpicArcAgentLocation(agent, station, type, system)
                : new EpicArcAgentLocation(agent, system, MissionLocation.SolarSystemType, system);
            objectives.Add(new object?[] { "agent", new object?[] { agent, location } });
        }
        else if (Offer.Cargo is { } cargo)
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
        else if (Offer.DungeonId.HasValue && !Offer.RequiresScanner)
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
            if (Offer.EnemyFactionId > 0)
            {
                dungeon["ownerID"] = Offer.EnemyFactionId;
            }
            if (Offer.HasShipRestrictions)
            {
                dungeon["shipRestrictions"] = 1;
            }
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
                            ["locationID"] = Offer.Agent.MissionLocationId,
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
            ["importantStandings"] = Offer.Storyline || Offer.EpicArcId is not null && Offer.Rewards.FactionFraction > 0,
            ["objectives"] = objectives,
            ["dungeons"] = dungeons,
            ["locations"] = new List<object?> { Offer.EpicArcTargetSystem ?? Offer.SystemId },
            ["agentGift"] = gifts,
            ["normalRewards"] = rewards,
            ["loyaltyPoints"] = Offer.Rewards.LoyaltyPoints,
            // Build 3662168 agentDialogueUtil consumes ticks, type, quantity, extra, and original minutes.
            ["bonusRewards"] = bonuses,
            ["collateral"] = new List<object?>(),
            ["researchPoints"] = Offer.ResearchPoints is double researchPoints ? researchPoints : (object)0,
        };
    }
}

internal sealed record MissionJournalInfo(MissionOffer Offer, long Now) : BlueModel
{
    public override object ToBlue() =>
        new Dictionary<string, object?>
        {
            ["missionNameID"] = Offer.NameId == 0 ? Offer.Name : Offer.NameId,
            ["briefingTextID"] = Offer.BriefingId == 0 ? Offer.Briefing : Offer.BriefingId,
            ["contentID"] = Offer.MissionId,
            ["iconID"] = null,
            ["expirationTime"] = Offer.NonExpiring ? null : Offer.ExpiresAt,
            ["missionState"] = (int)Offer.State,
            ["objectives"] = new MissionObjectives(Offer, Now),
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
                ["hint"] =
                    Offer.Kind == MissionKind.Mining ? "UI/Agents/Bookmarks/Mining/Mining" : "UI/Agents/Bookmarks/Encounter/Deadspace",
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
                            o.EpicArcId.HasValue ? EpicMissionType(o.Kind)
                            : o.Agent.Type == AgentType.Heraldry ? "UI/Agents/MissionTypes/Heraldry"
                            : o.Agent.Type == AgentType.Research
                                ? (
                                    o.Kind == MissionKind.Trade
                                        ? "UI/Agents/MissionTypes/ResearchTrade"
                                        : "UI/Agents/MissionTypes/ResearchCourier"
                                )
                            : o.Kind == MissionKind.Trade
                                ? (o.Storyline ? "UI/Agents/MissionTypes/StorylineTrade" : "UI/Agents/MissionTypes/Trade")
                            : o.Storyline ? "UI/Agents/MissionTypes/StorylineCourier"
                            : o.Kind == MissionKind.Combat ? "UI/Agents/MissionTypes/Encounter"
                            : o.Kind == MissionKind.Mining ? "UI/Agents/MissionTypes/Mining"
                            : "UI/Agents/MissionTypes/Courier",
                            o.NameId,
                            o.Agent.Id,
                            o.NonExpiring ? 0L : o.ExpiresAt,
                            Bookmarks(o),
                            false,
                            o.Kind == MissionKind.Courier,
                            o.MissionId,
                        }
                )
                .ToList(),
            (Research ?? []).Select(p => (object?)new ResearchJournalRow(p)).ToList(),
        };

    private static List<object?> Bookmarks(MissionOffer offer)
    {
        if (offer.EpicArcTargetAgentId.HasValue && offer.Kind == MissionKind.AgentHandoff)
        {
            // The legacy objective uses "objective"; the Opportunities card uses "objective.destination".
            return [new EpicArcHandoffBookmark(offer, "objective"), new EpicArcHandoffBookmark(offer, "objective.destination")];
        }
        return offer.DungeonId.HasValue && !offer.RequiresScanner ? [new MissionBookmark(offer)] : [];
    }

    private static string EpicMissionType(MissionKind kind) =>
        kind switch
        {
            MissionKind.AgentHandoff => "UI/Agents/MissionTypes/EpicArcTalkToAgent",
            MissionKind.Choice => "UI/Agents/MissionTypes/EpicArcAgentInteraction",
            MissionKind.Combat => "UI/Agents/MissionTypes/EpicArcEncounter",
            MissionKind.Mining => "UI/Agents/MissionTypes/EpicArcMining",
            MissionKind.Courier => "UI/Agents/MissionTypes/EpicArcCourier",
            MissionKind.Trade => "UI/Agents/MissionTypes/EpicArcTrade",
            _ => "UI/Agents/MissionTypes/EpicArc",
        };
}
