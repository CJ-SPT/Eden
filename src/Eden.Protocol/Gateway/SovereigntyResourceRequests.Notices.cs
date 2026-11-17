using Eden.Gameplay.Sovereignty;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Hub = Eden.Protocol.Gateway.Sovereignty.HubApi;
using UpgradeApi = Eden.Protocol.Gateway.Sovereignty.UpgradeApi;

namespace Eden.Protocol.Gateway;

internal static partial class SovereigntyResourceRequests
{
    internal static IReadOnlyList<Notice> Notices(
        SovereigntyResourceView before,
        SovereigntyResourceView current,
        ISovereigntyResources resources
    )
    {
        List<IMessage> messages = [];
        if (!before.Allocation.Upgrades.SequenceEqual(current.Allocation.Upgrades))
        {
            foreach (
                var removed in before.Allocation.Upgrades.Where(row => !current.Allocation.Upgrades.Any(next => next.TypeId == row.TypeId))
            )
            {
                messages.Add(
                    new UpgradeApi.UninstalledNotice
                    {
                        Hub = HubIdentifier(current.HubId),
                        UpgradeType = Identifier(removed.TypeId),
                        Upgrade = new()
                        {
                            Hub = HubIdentifier(current.HubId),
                            InstallationType = Identifier(removed.TypeId),
                            UpgradeType = new() { Sequential = checked((ulong)removed.TypeId) },
                        },
                    }
                );
            }
            messages.Add(new UpgradeApi.HubUpgradesSimulatedNotice { HubUpgrades = Upgrades(current, resources) });
        }
        if (
            before.Local != current.Local
            || before.Available != current.Available
            || before.Allocation.Used != current.Allocation.Used
            || before.Allocation.Produced != current.Allocation.Produced
        )
        {
            messages.Add(
                new Hub.ResourcesSimulatedNotice
                {
                    Hub = HubIdentifier(current.HubId),
                    SolarSystem = Identifier(current.SystemId),
                    Power = new()
                    {
                        Available = checked((ulong)current.Available.Power),
                        Allocated = checked((ulong)current.Allocation.Used.Power),
                        LocalHarvest = checked((ulong)current.Local.Power),
                    },
                    Workforce = new()
                    {
                        Available = checked((ulong)current.Available.Workforce),
                        Allocated = checked((ulong)current.Allocation.Used.Workforce),
                        LocalHarvest = checked((ulong)current.Local.Workforce),
                    },
                }
            );
        }
        return messages
            .Select(message => new Notice
            {
                Dispatched = Time(current.Allocation.UpdatedAt),
                Uuid = ByteString.CopyFrom(Guid.NewGuid().ToByteArray()),
                InternalOrigin = "eden",
                Payload = Any.Pack(message),
            })
            .ToArray();
    }

    internal static IReadOnlyList<Notice> Notices(SovereigntySkyhook before, SovereigntySkyhook current)
    {
        List<IMessage> messages = [];
        if (before.Active != current.Active)
        {
            messages.Add(
                new Sovereignty.SkyhookApi.ActivationNotice
                {
                    Skyhook = Identifier(current.Id),
                    Planet = Identifier(current.PlanetId),
                    SolarSystem = Identifier(current.SystemId),
                    Active = current.Active,
                }
            );
            messages.Add(
                new Sovereignty.SkyhookApi.WorkforceChangedNotice
                {
                    Skyhook = Identifier(current.Id),
                    Planet = Identifier(current.PlanetId),
                    SolarSystem = Identifier(current.SystemId),
                    Workforce = current.Active ? checked((ulong)(current.Resources?.Workforce ?? 0)) : 0,
                }
            );
        }
        if (
            current.ReagentTypeId != 0
            && (before.Active != current.Active || before.Secured != current.Secured || before.Surplus != current.Surplus)
        )
        {
            var simulated = new Sovereignty.SkyhookApi.ReagentSimulationsNotice
            {
                Skyhook = Identifier(current.Id),
                Planet = Identifier(current.PlanetId),
            };
            simulated.Simulations.Add(
                new Sovereignty.SkyhookApi.ReagentSimulationsNotice.Types.ReagentSimulation
                {
                    Reagent = Identifier(current.ReagentTypeId),
                    ReagentType = new() { Sequential = checked((ulong)current.ReagentTypeId) },
                    Simulation = Simulation(current),
                    Harvest = Harvest(current),
                    Maturation = Maturation(current),
                }
            );
            messages.Add(simulated);
        }
        return messages
            .Select(message => new Notice
            {
                Dispatched = Time(current.UpdatedAt),
                Uuid = ByteString.CopyFrom(Guid.NewGuid().ToByteArray()),
                InternalOrigin = "eden",
                Payload = Any.Pack(message),
            })
            .ToArray();
    }
}
