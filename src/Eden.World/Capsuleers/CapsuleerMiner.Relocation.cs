using Eden.Gameplay.Capsuleers;
using Eden.World.Runtime.Economy;

namespace Eden.World.Capsuleers;

internal sealed partial class CapsuleerMiner
{
    internal void ChangeCrew(IReadOnlyCollection<long> members) => crew = members;

    private bool TryCorporationRelocation()
    {
        if (_operation.World is not EconomyTransaction world || CapsuleerCorporations.Organization(world, pilotId).Relocation is null)
        {
            return false;
        }
        FinishCorporationRelocation();
        if (CapsuleerCorporations.StartRelocation(world, _operation.Catalog, Pilot, Now) is not { } route)
        {
            return false;
        }
        _work = Work with { WorkingBelt = 0, LeaveAfterCycle = false };
        UndockMiner();
        RefreshObjects();
        BeginRelocationLeg(route.Systems, route.DestinationStationId);
        return true;
    }

    private bool ContinueCorporationRelocation(WorldAction world)
    {
        if (CapsuleerCorporations.Organization(world, pilotId).Relocation is not { } move || move.ShipId != Pilot.ShipId)
        {
            return false;
        }
        BeginRelocationLeg(move.Systems, move.DestinationId);
        return true;
    }

    private void BeginRelocationLeg(int[] systems, int destination)
    {
        int index = Array.IndexOf(systems, Pilot.SystemId);
        if (index < 0)
        {
            throw new InvalidDataException("The miner is outside its relocation route.");
        }
        if (index + 1 < systems.Length)
        {
            var gate = _objects.First(body => body.DestinationSystemId == systems[index + 1]);
            StartCourse(gate, MinerActivity.FlyingToGate);
        }
        else
        {
            StartCourse(_objects.First(body => body.Id == destination), MinerActivity.Returning);
        }
    }

    private void FinishCorporationRelocation()
    {
        if (_operation.World is EconomyTransaction world && CapsuleerCorporations.FinishRelocation(world, _operation.Catalog, Pilot, Now))
        {
            _work = Work with { OperatingStationId = Pilot.StationId, AvoidedBelts = [], StrippedBelts = [], StarterOreTypes = [] };
        }
    }
}
