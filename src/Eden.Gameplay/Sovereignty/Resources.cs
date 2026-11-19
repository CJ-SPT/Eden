using System.Numerics;
using Eden.Gameplay.Inventory;
using Eden.Gameplay.Maps;
using Eden.Gameplay.Structures;

namespace Eden.Gameplay.Sovereignty;

// Build 3662168 eve_public.sovereignty.hub.upgrade.PowerState.
public enum SovereigntyUpgradePower
{
    Offline = 1,
    Online = 2,
    Low = 3,
    Pending = 4,
}

public enum SovereigntyWorkforceMode
{
    Idle,
    Import,
    Export,
    Transit,
}

public sealed record SovereigntyResourceAmount(long Power, long Workforce);

public sealed record SovereigntyUpgradeDefinition(
    int TypeId,
    string Name,
    string ExclusiveGroup,
    long Power,
    long Workforce,
    int FuelTypeId,
    long StartupFuel,
    long HourlyFuel,
    long PowerProduced,
    long WorkforceProduced
);

public sealed record SovereigntyUpgradeSetting(int TypeId, bool Online);

public sealed record SovereigntyUpgrade(
    int TypeId,
    int Priority,
    bool ConfiguredOnline,
    SovereigntyUpgradePower State,
    bool StartupPaid = false,
    long? RunningSince = null
);

public sealed record SovereigntyFuel(int TypeId, long Amount, long Remainder = 0);

public sealed record SovereigntyReagentDelivery(int TypeId, int Quantity);

public sealed record SovereigntyAllocation(
    IReadOnlyList<SovereigntyUpgrade> Upgrades,
    IReadOnlyList<SovereigntyFuel> Fuels,
    SovereigntyResourceAmount Used,
    SovereigntyResourceAmount Produced,
    long UpdatedAt
);

public sealed record SovereigntyWorkforceConfiguration(
    SovereigntyWorkforceMode Mode,
    int? Destination = null,
    long Amount = 0,
    IReadOnlyList<int>? Sources = null
);

public sealed record SovereigntyWorkforceFlow(int Source, int Destination, long Amount, bool Connected);

public sealed record SovereigntyNetworkHub(
    long HubId,
    int SystemId,
    int AllianceId,
    SovereigntyResourceAmount Local,
    SovereigntyWorkforceConfiguration Configuration
);

public sealed record SovereigntyResourceView(
    long HubId,
    int SystemId,
    int CorporationId,
    int AllianceId,
    SovereigntyResourceAmount Local,
    SovereigntyResourceAmount Available,
    SovereigntyResourceAmount Potential,
    SovereigntyAllocation Allocation,
    SovereigntyWorkforceConfiguration Workforce,
    IReadOnlyList<SovereigntyWorkforceFlow> Flows
);

public sealed record SovereigntyResourceResult(SovereigntyResourceView Hub, IReadOnlyList<InventoryChange> Changes);

public sealed record SovereigntySkyhook(
    long Id,
    int SystemId,
    long PlanetId,
    int CorporationId,
    int AllianceId,
    bool Active,
    long OnlineAt,
    int ReagentTypeId,
    long Secured,
    long Surplus,
    long UpdatedAt
)
{
    public PlanetResourceDefinition? Resources { get; init; }
}

public sealed record SovereigntySkyhookResult(SovereigntySkyhook Skyhook, IReadOnlyList<InventoryChange> Changes);

