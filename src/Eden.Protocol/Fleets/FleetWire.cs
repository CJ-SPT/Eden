using Eden.Gameplay.Fleets;
using Eden.Protocol.PilotSessions;
using Eden.Protocol.Serialization;

namespace Eden.Protocol.Fleets;

internal sealed record FleetOptionsReply(FleetOptions Options) : BlueModel
{
    public override object ToBlue() =>
        new BlueInstance(
            "util.KeyVal",
            new Dictionary<string, object?>
            {
                ["isFreeMove"] = Options.FreeMove,
                ["isRegistered"] = Options.Registered,
                ["autoJoinSquadID"] = Options.AutoJoinSquadId,
            }
        );
}

internal sealed record FleetCompositionReply(FleetCompositionMember Member) : BlueModel
{
    public override object ToBlue() =>
        new BlueInstance(
            "util.KeyVal",
            new Dictionary<string, object?>
            {
                ["characterID"] = Member.PilotId,
                ["solarSystemID"] = Member.SystemId,
                ["stationID"] = Member.DockedLocationId,
                ["shipTypeID"] = Member.ShipTypeId,
                ["skills"] = Member.Leadership is { } levels
                    ? new List<object?> { levels.Leadership, levels.WingCommand, levels.FleetCommand }
                    : new List<object?>(),
                ["skillIDs"] = Member.Leadership is not null
                    ? new List<object?>
                    {
                        (int)FleetLeadershipSkill.Leadership,
                        (int)FleetLeadershipSkill.WingCommand,
                        (int)FleetLeadershipSkill.FleetCommand,
                    }
                    : new List<object?>(),
            }
        );
}

internal sealed record FleetMemberReply(FleetMember Member) : BlueModel
{
    internal static BlueInstance OptOuts(FleetMember member) =>
        new(
            "util.KeyVal",
            new Dictionary<string, object?>
            {
                ["acceptsFleetWarp"] = member.TakesFleetWarp,
                ["acceptsConduitJumps"] = member.AcceptsConduitJumps,
                ["acceptsFleetRegroups"] = member.AcceptsFleetRegroups,
            }
        );

    public override object ToBlue() =>
        new BlueInstance(
            "util.KeyVal",
            new Dictionary<string, object?>
            {
                ["charID"] = Member.Pilot.Id,
                ["characterID"] = Member.Pilot.Id,
                ["corpID"] = Member.Pilot.CorporationId,
                ["allianceID"] = Member.Pilot.AllianceId,
                ["wingID"] = Member.Placement.WingId,
                ["squadID"] = Member.Placement.SquadId,
                ["role"] = (int)Member.Placement.Role,
                ["job"] = Member.Job,
                ["shipTypeID"] = Member.Pilot.ShipTypeId,
                ["solarSystemID"] = Member.Pilot.SystemId,
                ["memberOptOuts"] = OptOuts(Member),
            }
        );
}

internal sealed record FleetWingsReply(IReadOnlyList<FleetWing> Wings) : BlueModel
{
    public override object ToBlue() =>
        Wings.ToDictionary(
            w => w.Id,
            w =>
                (object?)
                    new BlueInstance(
                        "util.KeyVal",
                        new Dictionary<string, object?>
                        {
                            ["name"] = w.Name,
                            ["squads"] = w.Squads.ToDictionary(
                                s => s.Id,
                                s => (object?)new BlueInstance("util.KeyVal", new Dictionary<string, object?> { ["name"] = s.Name })
                            ),
                        }
                    )
        );
}

internal sealed record FleetInitReply(CooperativeFleet Fleet) : BlueModel
{
    public override object ToBlue() =>
        new BlueInstance(
            "util.KeyVal",
            new Dictionary<string, object?>
            {
                ["fleetID"] = Fleet.Id,
                ["members"] = Fleet.Members.ToDictionary(m => m.Pilot.Id, m => (object?)new FleetMemberReply(m)),
                ["wings"] = new FleetWingsReply(Fleet.Wings),
                ["options"] = new FleetOptionsReply(Fleet.Options),
                ["motd"] = Fleet.Motd,
            }
        );
}

internal abstract record FleetNotification(string EventName) : PilotNotification(EventName);

internal sealed record FleetInviteNotification(long FleetId, long Inviter) : FleetNotification("OnFleetInvite")
{
    public override object?[] Arguments => [FleetId, Inviter, "AskJoinFleet", new Dictionary<string, object?> { ["autoAccept"] = false }];
}

internal sealed record FleetJoinNotification(FleetMember Member) : FleetNotification("OnFleetJoin")
{
    public override object?[] Arguments => [new FleetMemberReply(Member)];
}

