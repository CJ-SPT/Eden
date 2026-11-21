using Eden.Gameplay.Drones;
using Eden.Gameplay.Flight;
using Eden.Infrastructure.Registration;
using Eden.Protocol.Docking;
using Eden.Protocol.PilotSessions;
using Eden.Protocol.Serialization;
using Eden.Protocol.Targeting;

namespace Eden.Protocol.Fighters;

[Injectable(InjectionType.Scoped)]
internal sealed partial class FighterDeckHandler(
    IPilotFighterDeck deck,
    IPilotFighterOrders orders,
    TargetingHandler targeting,
    TimeProvider clock
) : ISelectedPilotCallHandler
{
    public PilotCallResult Call(
        PilotCallContext context,
        string service,
        string method,
        object?[] args,
        Dictionary<string, object?>? keywords = null
    )
    {
        if (service != "fighterMgr" || keywords is { Count: > 0 })
        {
            return PilotCallResult.Unsupported;
        }
        var pilot = context.Pilot;
        if (method == "GetFightersForShip" && args.Length == 0)
        {
            return new FighterDeckReply(
                FighterDeckRules.Supports(pilot.ShipGroupId, pilot.ShipTypeId) ? deck.Read(pilot.PilotId, pilot.ShipId) : new([], [])
            );
        }
        if (
            !FighterDeckRules.Supports(pilot.ShipGroupId, pilot.ShipTypeId)
            || (pilot.Position is not null && !context.Flight.CanUseSelfModules)
        )
        {
            return false;
        }
        try
        {
            if (
                method
                is "LaunchFightersFromTubes"
                    or "RecallFightersToTubes"
                    or "CmdActivateAbilitySlots"
                    or "CmdDeactivateAbilitySlots"
                    or "ExecuteMovementCommandOnFighters"
            )
            {
                return Command(context, method, args);
            }
            var change = (method, args) switch
            {
                ("LoadFightersToTube", [long stack, long flag]) when stack > 0 && flag is >= int.MinValue and <= int.MaxValue => deck.Load(
                    pilot.PilotId,
                    pilot.ShipId,
                    stack,
                    (int)flag
                ),
                ("UnloadTubeToFighterBay", [long flag]) when flag is >= int.MinValue and <= int.MaxValue => deck.Unload(
                    pilot.PilotId,
                    pilot.ShipId,
                    (int)flag
                ),
                _ => null,
            };
            if (change is null)
            {
                return PilotCallResult.Unsupported;
            }
            foreach (var item in change.Inventory)
            {
                InventoryChangesWire.NotifyItemChange(context, item, beforeFlight: true);
            }
            if (change.Tube is { } tube)
            {
                context.NotificationsBeforeFlight.Add(new FighterTubeContent(tube));
            }
            else
            {
                context.NotificationsBeforeFlight.Add(new FighterTubeEmpty(change.Flag));
            }
            context.NotificationsBeforeFlight.Add(new FighterTubeStatus(change.Flag, change.Tube is null ? "EMPTY" : "READY"));
            return true;
        }
        catch (Exception error) when (error is InvalidDataException or OverflowException)
        {
            return method is "LoadFightersToTube" or "UnloadTubeToFighterBay" ? false : new PilotCallResult.Failure(error.Message);
        }
    }
}

internal sealed record FighterDeckReply(FighterDeckSnapshot State) : BlueModel
{
    public override object ToBlue() =>
        new object?[]
        {
            State.Tubes.Select(tube => (object?)FighterTubeContent.Fields(tube)).ToList(),
            State.Squadrons.Select(squad => (object?)new object?[] { squad.Flag, squad.SquadronId, squad.TypeId, squad.Count }).ToList(),
            BlueEmpty.Dictionary.ToBlue(),
        };
}

internal sealed record FighterTubeContent(FighterTube Tube) : PilotNotification("OnFighterTubeContentUpdate")
{
    internal static object?[] Fields(FighterTube tube) => [tube.Flag, tube.SquadronId, tube.TypeId, tube.Count];

    public override object?[] Arguments => Fields(Tube);
}

internal sealed record FighterTubeEmpty(int Flag) : PilotNotification("OnFighterTubeContentEmpty")
{
    public override object?[] Arguments => [Flag];
}

internal sealed record FighterTubeStatus(int Flag, string State) : PilotNotification("OnFighterTubeTaskStatus")
{
    public override object?[] Arguments => [Flag, State, null, null, null];
}
