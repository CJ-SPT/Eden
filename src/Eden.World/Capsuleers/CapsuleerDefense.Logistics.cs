using Eden.Data.World.Inventory;
using Eden.Data.World.Propulsion;
using Eden.Gameplay.Capsuleers;
using Eden.Gameplay.Combat;
using Eden.Gameplay.Docking;
using Eden.Gameplay.Dogma;
using Eden.Gameplay.Inventory;
using Eden.Gameplay.Repair;
using Eden.Gameplay.Targeting;
using Eden.World.Runtime;

namespace Eden.World.Capsuleers;

internal sealed partial class CapsuleerDefense
{
    private sealed record RepairRequest(long PilotId, long ShipId, int TypeId, HullDamage Remaining, double Distance);

    private CapsuleerDefenseResult AdvanceLogistics(
        LiveWorld world,
        DockedPilot pilot,
        CapsuleerDefenseState state,
        RoamingGang gang,
        long now
    )
    {
        if (CapsuleerDefenseRules.Retreat(health.Read(pilot.PilotId, now), true))
        {
            Stop(pilot.PilotId, state);
            return new(pilot, null, false, true, false);
        }
        var profile = Profile(pilot.PilotId);
        var modules = profile
            .Modules.Where(module => PvpSupport.RemoteRepair(module.Effect))
            .Select(module => RemoteRepairRules.Define(module.Item.Id, module.Item.TypeId, module.Effect, module.Attributes))
            .ToArray();
        if (modules.Length == 0)
        {
            Stop(pilot.PilotId, state);
            return new(pilot, null, false, false, false);
        }
        var playerJams = warfare.Incoming(pilot.ShipId);
        var npcJams = npcWarfare.Incoming(pilot.ShipId);
        // A fleet member's damage read represents their repair request. Opponents' health stays hidden here.
        List<RepairRequest> requests = [];
        foreach (var member in gang.Members)
        {
            if (
                member.PilotId == pilot.PilotId
                || member.Status != RoamingGangMemberStatus.Active
                || !CapsuleerCorporations.CanFlyTogether(world, pilot, member.PilotId)
                || !PlayerEwarRules.CanTarget(playerJams, member.ShipId)
                || !NpcEwarRules.CanTarget(npcJams, member.ShipId)
                || presence.Find(pilot.SystemId, member.ShipId) is not { } contact
                || world.Economy.Peek<Ship>(member.ShipId) is not { } ship
            )
            {
                continue;
            }
            double radius = database.Catalog.Items.GetValueOrDefault(ship.TypeId)?.Radius ?? 0;
            double distance = Math.Max(0, pilot.Position!.DistanceTo(contact.Position) - pilot.Flight.ShipRadius - radius);
            if (distance > Math.Min(profile.TargetRange, NpcEncounterDefinitions.VisibilityRange))
            {
                continue;
            }
            var status = health.Read(member.PilotId, now);
            if (!status.Remaining.Destroyed && status.ShipId == member.ShipId)
            {
                requests.Add(new(member.PilotId, member.ShipId, ship.TypeId, status.Remaining.Fractions(status.Defense), distance));
            }
        }
        var ongoing = requests.FirstOrDefault(request => request.ShipId == state.RepairLock?.TargetId);
        List<RemoteRepairCycle> cycling = [];
        bool settled = false;
        foreach (var cycle in state.RemoteRepairs)
        {
            if (ongoing is null || cycle.TargetId != ongoing.ShipId || !modules.Any(module => module.ItemId == cycle.Module.ItemId))
            {
                remoteRepairs.Stop(pilot.PilotId, cycle.Module.ItemId);
                continue;
            }
            if (cycle.CompletesAt > now)
            {
                cycling.Add(cycle);
                continue;
            }
            try
            {
                remoteRepairs.Complete(pilot.PilotId, cycle.Module.ItemId, now);
                settled = true;
            }
            catch (InvalidDataException)
            {
                remoteRepairs.Stop(pilot.PilotId, cycle.Module.ItemId);
            }
        }
        if (settled && ongoing is not null)
        {
            int index = requests.IndexOf(ongoing);
            var status = health.Read(ongoing.PilotId, now);
            ongoing = ongoing with { Remaining = status.Remaining.Fractions(status.Defense) };
            requests[index] = ongoing;
        }

        var target =
            cycling.Count > 0
                ? ongoing
                : requests
                    .Where(request => modules.Any(module => NeedsRepair(request, module)))
                    .OrderBy(request => modules.Where(module => NeedsRepair(request, module)).Min(module => Remaining(request, module)))
                    .ThenBy(request => request.PilotId)
                    .FirstOrDefault();
        if (target is null)
        {
            StopLogistics(pilot.PilotId, state with { RemoteRepairs = [] });
            return new(pilot, null, false, false, false);
        }
        var scan = state.RepairLock;
        if (scan?.TargetId != target.ShipId)
        {
            if (scan is not null)
            {
                locks.Remove(pilot.ShipId, scan.TargetId);
            }
            if (world.Economy.Peek<SpatialNavigationState>(pilot.ShipId)?.GateCloakUntil > now)
            {
                gateCloaks.Cancel(pilot.PilotId, pilot.ShipId);
            }
            double seconds = TargetingDefinitions.LockSeconds(
                profile.ShipAttributes.GetValueOrDefault(DogmaAttributeId.ScanResolution),
                Math.Max(1, database.Catalog.Definition(s_observedSignature, target.TypeId))
            );
            if (!double.IsFinite(seconds) || seconds <= 0)
            {
                return new(pilot, null, false, false, false);
            }
            scan = new(target.ShipId, checked(now + (long)Math.Ceiling(seconds * TimeSpan.TicksPerSecond)));
        }
        if (scan.ReadyAt <= now)
        {
            locks.Add(pilot.ShipId, scan.TargetId);
            foreach (
                var module in modules.Where(module =>
                    NeedsRepair(target, module) && cycling.All(cycle => cycle.Module.ItemId != module.ItemId)
                )
            )
            {
                try
                {
                    var activation = remoteRepairs.Start(
                        pilot.PilotId,
                        module.ItemId,
                        module.Effect,
                        scan.TargetId,
                        new HashSet<long> { scan.TargetId },
                        now
                    );
                    cycling.Add(activation.Cycle);
                }
                catch (InvalidDataException)
                {
                    // Current range, assistance, module condition and capacitor belong to the native repair owner.
                }
            }
        }
        return new(pilot, state with { RepairLock = scan, RemoteRepairs = cycling.ToArray() }, false, false, false);
    }

    private static bool NeedsRepair(RepairRequest request, RemoteRepairModule module) =>
        Remaining(request, module) < CapsuleerDefenseRules.RepairFraction
        && request.Distance <= module.Optimal + module.Falloff * PvpSupport.MaximumFalloffs;

    private static double Remaining(RepairRequest request, RemoteRepairModule module) =>
        module.Kind == RemoteRepairKind.Shield ? request.Remaining.Shield : request.Remaining.Armor;

    private void StopLogistics(long pilot, CapsuleerDefenseState state)
    {
        if (state.RepairLock is { } scan)
        {
            locks.Remove(state.ShipId, scan.TargetId);
        }
        foreach (var cycle in state.RemoteRepairs)
        {
            remoteRepairs.Stop(pilot, cycle.Module.ItemId);
        }
    }
}
