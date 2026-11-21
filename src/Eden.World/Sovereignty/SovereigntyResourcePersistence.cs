using System.Numerics;
using Eden.Data.World.Alliances;
using Eden.Data.World.Sovereignty;
using Eden.Data.World.Structures;
using Eden.Gameplay.Flight;
using Eden.Gameplay.Sovereignty;
using Eden.Gameplay.Structures;
using Eden.World.Catalog;
using Eden.World.Runtime.Economy;
using Eden.World.Structures;

namespace Eden.World.Sovereignty;

internal static class SovereigntyResourcePersistence
{
    internal static SovereigntyWorkforceConfiguration Configuration(WorldAction world, long hub)
    {
        var row = world.Set<SavedSovereigntyWorkforce>().Find(hub);
        return row is null
            ? new(SovereigntyWorkforceMode.Idle)
            : new(
                row.Mode,
                row.Destination,
                row.Amount,
                world
                    .Filter<SavedSovereigntyWorkforceSource>(source => source.HubId == hub)
                    .Select(source => source.SourceSystemId)
                    .Order()
                    .ToArray()
            );
    }

    internal static SovereigntyResourceAmount Local(WorldAction world, CatalogDbContext catalog, int system, long now)
    {
        var profile = SovereigntyResourceCatalog.System(catalog, system);
        long power = profile.StarPower,
            workforce = 0;
        int? alliance = world.Set<SavedSovereigntyClaim>().Find(system)?.AllianceId;
        foreach (var hook in world.Filter<SavedSovereigntySkyhook>(row => row.SystemId == system))
        {
            if (!Productive(world, hook, now) || world.Set<SavedAllianceMember>().Find(hook.CorporationId)?.AllianceId != alliance)
            {
                continue;
            }
            var planet = profile.Planets.Single(row => row.PlanetId == hook.PlanetId);
            power = checked(power + planet.Power);
            workforce = checked(workforce + planet.Workforce);
        }
        return new(power, workforce);
    }

    private static bool Productive(WorldAction world, SavedSovereigntySkyhook hook, long now) =>
        hook.Active
        && now >= hook.OnlineAt
        && world.Set<SavedStructure>().Find(hook.Id) is { State: not StructureState.Destroyed and not StructureState.Unanchored };

    internal static SovereigntyNetworkHub[] Network(WorldAction world, CatalogDbContext catalog, long now) =>
        world
            .Set<SavedSovereigntyClaim>()
            .OrderBy(row => row.SystemId)
            .Select(row => new SovereigntyNetworkHub(
                row.HubId,
                row.SystemId,
                row.AllianceId,
                Local(world, catalog, row.SystemId, now),
                Configuration(world, row.HubId)
            ))
            .ToArray();

    internal static SovereigntyAllocation Allocation(WorldAction world, long hub, long now) =>
        new(
            world
                .Filter<SavedSovereigntyUpgrade>(row => row.HubId == hub)
                .OrderBy(row => row.Priority)
                .Select(row => new SovereigntyUpgrade(
                    row.TypeId,
                    row.Priority,
                    row.ConfiguredOnline,
                    row.PowerState,
                    row.StartupPaid,
                    row.RunningSince
                ))
                .ToArray(),
            world
                .Filter<SavedSovereigntyFuel>(row => row.HubId == hub)
                .OrderBy(row => row.TypeId)
                .Select(row => new SovereigntyFuel(row.TypeId, row.Amount, row.Remainder))
                .ToArray(),
            new(0, 0),
            new(0, 0),
            now
        );

