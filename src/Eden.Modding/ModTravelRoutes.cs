using Eden.Gameplay.Flight;

namespace Eden.Modding;

public sealed record ModTravelPosition(double X, double Y, double Z)
{
    public SpacePosition ToSpacePosition() => new(X, Y, Z);
}

public sealed record ModTravelEndpoint(int GateId, int SystemId, int TypeId, ModTravelPosition Position);

public sealed record ModTravelRoute(string Id, ModTravelEndpoint A, ModTravelEndpoint B);

public static class ModTravelRoutes
{
    public const string PackagePath = "travel-routes.json";

    // Reserve part of build 3662168's 50-million stargate item-ID range (idCheckers.IsStargate).
    public const int FirstGateId = 58_000_000;
    public const int GateEndId = 59_000_000;
    public const int MaximumRoutes = 1024;
    private const double MaximumCoordinate = 1e15;

    public static void Validate(IReadOnlyList<ModTravelRoute> routes)
    {
        if (routes.Count > MaximumRoutes)
        {
            throw new InvalidDataException("A mod may add at most 1024 travel routes.");
        }
        HashSet<string> names = [];
        HashSet<int> gates = [];
        HashSet<(int, int)> connections = [];
        foreach (var route in routes)
        {
            if (route is null || route.A is null || route.B is null)
            {
                throw new InvalidDataException("Travel routes require two endpoints.");
            }
            ModPackages.Identity(route.Id);
            if (
                !names.Add(route.Id)
                || route.A.SystemId == route.B.SystemId
                || !connections.Add((Math.Min(route.A.SystemId, route.B.SystemId), Math.Max(route.A.SystemId, route.B.SystemId)))
            )
            {
                throw new InvalidDataException("Travel route names and system pairs must be unique and connect different systems.");
            }
            foreach (var endpoint in new[] { route.A, route.B })
            {
                if (
                    endpoint.GateId is < FirstGateId or >= GateEndId
                    || !gates.Add(endpoint.GateId)
                    || endpoint.SystemId <= 0
                    || endpoint.TypeId <= 0
                    || endpoint.Position is null
                    || !double.IsFinite(endpoint.Position.X)
                    || !double.IsFinite(endpoint.Position.Y)
                    || !double.IsFinite(endpoint.Position.Z)
                    || Math.Abs(endpoint.Position.X) > MaximumCoordinate
                    || Math.Abs(endpoint.Position.Y) > MaximumCoordinate
                    || Math.Abs(endpoint.Position.Z) > MaximumCoordinate
                )
                {
                    throw new InvalidDataException("Invalid travel gate identity, type, system or position.");
                }
            }
        }
    }
}
