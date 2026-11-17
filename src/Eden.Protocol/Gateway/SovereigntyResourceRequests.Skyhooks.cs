using Eden.Gameplay.Maps;
using Eden.Gameplay.Sovereignty;
using Eden.Protocol.Gateway.CelestialResources;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Reagent = Eden.Protocol.Gateway.Sovereignty.ResourcePlanetReagent;
using ReagentId = Eden.Protocol.Gateway.Sovereignty.ResourcePlanetReagentItemtype.Identifier;
using Skyhook = Eden.Protocol.Gateway.Sovereignty.SkyhookApi;

namespace Eden.Protocol.Gateway;

internal static partial class SovereigntyResourceRequests
{
    private static Specification ResourceVersion(ICelestialResourceDirectory directory) =>
        new() { Major = Eden.Gameplay.Content.BuildContentPolicy.PinnedBuild, BuildTags = { directory.Read().Revision } };

    private static Reagent.Definition ReagentDefinition(PlanetReagentResource resource) =>
        new()
        {
            AmountPerCycle = checked((uint)resource.AmountPerCycle),
            CyclePeriod = new Duration { Seconds = resource.CycleSeconds },
            SecuredCapacity = checked((uint)resource.SecuredCapacity),
            UnsecuredCapacity = checked((uint)resource.UnsecuredCapacity),
            SecuredPercentage = SovereigntyResourceRules.ReagentSecuredPercent,
        };

    private static Reagent.Simulation Simulation(SovereigntySkyhook hook) =>
        new()
        {
            SecuredStock = checked((uint)hook.Secured),
            UnsecuredStock = checked((uint)hook.Surplus),
            LastCycle = Time(hook.UpdatedAt),
        };

    private static Reagent.HarvestSimulation Harvest(SovereigntySkyhook hook) =>
        new()
        {
            LastCycle = Time(hook.UpdatedAt),
            Config = new()
            {
                AmountPerCycle = hook.Active ? checked((uint)(hook.Resources?.Reagent?.AmountPerCycle ?? 0)) : 0,
                CyclePeriod = new Duration { Seconds = hook.Resources?.Reagent?.CycleSeconds ?? 1 },
                MaxStock = checked(
                    (ulong)((hook.Resources?.Reagent?.SecuredCapacity ?? 0) + (hook.Resources?.Reagent?.UnsecuredCapacity ?? 0))
                ),
            },
        };

    private static Reagent.MaturationSimulation Maturation(SovereigntySkyhook hook) =>
        new()
        {
            LastCycle = Time(hook.UpdatedAt),
            Config = new() { CyclePeriod = new Duration { Seconds = 1 } },
        };

    private static Sovereignty.Skyhook.Workforce SkyhookWorkforce(SovereigntySkyhook hook) =>
        hook.Resources?.Workforce is > 0
            ? new() { Amount = hook.Active ? checked((ulong)hook.Resources.Workforce) : 0 }
            : new() { None = true };