    internal static void SettleHub(WorldAction world, CatalogDbContext catalog, SavedSovereigntyResourceHub hub, long now)
    {
        if (now < hub.UpdatedAt)
        {
            return;
        }
        var before = Allocation(world, hub.HubId, hub.UpdatedAt);
        var result = SovereigntyResourceRules.Advance(
            SovereigntyResourceCatalog.Upgrades(catalog),
            before.Upgrades,
            before.Fuels,
            new(hub.Power, hub.Workforce),
            hub.UpdatedAt,
            now
        );
        bool navigationPowerLost = false;
        bool shipyardPowerLost = false;
        foreach (var row in world.Filter<SavedSovereigntyUpgrade>(row => row.HubId == hub.HubId))
        {
            var next = result.Upgrades.Single(value => value.TypeId == row.TypeId);
            if (row.PowerState != next.State)
            {
                SovereigntySites.Invalidate(world, hub.SystemId);
                navigationPowerLost |=
                    row.PowerState == SovereigntyUpgradePower.Online
                    && row.TypeId
                        is CapitalTravelRules.CynosuralNavigationUpgrade
                            or CapitalTravelRules.CynosuralSuppressionUpgrade
                            or JumpBridgeRules.SovereigntyUpgrade;
                shipyardPowerLost |=
                    row.PowerState == SovereigntyUpgradePower.Online
                    && row.TypeId == StructureIndustryRules.SupercapitalConstructionUpgrade;
            }
            row.PowerState = next.State;
            row.StartupPaid = next.StartupPaid;
            row.RunningSince = next.RunningSince;
        }
        if (navigationPowerLost)
        {
            CapitalNavigationStore.StopUnavailableServices(world, hub.SystemId, now);
        }
        if (shipyardPowerLost)
        {
            StructureIndustry.StopUnavailableShipyards(world, catalog, hub.SystemId, now);
        }
        foreach (var next in result.Fuels)
        {
            var row = world.Filter<SavedSovereigntyFuel>(row => row.HubId == hub.HubId && row.TypeId == next.TypeId).SingleOrDefault();
            if (row is null && next.Amount == 0 && next.Remainder == 0)
            {
                continue;
            }
            row ??= world
                .Add(
                    new SavedSovereigntyFuel
                    {
                        Id = world.Next("WorldSovereigntyFuel"),
                        HubId = hub.HubId,
                        TypeId = next.TypeId,
                    }
                )
                .Entity;
            row.Amount = next.Amount;
            row.Remainder = next.Remainder;
        }
        hub.UpdatedAt = now;
    }

    internal static void Refresh(WorldAction world, CatalogDbContext catalog, long now)
    {
        var network = Network(world, catalog, now);
        var neighbors =
            network.Length == 0
                ? new Dictionary<int, IReadOnlyList<int>>()
                : (IReadOnlyDictionary<int, IReadOnlyList<int>>)SovereigntyResourceCatalog.System(catalog, network[0].SystemId).Neighbors;
        var flows = SovereigntyResourceRules.Route(network, neighbors);
        foreach (var node in network)
        {
            var row =
                world.Set<SavedSovereigntyResourceHub>().Find(node.HubId)
                ?? world
                    .Add(
                        new SavedSovereigntyResourceHub
                        {
                            HubId = node.HubId,
                            SystemId = node.SystemId,
                            UpdatedAt = now,
                        }
                    )
                    .Entity;
            if (row.UpdatedAt > now)
            {
                continue;
            }
            row.Power = node.Local.Power;
            row.Workforce = checked(
                node.Local.Workforce
                + flows.Where(flow => flow.Destination == node.SystemId).Sum(flow => flow.Amount)
                - flows.Where(flow => flow.Source == node.SystemId).Sum(flow => flow.Amount)
            );
            SettleHub(world, catalog, row, now);
        }
    }

    internal static void Advance(WorldAction world, CatalogDbContext catalog, long now)
    {
        foreach (var claim in world.Set<SavedSovereigntyClaim>())
        {
            if (world.Set<SavedSovereigntyResourceHub>().Find(claim.HubId) is null)
            {
                world.Add(
                    new SavedSovereigntyResourceHub
                    {
                        HubId = claim.HubId,
                        SystemId = claim.SystemId,
                        UpdatedAt = now,
                        Power = SovereigntyResourceCatalog.System(catalog, claim.SystemId).StarPower,
                    }
                );
            }
        }
        var hubs = world.Set<SavedSovereigntyResourceHub>().ToArray();
        long from = hubs.Select(row => row.UpdatedAt).DefaultIfEmpty(now).Min();
        long[] boundaries = world
            .Set<SavedSovereigntySkyhook>()
            .Select(row => row.OnlineAt)
            .Where(at => at > from && at < now)
            .Append(now)
            .Distinct()
            .Order()
            .ToArray();
        foreach (long at in boundaries)
        {
            foreach (var hub in hubs)
            {
                SettleHub(world, catalog, hub, at);
            }
            foreach (var hook in world.Set<SavedSovereigntySkyhook>())
            {
                Harvest(world, catalog, hook, at);
            }
            Refresh(world, catalog, at);
        }
    }

