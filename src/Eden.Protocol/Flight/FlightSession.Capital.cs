using Eden.Gameplay.Combat;
using Eden.Gameplay.Flight;
using Eden.Gameplay.Inventory;
using Eden.Gameplay.Ships;

namespace Eden.Protocol.Flight;

internal sealed partial class FlightSession
{
    internal CapitalJump? TakeConduitArrival() => space.TakeConduitArrival(Pilot.ShipId);

    internal bool IndustrialProtectionActive => capitalTravel?.IndustrialProtectionActive(Pilot.ShipId) == true;

    internal bool CapitalModuleActive =>
        capitalTravel?.Field(Pilot.PilotId)?.ShipId == Pilot.ShipId
        || capitalTravel?.Portal(Pilot.PilotId)?.ShipId == Pilot.ShipId
        || Pilot.ShipGroupId is (int)HullClass.Titan or (int)HullClass.LancerDreadnought
            && capitalTravel?.DoomsdayMovementLocked(Pilot.ShipId) == true
        || CapitalModeRules.CanUseMode(Pilot.ShipGroupId) && capitalTravel?.Mode(Pilot.ShipId) is not null;

    private bool BastionActive =>
        Pilot.ShipGroupId == (int)HullClass.Marauder && capitalTravel?.Mode(Pilot.ShipId)?.Effect == DogmaEffect.Bastion;

    internal bool MobileSiege =>
        Pilot.ShipTypeId == CapitalModeRules.Sarathiel
        && capitalTravel?.Field(Pilot.PilotId)?.ShipId != Pilot.ShipId
        && capitalTravel?.Mode(Pilot.ShipId)?.Effect == DogmaEffect.CapitalSiege;

    private bool CanNavigateInSiege(FlightCommand command) =>
        command
            is FlightCommand.Heading
                or FlightCommand.AlignTo
                or FlightCommand.AlignToBookmark
                or FlightCommand.AlignToPoint
                or FlightCommand.Follow
                or FlightCommand.Speed
        && MobileSiege;

    internal void RefreshDoomsdayFields(IReadOnlyList<DoomsdayFieldContact> contacts) =>
        Scene.RefreshDoomsdayFields(SceneContext, contacts);

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

    internal void StopForBastion()
    {
        ReleaseTether();
        StopForCapitalModule();
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