public interface ISovereigntyResources
{
    IReadOnlyList<SovereigntyUpgradeDefinition> Definitions();
    SovereigntyResourceView Read(long pilotId, long hubId);
    bool CanManageHub(long pilotId, long hubId);
    SovereigntyResourceResult Configure(
        long pilotId,
        long hubId,
        IReadOnlyList<long> items,
        IReadOnlyList<SovereigntyUpgradeSetting> configuration
    );
    void DestroyUpgrade(long pilotId, long hubId, int typeId);
    SovereigntyResourceResult AddFuel(long pilotId, long hubId, long itemId, int quantity);
    void ConfigureWorkforce(long pilotId, long hubId, SovereigntyWorkforceConfiguration configuration);
    IReadOnlyList<SovereigntyNetworkHub> Network(long pilotId, long hubId);
    SovereigntySkyhookResult DeploySkyhook(long pilotId, long itemId);
    IReadOnlyList<SovereigntySkyhook> Skyhooks(long pilotId, bool corporationOnly = false);
    SovereigntySkyhook Skyhook(long pilotId, long id);
    void SetSkyhookActive(long pilotId, long id, bool active);
    SovereigntySkyhookResult CollectReagents(long pilotId, long id, int quantity = int.MaxValue);
    bool IsSkyhookItem(long pilotId, long itemId);
    bool CanManageSkyhook(long pilotId, long id);
    bool OwnsPilot(long accountId, long pilotId);
    int CurrentSystem(long pilotId);
    int Corporation(long pilotId);
    void AdvanceAll();
}

public static class SovereigntyResourceRules
{
    public const int MaximumWorkforceImports = 3;
    public const int MaximumDetectionTier = 3;
    public const int SkyhookTypeId = 81080;
    public const int SkyhookShieldVulnerableState = 210;
    public const int ReagentSecuredPercent = 50;
    public const double InteractionRange = 10000;
    public static readonly long FuelPeriod = TimeSpan.TicksPerHour;

    public static IReadOnlyList<SovereigntyReagentDelivery> ReagentShortfalls(
        IReadOnlyList<SovereigntyUpgradeDefinition> definitions,
        SovereigntyAllocation allocation
    )
    {
        const int ReorderHours = 2;
        const int RefillHours = 4;
        var configured = definitions.Join(
            allocation.Upgrades.Where(upgrade => upgrade.ConfiguredOnline),
            definition => definition.TypeId,
            upgrade => upgrade.TypeId,
            (definition, upgrade) => (definition, upgrade.StartupPaid)
        );
        List<SovereigntyReagentDelivery> shortfalls = [];
        foreach (var group in configured.Where(entry => entry.definition.FuelTypeId > 0).GroupBy(entry => entry.definition.FuelTypeId))
        {
            BigInteger startup = 0;
            BigInteger hourly = 0;
            foreach (var entry in group)
            {
                startup += entry.StartupPaid ? 0 : entry.definition.StartupFuel;
                hourly += entry.definition.HourlyFuel;
            }
            long stock = allocation.Fuels.FirstOrDefault(fuel => fuel.TypeId == group.Key)?.Amount ?? 0;
            if (stock < BigInteger.Min(uint.MaxValue, startup + hourly * ReorderHours))
            {
                var target = BigInteger.Min(uint.MaxValue, startup + hourly * RefillHours);
                shortfalls.Add(new(group.Key, (int)BigInteger.Min(int.MaxValue, target - stock)));
            }
        }
        return shortfalls.OrderBy(shortfall => shortfall.TypeId).ToArray();
    }

