using Eden.Gameplay.Combat;
using Eden.Gameplay.Flight;

namespace Eden.Protocol.Flight;

internal sealed partial class FlightSession
{
    internal bool CapitalModuleActive =>
        capitalTravel?.Field(Pilot.PilotId)?.ShipId == Pilot.ShipId
        || capitalTravel?.Portal(Pilot.PilotId)?.ShipId == Pilot.ShipId
        || CapitalTravelRules.CombatCapital(Pilot.ShipGroupId) && capitalTravel?.Mode(Pilot.ShipId) is not null;

    internal void RefreshCynos()
    {
        if (capitalTravel is { } travel)
        {
            var contacts = travel.Fields(Pilot.SystemId).Select(field => field.Beacon);
            if (Pilot.Position is { } position)
            {
                contacts = contacts
                    .Where(body => body.IsGlobal || position.DistanceTo(body.Position) <= NpcEncounterDefinitions.VisibilityRange)
                    .Concat(
                        travel
                            .Deployables(Pilot.SystemId)
                            .Where(body => position.DistanceTo(body.Position) <= NpcEncounterDefinitions.VisibilityRange)
                    );
            }
            else
            {
                contacts = contacts.Where(body => body.IsGlobal);
            }
            Scene.RefreshCynos(SceneContext, contacts.ToArray());
        }
    }

    internal void StopForCapitalModule()
    {
        _navigation = new();
        Update(new DestinyCommand.Stop(Pilot.ShipId));
        Update(new DestinyCommand.SetBallVelocity(Pilot.ShipId, 0, 0, 0));
        PublishMotion();
        space.Publish(CreateContact());
    }

    internal void StartCapitalJump(IPilotCapitalTravel travel, CapitalJumpRequest request, ISystemObjects systems)
    {
        if (!CanUseSelfModules || InWarp || PreparingWarp || MicroJumpSpooling || Cloaked)
        {
            throw new InvalidDataException("Wait until your active ship can jump.");
        }
        long pilotId = Pilot.PilotId;
        long shipId = Pilot.ShipId;
        travel.CheckJump(pilotId, shipId, request);
        StartSpaceTransfer(
            new(
                () =>
                {
                    var moved = travel.Jump(pilotId, shipId, request);
                    return new(moved.Pilot, moved.Changes, moved.Timers, moved.Capacitor);
                },
                request.SystemId,
                systems.Load(request.SystemId),
                0,
                Capital: true
            )
        );
    }
}