internal sealed record FleetLeaveNotification(long PilotId) : FleetNotification("OnFleetLeave")
{
    public override object?[] Arguments => [PilotId];
}

internal sealed record FleetMemberNotification(long FleetId, FleetMember Before, FleetMember After, bool OnlyMember)
    : FleetNotification("OnFleetMemberChanged")
{
    public override object?[] Arguments =>
        [
            After.Pilot.Id,
            FleetId,
            Before.Placement.WingId,
            Before.Placement.SquadId,
            (int)Before.Placement.Role,
            Before.Job,
            FleetMemberReply.OptOuts(Before),
            After.Placement.WingId,
            After.Placement.SquadId,
            (int)After.Placement.Role,
            After.Job,
            FleetMemberReply.OptOuts(After),
            OnlyMember,
        ];
}

internal sealed record FleetHierarchyNotification(FleetEvent.Hierarchy Change)
    : FleetNotification(
        Change.Name is not null
            ? Change.SquadId.HasValue
                ? "OnFleetSquadNameChanged"
                : "OnFleetWingNameChanged"
            : Change.SquadId.HasValue
                ? Change.Deleted
                    ? "OnFleetSquadDeleted"
                    : "OnFleetSquadAdded"
                : Change.Deleted
                    ? "OnFleetWingDeleted"
                    : "OnFleetWingAdded"
    )
{
    public override object?[] Arguments =>
        Change.Name is not null ? [Change.SquadId ?? Change.WingId, Change.Name]
        : Change.SquadId is { } squad ? [Change.WingId, squad]
        : [Change.WingId];
}

internal sealed record FleetOptionsNotification(FleetOptions Before, FleetOptions After) : FleetNotification("OnFleetOptionsChanged")
{
    public override object?[] Arguments => [new FleetOptionsReply(Before), new FleetOptionsReply(After)];
}

internal sealed record FleetMotdNotification(string Text) : FleetNotification("OnFleetMotdChanged")
{
    public override object?[] Arguments => [Text];
}

internal sealed record FleetBroadcastNotification(FleetBroadcast Broadcast) : FleetNotification("OnFleetBroadcast")
{
    public override object?[] Arguments =>
        [
            Broadcast.Kind.ToString(),
            (int)Broadcast.Scope,
            Broadcast.Sender.Id,
            Broadcast.Sender.SystemId,
            Broadcast.ItemId,
            Broadcast.TypeId,
        ];
}

internal sealed record FleetModuleBeaconChanged(Eden.Gameplay.Flight.CynosuralField Field, bool Active)
    : FleetNotification("OnFleetJumpBeaconModuleChange")
{
    public override object?[] Arguments => [Field.PilotId, Field.SystemId, Field.Beacon.Id, Field.Beacon.TypeId, Active];
}

internal sealed record FleetDeployableBeaconChanged(long Id, Eden.Gameplay.Flight.CynosuralField Field, bool Active)
    : FleetNotification("OnFleetJumpBeaconDeployableChange")
{
    public override object?[] Arguments => [Id, Field.SystemId, Field.Beacon.Id, Field.PilotId, Active];
}

internal sealed record FleetPortalChanged(Eden.Gameplay.Flight.JumpPortal Portal, bool Active) : FleetNotification("OnBridgeModeChange")
{
    public override object?[] Arguments => [Portal.ShipId, Portal.Target.SystemId, Portal.Target.BeaconId, Active];
}

internal sealed record FleetStateNotification(IReadOnlyList<FleetTargetTag> Tags) : FleetNotification("OnFleetStateChange")
{
    public override object?[] Arguments =>
        [
            new BlueInstance(
                "util.KeyVal",
                new Dictionary<string, object?> { ["targetTags"] = Tags.ToDictionary(tag => tag.ItemId, tag => (object?)tag.Tag) }
            ),
        ];
}

internal sealed record FleetLocationsReply(IReadOnlyList<FleetMember> Members) : BlueModel
{
    public override object ToBlue() =>
        Members.ToDictionary(
            m => m.Pilot.Id,
            m =>
                (object?)
                    new BlueInstance(
                        "util.KeyVal",
                        new Dictionary<string, object?>
                        {
                            ["charID"] = m.Pilot.Id,
                            ["solarSystemID"] = m.Pilot.SystemId,
                            ["shipTypeID"] = m.Pilot.ShipTypeId,
                            ["pos"] = new object?[] { m.Pilot.Position!.X, m.Pilot.Position.Y, m.Pilot.Position.Z },
                        }
                    )
        );
}