    public static SovereigntyAllocation Advance(
        IReadOnlyList<SovereigntyUpgradeDefinition> definitions,
        IReadOnlyList<SovereigntyUpgrade> upgrades,
        IReadOnlyList<SovereigntyFuel> fuels,
        SovereigntyResourceAmount resources,
        long from,
        long now
    )
    {
        if (
            from < 0
            || now < 0
            || resources.Power < 0
            || resources.Workforce < 0
            || upgrades.Select(row => row.TypeId).Distinct().Count() != upgrades.Count
            || fuels.Select(row => row.TypeId).Distinct().Count() != fuels.Count
            || definitions.Select(row => row.TypeId).Distinct().Count() != definitions.Count
            || fuels.Any(row =>
                row.TypeId <= 0 || row.Amount < 0 || row.Amount > uint.MaxValue || row.Remainder < 0 || row.Remainder >= FuelPeriod
            )
        )
        {
            throw new InvalidDataException("Invalid sovereignty resource state.");
        }
        var profiles = definitions.ToDictionary(row => row.TypeId);
        if (
            definitions.Any(row =>
                row.TypeId <= 0
                || row.Power < 0
                || row.Workforce < 0
                || row.PowerProduced < 0
                || row.WorkforceProduced < 0
                || row.StartupFuel is < 0 or > uint.MaxValue
                || row.HourlyFuel is < 0 or > uint.MaxValue
                || row.ExclusiveGroup is null
                || row.FuelTypeId <= 0 && (row.StartupFuel > 0 || row.HourlyFuel > 0)
            ) || upgrades.Any(row => !profiles.ContainsKey(row.TypeId))
        )
        {
            throw new InvalidDataException("Invalid sovereignty upgrade definition.");
        }
        now = Math.Max(from, now);
        var ledger = fuels.ToDictionary(row => row.TypeId);
        foreach (var definition in definitions.Where(row => row.FuelTypeId > 0))
        {
            ledger.TryAdd(definition.FuelTypeId, new(definition.FuelTypeId, 0));
        }
        var ordered = upgrades.OrderBy(row => row.Priority).ThenBy(row => row.TypeId).ToArray();
        SovereigntyResourceAmount used = new(0, 0),
            produced = new(0, 0);
        void Allocate(long at)
        {
            long power = resources.Power,
                workforce = resources.Workforce;
            long powerUsed = 0,
                workforceUsed = 0;
            HashSet<string> exclusive = [];
            Dictionary<int, long> reserved = [];
            for (int index = 0; index < ordered.Length; index++)
            {
                var row = ordered[index];
                var definition = profiles[row.TypeId];
                var fuel = ledger.GetValueOrDefault(definition.FuelTypeId);
                bool startup = !row.StartupPaid;
                long cost = startup ? definition.StartupFuel : 0;
                bool blocked =
                    definition.Power > power - powerUsed
                    || definition.Workforce > workforce - workforceUsed
                    || definition.ExclusiveGroup.Length > 0 && exclusive.Contains(definition.ExclusiveGroup);
                bool noFuel =
                    definition.HourlyFuel > 0 && (fuel?.Amount ?? 0) - cost - reserved.GetValueOrDefault(definition.FuelTypeId) < 1;
                bool noStartup = cost > (fuel?.Amount ?? 0);
                var state =
                    !row.ConfiguredOnline ? SovereigntyUpgradePower.Offline
                    : blocked || noFuel && !noStartup ? SovereigntyUpgradePower.Low
                    : noStartup ? SovereigntyUpgradePower.Pending
                    : SovereigntyUpgradePower.Online;
                bool online = state == SovereigntyUpgradePower.Online;
                ordered[index] = row with
                {
                    State = state,
                    StartupPaid = row.ConfiguredOnline && (row.StartupPaid || online),
                    RunningSince = online
                        ? row.State == SovereigntyUpgradePower.Online
                            ? row.RunningSince ?? at
                            : at
                        : null,
                };
                if (!online)
                {
                    continue;
                }
                powerUsed = checked(powerUsed + definition.Power);
                workforceUsed = checked(workforceUsed + definition.Workforce);
                power = checked(power + definition.PowerProduced);
                workforce = checked(workforce + definition.WorkforceProduced);
                if (definition.ExclusiveGroup.Length > 0)
                {
                    exclusive.Add(definition.ExclusiveGroup);
                }
                if (fuel is not null)
                {
                    ledger[fuel.TypeId] = fuel with { Amount = fuel.Amount - cost };
                    if (definition.HourlyFuel > 0)
                    {
                        reserved[fuel.TypeId] = reserved.GetValueOrDefault(fuel.TypeId) + 1;
                    }
                }
            }
            used = new(powerUsed, workforceUsed);
            produced = new(power - resources.Power, workforce - resources.Workforce);
        }
        long at = from;
        Allocate(at);
        while (at < now)
        {
            var rates = ordered
                .Where(row => row.State == SovereigntyUpgradePower.Online && profiles[row.TypeId].HourlyFuel > 0)
                .GroupBy(row => profiles[row.TypeId].FuelTypeId)
                .ToDictionary(group => group.Key, group => group.Sum(row => profiles[row.TypeId].HourlyFuel));
            long elapsed = now - at;
            foreach (var (type, rate) in rates)
            {
                var fuel = ledger[type];
                int running = ordered.Count(row =>
                    row.State == SovereigntyUpgradePower.Online
                    && profiles[row.TypeId].FuelTypeId == type
                    && profiles[row.TypeId].HourlyFuel > 0
                );
                BigInteger ticks = ((BigInteger)(fuel.Amount - running + 1) * FuelPeriod - fuel.Remainder + rate - 1) / rate;
                if (ticks < elapsed)
                {
                    elapsed = Math.Max(1, (long)ticks);
                }
            }
            foreach (var (type, rate) in rates)
            {
                var fuel = ledger[type];
                BigInteger burn = (BigInteger)rate * elapsed + fuel.Remainder;
                long consumed = Math.Min(fuel.Amount, (long)BigInteger.Min(long.MaxValue, burn / FuelPeriod));
                ledger[type] = fuel with
                {
                    Amount = fuel.Amount - consumed,
                    Remainder = consumed == fuel.Amount ? 0 : (long)(burn % FuelPeriod),
                };
            }
            at += elapsed;
            Allocate(at);
        }
        return new(ordered, ledger.Values.OrderBy(row => row.TypeId).ToArray(), used, produced, now);
    }

