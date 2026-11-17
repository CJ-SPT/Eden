using System.Net;
using Eden.Gameplay.Sovereignty;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Fuel = Eden.Protocol.Gateway.Sovereignty.FuelApi;
using Hub = Eden.Protocol.Gateway.Sovereignty.HubApi;
using HubId = Eden.Protocol.Gateway.Sovereignty.Hub.Identifier;
using Identifier = Eden.Protocol.Gateway.Sovereignty.Identifier;
using Skyhook = Eden.Protocol.Gateway.Sovereignty.SkyhookApi;
using Upgrade = Eden.Protocol.Gateway.Sovereignty.Upgrades;
using UpgradeApi = Eden.Protocol.Gateway.Sovereignty.UpgradeApi;
using Workforce = Eden.Protocol.Gateway.Sovereignty.Workforce;
using WorkforceApi = Eden.Protocol.Gateway.Sovereignty.WorkforceApi;

namespace Eden.Protocol.Gateway;

internal static partial class SovereigntyResourceRequests
{
    private const string UpgradePrefix = "eve_public.sovereignty.hub.upgrade.api.";
    private const string FuelPrefix = "eve_public.sovereignty.hub.fuel.api.";
    private const string HubPrefix = "eve_public.sovereignty.hub.api.";
    private const string WorkforcePrefix = "eve_public.sovereignty.hub.workforce.api.";
    private const string SkyhookPrefix = "eve_public.sovereignty.skyhook.api.";

    internal static GatewayReply? Read(
        string type,
        ByteString payload,
        AuthoritativeContext? context,
        long? account,
        ISovereigntyResources resources,
        Eden.Gameplay.Maps.ICelestialResourceDirectory directory,
        Eden.Protocol.Sovereignty.SovereigntyGatewayUpdates? updates
    )
    {
        if (
            !type.StartsWith(UpgradePrefix, StringComparison.Ordinal)
            && !type.StartsWith(FuelPrefix, StringComparison.Ordinal)
            && !type.StartsWith(HubPrefix, StringComparison.Ordinal)
            && !type.StartsWith(WorkforcePrefix, StringComparison.Ordinal)
            && !type.StartsWith(SkyhookPrefix, StringComparison.Ordinal)
        )
        {
            return null;
        }
        try
        {
            if (type == UpgradePrefix + "GetDefinitionsRequest")
            {
                _ = UpgradeApi.GetDefinitionsRequest.Parser.ParseFrom(payload);
                var definitions = new UpgradeApi.GetDefinitionsResponse();
                definitions.Definitions.Add(resources.Definitions().Select(Definition));
                return new(HttpStatusCode.OK, definitions);
            }
            long pilot = context?.Identity?.Character?.Sequential ?? 0;
            if (account is not long authenticated || pilot == 0 || !resources.OwnsPilot(authenticated, pilot))
            {
                return new(HttpStatusCode.Unauthorized);
            }
            var reply = Private(type, payload, pilot, resources, directory, authenticated, updates);
            return reply is null ? new(HttpStatusCode.NotImplemented) : new(HttpStatusCode.OK, reply);
        }
        catch (UnauthorizedAccessException)
        {
            return new(HttpStatusCode.Forbidden);
        }
        catch (Exception error) when (error is InvalidDataException or InvalidProtocolBufferException or OverflowException)
        {
            return new(HttpStatusCode.BadRequest);
        }
    }

    private static long Id(ulong? value) =>
        value is > 0 and <= long.MaxValue ? (long)value.Value : throw new InvalidDataException("Choose a valid sovereignty identity.");

    private static int Type(ulong? value) => checked((int)Id(value));

    private static Identifier Identifier(long value) => new() { Sequential = checked((ulong)value) };

    private static HubId HubIdentifier(long value) => new() { Sequential = checked((ulong)value) };

    private static Timestamp Time(long value) => Timestamp.FromDateTime(DateTime.FromFileTimeUtc(value));

