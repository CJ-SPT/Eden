using System.Globalization;
using Eden.Protocol;
using Eden.Protocol.Docking;
using Eden.Protocol.Flight;
using Eden.Protocol.Mining;
using Eden.Server.Persistence;

namespace Eden.Server.Catalog;

[Injectable]
internal sealed class SystemObjects(WorldDatabase database, IAsteroidField asteroids) : ISystemObjects
{
    internal static FlightSystem FindSystem(CatalogDbContext catalog, int systemId) =>
        catalog.Set<SolarSystem>().Where(s => s.Id == systemId)
            .Select(s => new FlightSystem(s.Id, s.ConstellationId, s.Constellation.RegionId)).Single();

    public FlightSystem GetSystem(int systemId)
    {
        using CatalogDbContext catalog = new(database.CatalogPath);
        return FindSystem(catalog, systemId);
    }

    public IReadOnlyList<StaticSpaceObject> Load(int systemId)
    {
        using CatalogDbContext catalog = new(database.CatalogPath);
        var gates = catalog.Set<Stargate>().Where(g => g.SolarSystemId == systemId)
            .Select(g => new { g.Id, g.DestinationId, g.Destination.SolarSystemId }).ToDictionary(g => g.Id);
        var celestials = from body in catalog.Set<Celestial>()
                         join type in catalog.Set<ItemType>() on body.TypeId equals type.Id
                         where body.SolarSystemId == systemId
                         orderby body.Id
                         select new { Body = body, type.Group.CategoryId, Radius = body.Radius ?? type.Radius ?? 0 };
        var objects = new List<StaticSpaceObject>();
        foreach (var row in celestials)
        {
            var body = row.Body;
            gates.TryGetValue(body.Id, out var gate);
            objects.Add(new(body.Id, body.TypeId, body.GroupId, row.CategoryId, 1,
                new(body.X, body.Y, body.Z), (float)row.Radius, gate?.DestinationId, gate?.SolarSystemId));
        }
        objects.AddRange(catalog.Set<Station>().Where(s => s.SolarSystemId == systemId).OrderBy(s => s.Id)
            .Select(s => new StaticSpaceObject(s.Id, s.TypeId, s.Type.GroupId, s.Type.Group.CategoryId, s.OwnerId,
                new SpacePosition(s.X, s.Y, s.Z), (float)(s.Type.Radius ?? 0), null, null)));
        // The client keeps secondary suns and disrupted gates in system content, outside mapObjects.db.
        var content = catalog.Set<SourceRecord>().Find("client/solarsystemcontent", systemId.ToString(CultureInfo.InvariantCulture))
            ?.Read<ClientSolarsystemcontent>();
        if (content?.SecondarySun is { } sun)
        {
            AddSystemObject(checked((int)sun.ItemID!.Value), sun.TypeID, sun.Position);
        }
        if (content?.DisruptedStargates is { } disrupted)
        {
            foreach (var (key, gate) in disrupted)
            {
                AddSystemObject(int.Parse(key, CultureInfo.InvariantCulture), gate.TypeID, gate.Position);
            }
        }
        objects.AddRange(asteroids.List(systemId));
        return objects;

        void AddSystemObject(int id, long? typeId, ClientConstellationsCenter? point)
        {
            if (point is not { X: double x, Y: double y, Z: double z } || typeId is null)
            {
                throw new InvalidDataException($"Incomplete static object {id} in system {systemId}.");
            }
            var type = catalog.Set<ItemType>().Where(t => t.Id == typeId).Select(t => new
            {
                t.Id,
                t.GroupId,
                t.Group.CategoryId,
                t.Radius,
            }).Single();
            objects.Add(new(id, type.Id, type.GroupId, type.CategoryId, 1, new(x, y, z), (float)(type.Radius ?? 0)));
        }
    }
}