    public static IReadOnlyList<SovereigntyWorkforceFlow> Route(
        IReadOnlyList<SovereigntyNetworkHub> hubs,
        IReadOnlyDictionary<int, IReadOnlyList<int>> neighbors
    )
    {
        if (
            hubs.Select(row => row.SystemId).Distinct().Count() != hubs.Count
            || hubs.Any(row =>
                row.HubId <= 0
                || row.SystemId <= 0
                || row.AllianceId <= 0
                || row.Local.Power < 0
                || row.Local.Workforce < 0
                || row.Configuration.Amount < 0
                || !Enum.IsDefined(row.Configuration.Mode)
            )
        )
        {
            throw new InvalidDataException("Invalid sovereignty workforce network.");
        }
        var bySystem = hubs.ToDictionary(row => row.SystemId);
        List<SovereigntyWorkforceFlow> flows = [];
        foreach (var source in hubs.Where(row => row.Configuration.Mode == SovereigntyWorkforceMode.Export).OrderBy(row => row.SystemId))
        {
            var configuration = source.Configuration;
            long amount = Math.Min(source.Local.Workforce, Math.Max(0, configuration.Amount));
            bool connected =
                configuration.Destination is int destination
                && destination != source.SystemId
                && bySystem.TryGetValue(destination, out var target)
                && target.AllianceId == source.AllianceId
                && target.Configuration.Mode == SovereigntyWorkforceMode.Import
                && target.Configuration.Sources?.Contains(source.SystemId) == true;
            if (connected)
            {
                HashSet<int> visited = [source.SystemId];
                Queue<int> queue = new();
                queue.Enqueue(source.SystemId);
                connected = false;
                while (queue.TryDequeue(out int current))
                {
                    foreach (int next in neighbors.GetValueOrDefault(current) ?? [])
                    {
                        if (!visited.Add(next) || !bySystem.TryGetValue(next, out var node) || node.AllianceId != source.AllianceId)
                        {
                            continue;
                        }
                        if (next == configuration.Destination)
                        {
                            connected = true;
                            break;
                        }
                        if (node.Configuration.Mode == SovereigntyWorkforceMode.Transit)
                        {
                            queue.Enqueue(next);
                        }
                    }
                    if (connected)
                    {
                        break;
                    }
                }
            }
            flows.Add(new(source.SystemId, configuration.Destination ?? 0, connected ? amount : 0, connected));
        }
        return flows;
    }
}