    private static IMessage? Private(
        string type,
        ByteString payload,
        long pilot,
        ISovereigntyResources resources,
        Eden.Gameplay.Maps.ICelestialResourceDirectory directory,
        long account,
        Eden.Protocol.Sovereignty.SovereigntyGatewayUpdates? updates
    )
    {
        SovereigntyResourceView Read(long hub)
        {
            var view = resources.Read(pilot, hub);
            updates?.Observe(account, pilot, view);
            return view;
        }
        switch (type)
        {
            case UpgradePrefix + "GetHubUpgradesRequest":
                var upgrades = UpgradeApi.GetHubUpgradesRequest.Parser.ParseFrom(payload);
                return new UpgradeApi.GetHubUpgradesResponse { HubUpgrades = Upgrades(Read(Id(upgrades.Hub?.Sequential)), resources) };
            case UpgradePrefix + "GetRequest":
                var get = UpgradeApi.GetRequest.Parser.ParseFrom(payload);
                var view = Read(Id(get.Upgrade?.Hub?.Sequential));
                int requested = Type(get.Upgrade?.UpgradeType?.Sequential ?? get.Upgrade?.InstallationType?.Sequential);
                var row =
                    view.Allocation.Upgrades.SingleOrDefault(row => row.TypeId == requested)
                    ?? throw new InvalidDataException("This upgrade is not installed.");
                return new UpgradeApi.GetResponse { Attributes = Attributes(row, resources) };
            case UpgradePrefix + "ProcessConfigurationRequest":
                var configure = UpgradeApi.ProcessConfigurationRequest.Parser.ParseFrom(payload);
                var configured = resources.Configure(
                    pilot,
                    Id(configure.Hub?.Sequential),
                    configure.NewUpgrades.Select(row => Id(row.Sequential)).ToArray(),
                    configure
                        .Configuration.Select(row => new SovereigntyUpgradeSetting(Type(row.UpgradeType?.Sequential), row.Online))
                        .ToArray()
                );
                updates?.Inventory(pilot, configured.Changes);
                updates?.Observe(account, pilot, configured.Hub);
                return new UpgradeApi.ProcessConfigurationResponse { HubUpgrades = Upgrades(configured.Hub, resources) };
            case UpgradePrefix + "UninstallRequest":
                var uninstall = UpgradeApi.UninstallRequest.Parser.ParseFrom(payload);
                resources.DestroyUpgrade(
                    pilot,
                    Id(uninstall.Upgrade?.Hub?.Sequential),
                    Type(uninstall.Upgrade?.UpgradeType?.Sequential ?? uninstall.Upgrade?.InstallationType?.Sequential)
                );
                return new UpgradeApi.UninstallResponse();
            case FuelPrefix + "GetRequest":
                var fuel = Fuel.GetRequest.Parser.ParseFrom(payload);
                return Fuels(Read(Id(fuel.Hub?.Sequential)), resources);
            case FuelPrefix + "AddRequest":
                var add = Fuel.AddRequest.Parser.ParseFrom(payload);
                var added = resources.AddFuel(pilot, Id(add.Hub?.Sequential), Id(add.FuelItem?.Sequential), checked((int)add.Amount));
                updates?.Inventory(pilot, added.Changes);
                updates?.Observe(account, pilot, added.Hub);
                return new Fuel.AddResponse();
            case HubPrefix + "GetResourcesRequest":
                var requestedResources = Hub.GetResourcesRequest.Parser.ParseFrom(payload);
                var hub = Read(Id(requestedResources.Hub?.Sequential));
                return new Hub.GetResourcesResponse
                {
                    Power = new()
                    {
                        Available = checked((ulong)hub.Available.Power),
                        Allocated = checked((ulong)hub.Allocation.Used.Power),
                        LocalHarvest = checked((ulong)hub.Local.Power),
                    },
                    Workforce = new()
                    {
                        Available = checked((ulong)hub.Available.Workforce),
                        Allocated = checked((ulong)hub.Allocation.Used.Workforce),
                        LocalHarvest = checked((ulong)hub.Local.Workforce),
                    },
                };
            case WorkforcePrefix + "GetConfigurationRequest":
                var workforce = WorkforceApi.GetConfigurationRequest.Parser.ParseFrom(payload);
                return new WorkforceApi.GetConfigurationResponse
                {
                    Configuration = Configuration(resources.Read(pilot, Id(workforce.Hub?.Sequential)).Workforce),
                };
            case WorkforcePrefix + "ConfigureRequest":
                var workforceChange = WorkforceApi.ConfigureRequest.Parser.ParseFrom(payload);
                resources.ConfigureWorkforce(pilot, Id(workforceChange.Hub?.Sequential), Configuration(workforceChange.Configuration));
                return new WorkforceApi.ConfigureResponse();
            case WorkforcePrefix + "GetStateRequest":
                var state = WorkforceApi.GetStateRequest.Parser.ParseFrom(payload);
                return new WorkforceApi.GetStateResponse { State = State(resources.Read(pilot, Id(state.Hub?.Sequential))) };
            case WorkforcePrefix + "GetNetworkableHubsRequest":
                var network = WorkforceApi.GetNetworkableHubsRequest.Parser.ParseFrom(payload);
                var result = new WorkforceApi.GetNetworkableHubsResponse();
                result.Hubs.Add(
                    resources
                        .Network(pilot, Id(network.Hub?.Sequential))
                        .Select(row => new WorkforceApi.GetNetworkableHubsResponse.Types.NetworkedHub
                        {
                            Hub = HubIdentifier(row.HubId),
                            System = Identifier(row.SystemId),
                            Configuration = Configuration(row.Configuration),
                            State = State(resources.Read(pilot, row.HubId)),
                        })
                );
                return result;
            default:
                return Skyhooks(type, payload, pilot, resources, directory, account, updates);
        }
    }

