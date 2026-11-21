using Eden.Data.Catalog.Capsuleers;
using Eden.Data.World.Characters;
using Eden.Data.World.Inventory;
using Eden.Gameplay.Capsuleers;
using Eden.Gameplay.Configuration;
using Eden.Gameplay.Docking;
using Eden.Gameplay.Inventory;
using Eden.Gameplay.Ships;
using Eden.World.Capsuleers;
using Eden.World.Catalog;
using Eden.World.Runtime.Economy;

namespace Eden.Protocol.Tests.Capsuleers;

public sealed class PatrolCatalogTests
{
    [Fact]
    public void InstalledSnapshotSelectsAndValidatesEveryCombatCapablePatrolHull()
    {
        string? path = Environment.GetEnvironmentVariable("EDEN_ENCOUNTER_CATALOG");
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }
        using var catalog = new CatalogDbContext(path);
        var snapshot = TrafficSnapshot.Read(
            Path.Combine(Path.GetDirectoryName(path)!, "traffic.sqlite"),
            TrafficSnapshot.Fingerprint(catalog)
        );
        const int system = 30004759; // 1DQ1-A, nullsec.
        var pools = snapshot.PrepareCandidates(catalog.RequireSystem(system).RegionId, TrafficSecurityBand.Null, null, null);
        var imported = pools.Candidates(45, system, 0).First();
        Dictionary<int, TrafficProfile> fits = [];
        var policy = new CapsuleerPopulationPolicy();
        bool sawImport = false;
        for (int slot = 0; slot < 32_768; slot++)
        {
            var fit = CapsuleerPatrolFit.Select(catalog, policy, 45, system, slot, () => imported)!;
            if (fit.CharacterId > 0)
            {
                sawImport = true;
            }
            else
            {
                fits.TryAdd(fit.HullTypeId, fit);
            }
        }
        Assert.True(sawImport);
        using var blueprints = new CapsuleerShipBlueprints(catalog);
        const long pilot = 90_000_001;
        const long ship = 140_000_000_001;
        var station = catalog.Definitions.Stations.Keys.First();
        var origin = CapsuleerProvisioningCatalog.Origins(catalog)[0];
        List<string> failures = [];
        foreach (var fit in fits.Values)
        {
            using var world = ResidentEconomy.PreviewFit(catalog);
            world.Add(
                new Account
                {
                    Id = 1,
                    UserName = "Patrol test",
                    CloneGrade = AccountCloneGrade.Omega,
                }
            );
            world.Add(new InventoryOwner { Id = pilot });
            world.Add(
                new Ship
                {
                    Id = ship,
                    OwnerId = pilot,
                    StationId = station,
                    TypeId = fit.HullTypeId,
                    Name = "Patrol test",
                }
            );
            world.Add(
                new Pilot
                {
                    Id = pilot,
                    AccountId = 1,
                    RaceId = origin.RaceId,
                    ActiveShipId = ship,
                    Name = "Patrol test",
                }
            );
            world.SaveChanges();
            blueprints.Create(world, catalog, origin.RaceId, fit.HullTypeId, fit, pilot, ship, station, DateTime.UnixEpoch.ToFileTimeUtc());
            var profile = CapsuleerDefenseProfile.Read(world, catalog, pilot);
            bool offline = world
                .Set<OwnedItem>()
                .Where(item => item.LocationId == ship && !item.Online)
                .AsEnumerable()
                .Any(item => InventoryDefinitions.IsFittedSlot(item.FlagId));
            if (!profile.Combat || !profile.HasDamage || offline)
            {
                failures.Add(
                    $"{catalog.RequireItem(fit.HullTypeId).Name}: combat={profile.Combat}, damage={profile.HasDamage}, offline={offline}"
                );
            }
        }
        // Published build 3662168 hulls in these classes, less the restrictions in docs/protocol/capsuleers.md.
        var published = catalog
            .Definitions.Items.Values.Where(item =>
                item.CategoryId == (int)InventoryCategory.Ship
                && CapsuleerDefenseRules.PatrolWeaponSize((HullClass)item.GroupId) is not null
                && catalog.Read(CatalogData.Client.Types, (long)item.Id)?.Published == 1
            )
            .ToArray();
        var omitted = published.Where(hull => !fits.ContainsKey(hull.Id)).OrderBy(hull => hull.Name).Select(hull => hull.Name).ToArray();
        Assert.True(failures.Count == 0, string.Join(Environment.NewLine, failures));
        Assert.Equal(312, published.Length);
        Assert.Equal(296, fits.Count);
        string[] exceptions =
        [
            "Confessor",
            "Echelon",
            "Hecate",
            "Jackdaw",
            "Legion",
            "Loki",
            "Monitor",
            "Moreau YC128 Campaign Bus",
            "Proteus",
            "Roden YC128 Campaign Bus",
            "Skua",
            "Svipul",
            "Tengu",
            "Tenzin YC128 Campaign Bus",
            "Victorieux Luxury Yacht",
            "Zephyr",
        ];
        Assert.Equal(exceptions.Order(StringComparer.Ordinal), omitted.Order(StringComparer.Ordinal));
    }
}
