using System.Net;
using Eden.Gameplay.Content;
using Eden.Gameplay.Maps;
using Eden.Protocol.Gateway.CelestialResources;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Planets = Eden.Protocol.Gateway.CelestialResources.Planets;
using Stars = Eden.Protocol.Gateway.CelestialResources.Stars;

namespace Eden.Protocol.Gateway;

internal sealed record CelestialResourceReply(HttpStatusCode Status, IMessage? Payload = null);

internal static class CelestialResourceRequests
{
    private const string PlanetApi = "eve_public.sovereignty.resource.planet.api.";
    private const string StarApi = "eve_public.sovereignty.resource.star.api.";

    internal static CelestialResourceReply? Read(string type, ByteString payload, ICelestialResourceDirectory directory)
    {
        try
        {
            switch (type)
            {
                case StarApi + "GetAllConfigurationsRequest":
                    _ = Stars.GetAllConfigurationsRequest.Parser.ParseFrom(payload);
                    // The pinned catalog has no authoritative stellar power configurations.
                    // An empty collection lets Sun Show Info retain its other static attributes.
                    return new(HttpStatusCode.OK, new Stars.GetAllConfigurationsResponse());
                case PlanetApi + "GetAllDefinitionsRequest":
                    var all = Planets.GetAllDefinitionsRequest.Parser.ParseFrom(payload);
                    var snapshot = directory.Read();
                    var version = Version(snapshot);
                    if (version.Equals(all.KnownVersion))
                    {
                        return new(HttpStatusCode.NotModified);
                    }
                    var response = new Planets.GetAllDefinitionsResponse { Version = version };
                    response.Definitions.Add(snapshot.Planets.Select(Definition));
                    return new(HttpStatusCode.OK, response);
                case PlanetApi + "GetDefinitionsVersionRequest":
                    var requested = Planets.GetDefinitionsVersionRequest.Parser.ParseFrom(payload);
                    var current = directory.Read();
                    if (!Version(current).Equals(requested.Version))
                    {
                        return new(HttpStatusCode.NotFound);
                    }
                    var historical = new Planets.GetDefinitionsVersionResponse();
                    historical.Definitions.Add(current.Planets.Select(Definition));
                    return new(HttpStatusCode.OK, historical);
                default:
                    return null;
            }
        }
        catch (InvalidProtocolBufferException)
        {
            return new(HttpStatusCode.BadRequest);
        }
    }

    private static Specification Version(CelestialResourceSnapshot snapshot)
    {
        var version = new Specification { Major = BuildContentPolicy.PinnedBuild };
        version.BuildTags.Add(snapshot.Revision);
        return version;
    }

    private static PlanetDefinition Definition(PlanetResourceDefinition planet)
    {
        var definition = new PlanetDefinition
        {
            Planet = new SequentialIdentifier { Sequential = checked((ulong)planet.PlanetId) },
            Power = checked((ulong)planet.Power),
            Workforce = checked((ulong)planet.Workforce),
        };
        if (planet.Reagent is { } reagent)
        {
            definition.ReagentDefinitions.Add(
                new PlanetReagentDefinition
                {
                    Type = new SequentialIdentifier { Sequential = checked((ulong)reagent.TypeId) },
                    ReagentType = new SequentialIdentifier { Sequential = checked((ulong)reagent.TypeId) },
                    Definition = new ReagentHarvestDefinition
                    {
                        AmountPerCycle = checked((uint)reagent.AmountPerCycle),
                        CyclePeriod = new Duration { Seconds = reagent.CycleSeconds },
                        SecuredCapacity = checked((uint)reagent.SecuredCapacity),
                        UnsecuredCapacity = checked((uint)reagent.UnsecuredCapacity),
                        // The SDE does not publish the security split. The Info window
                        // uses the total amount/period; no skyhook simulation is enabled.
                    },
                }
            );
        }
        return definition;
    }
}
