using System.Text.Json;
using Eden.Gameplay.Sovereignty;
using Eden.Infrastructure.Diagnostics;
using Eden.World.Catalog;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Eden.Protocol.Tests.Sovereignty;

public sealed class SovereigntyCatalogTests
{
    [Theory]
    [InlineData(null, null, null, null, null, true)]
    [InlineData(40, null, null, null, null, false)]
    [InlineData(null, 1012, null, null, null, false)]
    [InlineData(65, 1657, null, null, null, true)]
    [InlineData(null, null, 500010L, null, null, false)]
    [InlineData(null, null, null, 500020L, null, false)]
    [InlineData(null, null, null, null, 500001L, false)]
    public void EligibilityUsesCatalogSecurityFactionAndAnchoringRestrictions(
        int? category,
        int? group,
        long? systemFaction,
        long? constellationFaction,
        long? regionFaction,
        bool claimable
    )
    {
        using TestWorld fixture = new(liveWorld: true);
        var database = new WorldDatabase(fixture.Path);
        const int system = 30003995;
        using (CatalogDbContext catalog = new(database.CatalogPath, writable: true))
        {
            string data = JsonSerializer.Serialize(
                new
                {
                    regionID = 10000050,
                    constellationID = 20000583,
                    securityStatus = -0.4,
                    factionID = systemFaction,
                    disallowedAnchorCategories = category is { } c ? new[] { c } : [],
                    disallowedAnchorGroups = group is { } g ? new[] { g } : [],
                }
            );
            catalog.Database.ExecuteSql(
                $"INSERT OR REPLACE INTO SourceRecord(Dataset,Key,Data) VALUES ('sde/mapSolarSystems',{system.ToString()},{data})"
            );
            Add("sde/mapConstellations", "20000583", new { regionID = 10000050, factionID = constellationFaction });
            Add("sde/mapRegions", "10000050", new { factionID = regionFaction });

            void Add(string dataset, string key, object value)
            {
                string payload = JsonSerializer.Serialize(value);
                catalog.Database.ExecuteSql($"INSERT OR REPLACE INTO SourceRecord(Dataset,Key,Data) VALUES ({dataset},{key},{payload})");
            }
        }
        CatalogProvider.Invalidate(database.CatalogPath);
        var claims = fixture.Services.GetRequiredService<ISovereignty>();
        var snapshot = Assert.IsType<SovereigntySystem>(claims.System(system));
        bool allowed = category != 40 && group != 1012;
        Assert.Equal(new(system, 10000050, -0.4, systemFaction ?? constellationFaction ?? regionFaction, allowed), snapshot);
        Assert.Equal(claimable, SovereigntyRules.CanClaim(snapshot));
        Assert.Null(claims.System(int.MaxValue));
        using var metrics = SessionPerformance.Begin("warm sovereignty catalog", _ => { }, force: true)!;
        Assert.Equal(snapshot, claims.System(system));
        Assert.Null(claims.System(int.MaxValue));
        Assert.Equal(0, metrics.Commands);
        Assert.Equal(0, metrics.Connections);
    }
}