    private static void Harvest(WorldAction world, CatalogDbContext catalog, SavedSovereigntySkyhook hook, long now)
    {
        if (now <= hook.UpdatedAt)
        {
            return;
        }
        var reagent = SovereigntyResourceCatalog
            .System(catalog, hook.SystemId)
            .Planets.Single(row => row.PlanetId == hook.PlanetId)
            .Reagent;
        if (reagent is null || !Productive(world, hook, now))
        {
            hook.UpdatedAt = now;
            return;
        }
        long period = checked(reagent.CycleSeconds * TimeSpan.TicksPerSecond);
        long from = Math.Max(hook.OnlineAt, hook.UpdatedAt);
        long cycles = Math.Max(0, now - from) / period;
        if (cycles == 0)
        {
            return;
        }
        BigInteger amount = (BigInteger)cycles * reagent.AmountPerCycle;
        BigInteger secure = (BigInteger)cycles * (reagent.AmountPerCycle * SovereigntyResourceRules.ReagentSecuredPercent / 100);
        hook.Secured = (long)BigInteger.Min(reagent.SecuredCapacity, hook.Secured + secure);
        hook.Surplus = (long)BigInteger.Min(reagent.UnsecuredCapacity, hook.Surplus + amount - secure);
        hook.UpdatedAt = checked(from + cycles * period);
    }

    internal static SovereigntyResourceView View(WorldAction world, CatalogDbContext catalog, SavedSovereigntyClaim claim, long now)
    {
        var hub = world.Set<SavedSovereigntyResourceHub>().Find(claim.HubId)!;
        now = Math.Max(now, hub.UpdatedAt);
        var allocation = Allocation(world, claim.HubId, hub.UpdatedAt);
        var definitions = SovereigntyResourceCatalog.Upgrades(catalog).ToDictionary(row => row.TypeId);
        var online = allocation
            .Upgrades.Where(row => row.State == SovereigntyUpgradePower.Online)
            .Select(row => definitions[row.TypeId])
            .ToArray();
        allocation = allocation with
        {
            Used = new(online.Sum(row => row.Power), online.Sum(row => row.Workforce)),
            Produced = new(online.Sum(row => row.PowerProduced), online.Sum(row => row.WorkforceProduced)),
        };
        var profile = SovereigntyResourceCatalog.System(catalog, claim.SystemId);
        return new(
            claim.HubId,
            claim.SystemId,
            claim.CorporationId,
            claim.AllianceId,
            Local(world, catalog, claim.SystemId, now),
            new(hub.Power, hub.Workforce),
            new(profile.StarPower + profile.Planets.Sum(row => row.Power), profile.Planets.Sum(row => row.Workforce)),
            allocation,
            Configuration(world, claim.HubId),
            SovereigntyResourceRules.Route(Network(world, catalog, now), profile.Neighbors)
        );
    }

    internal static void DestroyHub(WorldAction world, CatalogDbContext catalog, long hubId, long at)
    {
        if (world.Set<SavedSovereigntyResourceHub>().Find(hubId) is { } hub)
        {
            SettleHub(world, catalog, hub, at);
            SovereigntySites.Invalidate(world, hub.SystemId);
            world.Remove(hub);
        }
        world.RemoveRange(world.Filter<SavedSovereigntyUpgrade>(row => row.HubId == hubId).ToArray());
        world.RemoveRange(world.Filter<SavedSovereigntyFuel>(row => row.HubId == hubId).ToArray());
        world.RemoveRange(world.Filter<SavedSovereigntyWorkforceSource>(row => row.HubId == hubId).ToArray());
        if (world.Set<SavedSovereigntyWorkforce>().Find(hubId) is { } configuration)
        {
            world.Remove(configuration);
        }
    }
}
