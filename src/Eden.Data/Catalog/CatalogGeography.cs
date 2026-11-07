namespace Eden.Data.Catalog;

public sealed record CatalogSystemDefinition(int Id, string Name, int ConstellationId, int RegionId);

public sealed record CatalogStationDefinition(
    int Id,
    int TypeId,
    int OwnerId,
    int SolarSystemId,
    int OperationId,
    int OrbitId,
    double X,
    double Y,
    double Z
);
