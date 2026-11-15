using Eden.Gameplay.Exploration;
using Eden.Gameplay.Flight;
using Eden.Infrastructure.Registration;
using Eden.Protocol.Docking;
using Eden.Protocol.PilotSessions;

namespace Eden.Protocol.Exploration;

[Injectable(InjectionType.Scoped)]
internal sealed class AbyssalHandler(IAbyssalDeadspace abyss, ISystemObjects systems) : ISelectedPilotCallHandler
{
    internal void Advance(PilotCallContext context) => context.Flight.RefreshAbyssal(abyss.Objects(context.Pilot.SystemId));

    public PilotCallResult Call(
        PilotCallContext context,
        string service,
        string method,
        object?[] args,
        Dictionary<string, object?>? keywords = null
    )
    {
        if (service != "abyssalMgr")
        {
            return PilotCallResult.Unsupported;
        }
        if (keywords is { Count: > 0 })
        {
            return new PilotCallResult.Failure("Unexpected Abyssal arguments.");
        }
        try
        {
            if (method == "ClientIsReady" && args.Length == 0)
            {
                if (abyss.Read(context.Pilot.PilotId) is { } active)
                {
                    Notify(context, active);
                }
                return PilotCallResult.None;
            }
            context.Flight.Checkpoint();
            if (!context.Flight.CanUseSelfModules || context.Flight.InWarp || context.Flight.PreparingWarp)
            {
                return new PilotCallResult.Failure("Wait until your ship is ready to enter the Abyssal conduit.");
            }
            AbyssalTransition moved;
            if (method == "AbyssalEntranceDeployment" && args is [long item, long mode])
            {
                moved = abyss.Deploy(context.Pilot.PilotId, context.Pilot.ShipId, item, (AbyssalMode)mode);
            }
            else if (
                method is "AbyssalGateActivation" or "AbyssalEndGateActivation" or "AbyssalEntranceGateActivation"
                && args is [long gate]
            )
            {
                moved = abyss.Activate(context.Pilot.PilotId, context.Pilot.ShipId, gate);
            }
            else
            {
                return new PilotCallResult.Failure("Invalid Abyssal request.");
            }
            var objects = AbyssalRules.IsSystem(moved.Pilot.SystemId)
                ? abyss.Objects(moved.Pilot.SystemId)
                : systems.Load(moved.Pilot.SystemId);
            context.Flight.RelocateAbyssal(moved.Pilot, objects);
            context.ReleaseLocationReferences();
            foreach (var change in moved.Items)
            {
                InventoryChangesWire.NotifyItemChange(context, change);
            }
            Notify(context, moved.Run);
            return PilotCallResult.None;
        }
        catch (InvalidDataException error)
        {
            return new PilotCallResult.Failure(error.Message);
        }
    }

    private static void Notify(PilotCallContext context, AbyssalRun run)
    {
        if (run.Status == AbyssalStatus.Active)
        {
            context.Notifications.Add(new AbyssalLocationEntered(run));
            context.Notifications.Add(new AbyssalDeadline(run.ExpiresAt));
        }
        else
        {
            context.Notifications.Add(new AbyssalFinished(run.Id));
        }
    }
}

internal sealed record AbyssalLocationEntered(AbyssalRun Run) : PilotNotification("OnAbyssalLocationEntered")
{
    // Build 3662168 abyssal_service consumes a dictionary, including one-based location_id.
    public override object?[] Arguments =>
        [
            new Dictionary<string, object?>
            {
                ["content_id"] = Run.Id,
                ["location_id"] = Run.Room + 1,
                ["difficulty_tier"] = Run.Tier,
                ["nebula_graphic_id"] = Run.NebulaGraphicId,
                ["num_rooms"] = AbyssalRules.RoomCount,
            },
        ];
}

internal sealed record AbyssalDeadline(long EndsAt) : PilotNotification("OnAddExpiryDurationForAbyssalContent")
{
    public override object?[] Arguments => [EndsAt];
}

internal sealed record AbyssalFinished(long RunId) : PilotNotification("OnAbyssalContentFinished")
{
    // Build 3662168 crimewatchConst normal-PvE Abyssal timer.
    private const int AbyssalTimer = 11;
    public override object?[] Arguments => [RunId, AbyssalTimer];
}
