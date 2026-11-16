using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Eden.Data.Catalog;
using Eden.Modding;
using Eden.World.Catalog;
using Microsoft.EntityFrameworkCore;

namespace Eden.World.Modding;

// Catalog composition only. Gameplay continues to use the existing resident gate-transfer operation.
public static class TravelRouteProjection
{
    private const int StargateGroup = 10;
    private const double MinimumClearance = 1_000_000;

    private enum MapJumpKind
    {
        System,
        Constellation,
        Region,
    }

    public static string Apply(string path, string overlay, IReadOnlyList<ModPackage> packages)
    {
        if (!packages.Any(package => package.Contents.ContainsKey(ModTravelRoutes.PackagePath)))
        {
            return overlay;
        }
        var document = JsonNode.Parse(overlay)!.AsObject();
        JsonObject systems = new();
        JsonObject content = new();
        JsonArray jumps = [];
        JsonObject locations = new();
        using CatalogDbContext catalog = new(path, writable: true);
        using var transaction = catalog.Database.BeginTransaction();
        catalog.Database.ExecuteSqlRaw("PRAGMA defer_foreign_keys=ON");
        var connections = catalog
            .Set<Stargate>()
            .Select(gate => new { From = gate.SolarSystemId, To = gate.Destination.SolarSystemId })
            .AsEnumerable()
            .Select(gate => (Math.Min(gate.From, gate.To), Math.Max(gate.From, gate.To)))
            .ToHashSet();
        foreach (var package in packages)
        {
            if (!package.Contents.TryGetValue(ModTravelRoutes.PackagePath, out var bytes))
            {
                continue;
            }
            if (package.Manifest.ApiVersion != ModFormat.TravelApiVersion)
            {
                throw new InvalidDataException("Travel route packages require mod API 1.1.0.");
            }
            var routes =
                JsonSerializer.Deserialize<ModTravelRoute[]>(bytes, ModFormat.Json)
                ?? throw new InvalidDataException("Missing travel routes.");
            ModTravelRoutes.Validate(routes);
            foreach (var route in routes)
            {
                if (!connections.Add((Math.Min(route.A.SystemId, route.B.SystemId), Math.Max(route.A.SystemId, route.B.SystemId))))
                {
                    throw new InvalidDataException("A travel route duplicates an existing system connection.");
                }
                Add(route.A, route.B);
                Add(route.B, route.A);
            }
        }
        transaction.Commit();
        document["travel"] = new JsonObject
        {
            ["systems"] = systems,
            ["content"] = content,
            ["jumps"] = jumps,
            ["locations"] = locations,
        };
        return document.ToJsonString();

        void Add(ModTravelEndpoint gate, ModTravelEndpoint exit)
        {
            var system = catalog.RequireSystem(gate.SystemId);
            var destination = catalog.RequireSystem(exit.SystemId);
            var type = catalog.RequireItem(gate.TypeId);
            if (type.GroupId != StargateGroup || catalog.FindCelestial(gate.GateId) is not null)
            {
                throw new InvalidDataException("Travel gates require a stargate type and an unused celestial ID.");
            }
            if (
                catalog
                    .CelestialsInSystem(gate.SystemId)
                    .Any(body =>
                        gate.Position.ToSpacePosition().DistanceTo(new(body.X, body.Y, body.Z)) < (body.Radius ?? 0) + MinimumClearance
                    )
                || catalog
                    .StationsInSystem(gate.SystemId)
                    .Any(station => gate.Position.ToSpacePosition().DistanceTo(new(station.X, station.Y, station.Z)) < MinimumClearance)
            )
            {
                throw new InvalidDataException("A travel gate overlaps an existing celestial or station.");
            }
            string id = gate.GateId.ToString(CultureInfo.InvariantCulture);
            string systemId = gate.SystemId.ToString(CultureInfo.InvariantCulture);
            var position = new JsonObject
            {
                ["x"] = gate.Position.X,
                ["y"] = gate.Position.Y,
                ["z"] = gate.Position.Z,
            };
            var systemData = Read("client/systems", systemId);
            var exitData = Read("client/systems", exit.SystemId.ToString(CultureInfo.InvariantCulture));
            var systemContent = Read("client/solarsystemcontent", systemId);
            var stargates = systemContent["stargates"]!.AsObject();
            // Preserve model orientation/animation fields from a gate in this system.
            var template = stargates.First().Value!.DeepClone().AsObject();
            template["typeID"] = gate.TypeId;
            template["destination"] = exit.GateId;
            template["position"] = position.DeepClone();
            stargates.Add(id, template);
            var neighbours = systemData["neighbours"]!.AsArray();
            var kind =
                system.RegionId != destination.RegionId ? MapJumpKind.Region
                : system.ConstellationId != destination.ConstellationId ? MapJumpKind.Constellation
                : MapJumpKind.System;
            int jumpType = (int)kind;
            neighbours.Add(
                new JsonObject
                {
                    ["solarSystemID"] = exit.SystemId,
                    ["stargateID"] = gate.GateId,
                    ["jumpType"] = jumpType,
                }
            );
            Write("client/systems", systemId, systemData);
            Write("client/solarsystemcontent", systemId, systemContent);
            var jump = new JsonObject
            {
                ["fromSystemID"] = gate.SystemId,
                ["toSystemID"] = exit.SystemId,
                ["stargateID"] = gate.GateId,
                ["jumpType"] = jumpType,
                ["jumpID"] = gate.GateId,
                ["fromCoordinate"] = systemData["center"]!.DeepClone(),
                ["toCoordinate"] = exitData["center"]!.DeepClone(),
            };
            Write("client/jumps", id, jump);
            catalog.Database.ExecuteSql(
                $"""
                INSERT INTO Celestial(Id,SolarSystemId,TypeId,GroupId,Radius,X,Y,Z)
                VALUES ({gate.GateId},{gate.SystemId},{gate.TypeId},{StargateGroup},{type.Radius},
                    {gate.Position.X},{gate.Position.Y},{gate.Position.Z});
                INSERT INTO Stargate(Id,SolarSystemId,DestinationId) VALUES ({gate.GateId},{gate.SystemId},{exit.GateId});
                """
            );
            // Keep only edited fields in the client overlay; original rows supply all other fields.
            systems[systemId] = new JsonObject { ["neighbours"] = neighbours.DeepClone() };
            if (content[systemId] is null)
            {
                content[systemId] = new JsonObject();
            }
            content[systemId]![id] = template.DeepClone();
            jumps.Add(jump);
            locations[id] = new JsonObject
            {
                ["systemID"] = gate.SystemId,
                ["typeID"] = gate.TypeId,
                ["name"] = "Stargate (" + destination.Name + ")",
                ["position"] = position,
            };
        }

        JsonObject Read(string dataset, string key) =>
            JsonNode.Parse(catalog.Set<SourceRecord>().Single(record => record.Dataset == dataset && record.Key == key).Data)!.AsObject();

        void Write(string dataset, string key, JsonNode value)
        {
            string json = value.ToJsonString();
            catalog.Database.ExecuteSql(
                $"""
                INSERT INTO SourceRecord(Dataset,Key,Data) VALUES ({dataset},{key},{json})
                ON CONFLICT(Dataset,Key) DO UPDATE SET Data=excluded.Data
                """
            );
        }
    }
}