    private static IMessage? Skyhooks(
        string type,
        ByteString payload,
        long pilot,
        ISovereigntyResources resources,
        ICelestialResourceDirectory directory,
        long account,
        Eden.Protocol.Sovereignty.SovereigntyGatewayUpdates? updates
    )
    {
        SovereigntySkyhook Observe(long id)
        {
            var view = resources.Skyhook(pilot, id);
            updates?.Observe(account, pilot, view);
            return view;
        }
        switch (type)
        {
            case SkyhookPrefix + "GetRequest":
                var requested = Skyhook.GetRequest.Parser.ParseFrom(payload);
                var hook = Observe(Id(requested.Skyhook?.Sequential));
                var result = new Skyhook.GetResponse
                {
                    Skyhook = Identifier(hook.Id),
                    Active = hook.Active,
                    Workforce = SkyhookWorkforce(hook),
                    Unknown = true,
                    PlanetResourcesDefinitionsVersion = ResourceVersion(directory),
                };
                if (hook.Resources?.Reagent is { } reagent)
                {
                    result.ReagentDefinitions.Add(
                        new Skyhook.GetResponse.Types.ReagentDefinition
                        {
                            Reagent = Identifier(hook.ReagentTypeId),
                            ReagentType = new ReagentId { Sequential = checked((ulong)hook.ReagentTypeId) },
                            Definition = ReagentDefinition(reagent),
                        }
                    );
                    result.ReagentSimulations.Add(
                        new Skyhook.GetResponse.Types.ReagentSimulation
                        {
                            Reagent = Identifier(hook.ReagentTypeId),
                            ReagentType = new ReagentId { Sequential = checked((ulong)hook.ReagentTypeId) },
                            Simulation = Simulation(hook),
                            Harvest = Harvest(hook),
                            Maturation = Maturation(hook),
                        }
                    );
                }
                return result;
            case SkyhookPrefix + "GetAllLocalRequest":
                _ = Skyhook.GetAllLocalRequest.Parser.ParseFrom(payload);
                var local = new Skyhook.GetAllLocalResponse { SolarSystem = Identifier(resources.CurrentSystem(pilot)) };
                foreach (var row in resources.Skyhooks(pilot))
                {
                    updates?.Observe(account, pilot, row);
                    var skyhook = new Skyhook.GetAllLocalResponse.Types.Skyhook
                    {
                        Skyhook_ = Identifier(row.Id),
                        Active = row.Active,
                        Unknown = true,
                        Workforce = SkyhookWorkforce(row),
                        PlanetResourcesDefinitionsVersion = ResourceVersion(directory),
                    };
                    if (row.Resources?.Reagent is { } definition)
                    {
                        skyhook.ReagentDefinitions.Add(
                            new Skyhook.GetAllLocalResponse.Types.ReagentDefinition
                            {
                                Reagent = Identifier(row.ReagentTypeId),
                                ReagentType = new() { Sequential = checked((ulong)row.ReagentTypeId) },
                                Definition = ReagentDefinition(definition),
                            }
                        );
                        skyhook.ReagentSimulations.Add(
                            new Skyhook.GetAllLocalResponse.Types.ReagentSimulation
                            {
                                Reagent = Identifier(row.ReagentTypeId),
                                ReagentType = new() { Sequential = checked((ulong)row.ReagentTypeId) },
                                Simulation = Simulation(row),
                                Harvest = Harvest(row),
                                Maturation = Maturation(row),
                            }
                        );
                    }
                    local.Skyhooks.Add(skyhook);
                }
                return local;
            case SkyhookPrefix + "GetAllByCorporationRequest":
                var corporation = Skyhook.GetAllByCorporationRequest.Parser.ParseFrom(payload);
                if (Type(corporation.Corporation?.Sequential) != resources.Corporation(pilot))
                {
                    throw new UnauthorizedAccessException("Choose your own corporation's skyhooks.");
                }
                var own = new Skyhook.GetAllByCorporationResponse();
                foreach (var row in resources.Skyhooks(pilot, corporationOnly: true))
                {
                    updates?.Observe(account, pilot, row);
                    var skyhook = new Skyhook.GetAllByCorporationResponse.Types.Skyhook
                    {
                        Skyhook_ = Identifier(row.Id),
                        Active = row.Active,
                        Unknown = true,
                        Workforce = SkyhookWorkforce(row),
                        PlanetResourcesDefinitionsVersion = ResourceVersion(directory),
                    };
                    if (row.Resources?.Reagent is { } definition)
                    {
                        skyhook.ReagentDefinitions.Add(
                            new Skyhook.GetAllByCorporationResponse.Types.ReagentDefinition
                            {
                                ReagentType = new() { Sequential = checked((ulong)row.ReagentTypeId) },
                                Definition = ReagentDefinition(definition),
                            }
                        );
                        skyhook.ReagentSimulations.Add(
                            new Skyhook.GetAllByCorporationResponse.Types.ReagentSimulation
                            {
                                ReagentType = new() { Sequential = checked((ulong)row.ReagentTypeId) },
                                Simulation = Simulation(row),
                                Harvest = Harvest(row),
                                Maturation = Maturation(row),
                            }
                        );
                    }
                    own.Skyhooks.Add(skyhook);
                }
                return own;
            case SkyhookPrefix + "ActivateRequest":
                var activate = Skyhook.ActivateRequest.Parser.ParseFrom(payload);
                Observe(Id(activate.Skyhook?.Sequential));
                resources.SetSkyhookActive(pilot, Id(activate.Skyhook?.Sequential), true);
                return new Skyhook.ActivateResponse();
            case SkyhookPrefix + "DeactivateRequest":
                var deactivate = Skyhook.DeactivateRequest.Parser.ParseFrom(payload);
                Observe(Id(deactivate.Skyhook?.Sequential));
                resources.SetSkyhookActive(pilot, Id(deactivate.Skyhook?.Sequential), false);
                return new Skyhook.DeactivateResponse();
            default:
                return null;
        }
    }
}