    private static Upgrade.Definition Definition(SovereigntyUpgradeDefinition value) =>
        new()
        {
            InstallationType = Identifier(value.TypeId),
            PowerRequired = checked((ulong)value.Power),
            WorkforceRequired = checked((ulong)value.Workforce),
            FuelType = value.FuelTypeId == 0 ? null : Identifier(value.FuelTypeId),
            FuelStartupCost = checked((uint)value.StartupFuel),
            FuelConsumptionPerHour = checked((uint)value.HourlyFuel),
            PowerProduced = checked((ulong)value.PowerProduced),
            WorkforceProduced = checked((ulong)value.WorkforceProduced),
            MutuallyExclusiveGroup = value.ExclusiveGroup,
        };

    private static Upgrade.Attributes Attributes(SovereigntyUpgrade row, ISovereigntyResources resources) =>
        new() { Definition = Definition(resources.Definitions().Single(value => value.TypeId == row.TypeId)), PowerState = (int)row.State };

    private static Upgrade.HubUpgrades Upgrades(SovereigntyResourceView hub, ISovereigntyResources resources)
    {
        var result = new Upgrade.HubUpgrades { Hub = HubIdentifier(hub.HubId), LastUpdated = Time(hub.Allocation.UpdatedAt) };
        result.Upgrades.Add(
            hub.Allocation.Upgrades.Select(row => new Upgrade.HubUpgrades.Types.Upgrade
            {
                Identifier = new()
                {
                    Hub = HubIdentifier(hub.HubId),
                    InstallationType = Identifier(row.TypeId),
                    UpgradeType = new() { Sequential = checked((ulong)row.TypeId) },
                },
                Attributes = Attributes(row, resources),
            })
        );
        return result;
    }

    private static Fuel.GetResponse Fuels(SovereigntyResourceView hub, ISovereigntyResources resources)
    {
        var result = new Fuel.GetResponse { LastUpdated = Time(hub.Allocation.UpdatedAt) };
        var definitions = resources.Definitions().ToDictionary(row => row.TypeId);
        result.Fuels.Add(
            hub.Allocation.Fuels.Select(row => new Fuel.GetResponse.Types.Fuel
            {
                FuelType = Identifier(row.TypeId),
                Amount = checked((uint)row.Amount),
                BurnedPerHour = checked(
                    (uint)
                        hub
                            .Allocation.Upgrades.Where(upgrade =>
                                upgrade.State == SovereigntyUpgradePower.Online && definitions[upgrade.TypeId].FuelTypeId == row.TypeId
                            )
                            .Sum(upgrade => definitions[upgrade.TypeId].HourlyFuel)
                ),
            })
        );
        return result;
    }

