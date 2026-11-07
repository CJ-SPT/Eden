namespace Eden.Data.Catalog;

public sealed record CatalogSystemDefinition(int Id, string Name, int ConstellationId, int RegionId);

public sealed record CatalogConstellationDefinition(int Id, string Name, int RegionId);

public sealed record CatalogGateDefinition(int Id, int SolarSystemId, int DestinationId, int DestinationSystemId);

public sealed record CatalogCelestialDefinition(
    int Id,
    int SolarSystemId,
    int TypeId,
    int GroupId,
    double? Radius,
    int? OrbitId,
    double X,
    double Y,
    double Z
);

public sealed record CatalogStationDefinition(
    int Id,
    int TypeId,
    int OwnerId,
    int SolarSystemId,
    int OperationId,
    int OrbitId,
    double X,
    double Y,
    double Z,
    int RegionId
);