    private static SovereigntyWorkforceConfiguration Configuration(Workforce.Configuration? value) =>
        value?.ModeCase switch
        {
            Workforce.Configuration.ModeOneofCase.Inactive when value.Inactive => new(SovereigntyWorkforceMode.Idle),
            Workforce.Configuration.ModeOneofCase.Transit when value.Transit => new(SovereigntyWorkforceMode.Transit),
            Workforce.Configuration.ModeOneofCase.ImportSettings => new(
                SovereigntyWorkforceMode.Import,
                Sources: value.ImportSettings.Sources.Select(row => Type(row.Source_?.Sequential)).ToArray()
            ),
            Workforce.Configuration.ModeOneofCase.ExportSettings => new(
                SovereigntyWorkforceMode.Export,
                value.ExportSettings.ExportDestinationCase == Workforce.ExportConfiguration.ExportDestinationOneofCase.NoDestination
                    ? null
                    : Type(value.ExportSettings.DestinationSystem?.Sequential ?? value.ExportSettings.Destination?.Sequential),
                checked((long)value.ExportSettings.Amount)
            ),
            _ => throw new InvalidDataException("Choose a workforce configuration mode."),
        };

    private static Workforce.Configuration Configuration(SovereigntyWorkforceConfiguration value)
    {
        var result = new Workforce.Configuration();
        switch (value.Mode)
        {
            case SovereigntyWorkforceMode.Idle:
                result.Inactive = true;
                break;
            case SovereigntyWorkforceMode.Transit:
                result.Transit = true;
                break;
            case SovereigntyWorkforceMode.Export:
                result.ExportSettings = new() { Amount = checked((ulong)value.Amount) };
                if (value.Destination is int destination)
                {
                    result.ExportSettings.DestinationSystem = Identifier(destination);
                }
                else
                {
                    result.ExportSettings.NoDestination = true;
                }
                break;
            case SovereigntyWorkforceMode.Import:
                result.ImportSettings = new();
                result.ImportSettings.Sources.Add(
                    (value.Sources ?? []).Select(source => new Workforce.ImportConfiguration.Types.Source { Source_ = Identifier(source) })
                );
                break;
        }
        return result;
    }

    private static Workforce.State State(SovereigntyResourceView value)
    {
        var result = new Workforce.State();
        switch (value.Workforce.Mode)
        {
            case SovereigntyWorkforceMode.Idle:
                result.Inactive = true;
                break;
            case SovereigntyWorkforceMode.Transit:
                result.Transit = new();
                break;
            case SovereigntyWorkforceMode.Import:
                result.ImportState = new();
                result.ImportState.Sources.Add(
                    (value.Workforce.Sources ?? []).Select(source => new Workforce.ImportState.Types.Source
                    {
                        Source_ = Identifier(source),
                        Amount = checked(
                            (ulong)
                                value
                                    .Flows.Where(flow => flow.Source == source && flow.Destination == value.SystemId)
                                    .Sum(flow => flow.Amount)
                        ),
                    })
                );
                break;
            case SovereigntyWorkforceMode.Export:
                result.ExportState = new() { Amount = checked((ulong)value.Workforce.Amount) };
                var flow = value.Flows.SingleOrDefault(flow => flow.Source == value.SystemId);
                if (value.Workforce.Destination is int destination)
                {
                    result.ExportState.DestinationSystem = Identifier(destination);
                }
                else
                {
                    result.ExportState.NoDestination = true;
                }
                if (flow?.Connected == true)
                {
                    result.ExportState.Connected = new()
                    {
                        DestinationSystem = Identifier(flow.Destination),
                        ExportedQuantity = checked((ulong)flow.Amount),
                    };
                }
                else
                {
                    result.ExportState.Disconnected = new() { LocalReserve = checked((ulong)value.Local.Workforce) };
                }
                break;
        }
        return result;
    }
}
