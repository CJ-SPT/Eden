using Eden.Gameplay.Combat;
using Eden.Gameplay.Dogma;
using Eden.Gameplay.Mining;
using Eden.Gameplay.Ships;
using Eden.Protocol;
using Eden.Protocol.Combat;
using Eden.Protocol.Docking;
using Eden.Protocol.Mining;
using Eden.Server.Catalog;
using Microsoft.EntityFrameworkCore;

namespace Eden.Server.Persistence;

public sealed class WorldNpc
{
    public long Id { get; set; }
    public int BeltId { get; set; }
    public int SystemId { get; set; }
    public int TypeId { get; set; }
    public int CorporationId { get; set; } = NpcEncounterDefinitions.GuristasCorporation;
    public int FactionId { get; set; }
    public NpcRole Role { get; set; }
    public NpcAnchorKind AnchorKind { get; set; }
    public long EncounterId { get; set; }
    public double? HomeX { get; set; }
    public double? HomeY { get; set; }
    public double? HomeZ { get; set; }
    public long? RetiredAt { get; set; }
    public double X { get; set; }
    public double Y { get; set; }
    public double Z { get; set; }
    public double ShieldCharge { get; set; }
    public double ArmorDamage { get; set; }
    public double StructureDamage { get; set; }
    public long UpdatedAt { get; set; }
    public long? DestroyedAt { get; set; }
    public double VelocityX { get; set; }
    public double VelocityY { get; set; }
    public double VelocityZ { get; set; }
    public long MotionAt { get; set; }
    public NpcNavigationState Navigation { get; set; }
}

[Injectable]
internal sealed class NpcEncounterStore(
    WorldDatabase database,
    INpcPopulationSeeder? population = null,
    NpcSpawnCatalog? spawnCatalog = null
) : INpcEncounters
{
    public IReadOnlyList<NpcContact> List(int systemId, long now)
    {
        lock (database.MotionLock)
        {
            return Read(systemId, now);
        }
    }

    private IReadOnlyList<NpcContact> Read(int systemId, long now)
    {
        using WorldDbContext world = new(database.Path);
        using CatalogDbContext catalog = new(database.CatalogPath);
        using var transaction = world.Database.BeginTransaction();
        long populationStamp = now / TimeSpan.TicksPerSecond;
        if (population is not null && database.PopulationStamps.GetValueOrDefault(systemId, -1) != populationStamp)
        {
            population.Seed(world, catalog, systemId, now);
        }
        world.SaveChanges();
        var npcs = world
            .Set<WorldNpc>()
            .Where(n => n.SystemId == systemId && n.DestroyedAt == null && n.RetiredAt == null)
            .OrderBy(n => n.Id)
            .ToArray();
        var ids = npcs.Select(n => n.Id).ToArray();
        var aggression = world.Set<NpcAggression>().AsNoTracking().Where(a => ids.Contains(a.NpcId)).ToDictionary(a => a.NpcId);
        var profiles = npcs.Select(n => n.TypeId).Distinct().ToDictionary(id => id, id => ReadProfile(catalog, id));
        NpcWeaponContact? Weapon(long npcId)
        {
            var attributes = profiles[npcs.Single(n => n.Id == npcId).TypeId].Attributes;
            double duration = attributes.GetValueOrDefault(DogmaAttributeId.WeaponCycleTime);
            return
                aggression.TryGetValue(npcId, out var active)
                && active.LastShotAt is long started
                && now >= started
                && now - started < duration * TimeSpan.TicksPerMillisecond
                ? new(active.TargetShipId, checked((int)attributes.GetValueOrDefault(DogmaAttributeId.EntityTurretType)), started, duration)
                : null;
        }
        var effective = npcs.ToDictionary(n => n.Id, n => PilotTackleStore.NpcAttributes(world, n.Id, profiles[n.TypeId].Attributes));
        foreach (var npc in npcs)
        {
            if (NpcMotion.Engaged(npc) && !aggression.ContainsKey(npc.Id))
            {
                NpcMotion.Return(npc);
            }
            if (npc.Navigation == NpcNavigationState.Returning && !database.MotionStamps.ContainsKey(systemId))
            {
                NpcMotion.Advance(npc, NpcMotion.Home(npc, catalog), profiles[npc.TypeId].Type, effective[npc.Id], now, returning: true);
            }
        }
        world.SaveChanges();
        var contacts = npcs.Select(n =>
            {
                var (type, attributes, defense) = profiles[n.TypeId];
                return new NpcContact(
                    n.Id,
                    n.TypeId,
                    type.GroupId,
                    type.Group.CategoryId,
                    n.CorporationId,
                    type.Name,
                    systemId,
                    new(n.X, n.Y, n.Z),
                    type.Mass ?? 0,
                    (float)(type.Radius ?? 0),
                    (float)effective[n.Id].GetValueOrDefault(DogmaAttributeId.MaxVelocity),
                    NpcMotion.Agility(attributes),
                    new(
                        n.Id,
                        defense,
                        new(
                            ChargedResource.Recharged(
                                n.ShieldCharge,
                                defense.Shield,
                                defense.ShieldRechargeMilliseconds,
                                Math.Max(0, (now - n.UpdatedAt) / (double)TimeSpan.TicksPerMillisecond)
                            ),
                            Math.Max(0, defense.Armor - n.ArmorDamage),
                            Math.Max(0, defense.Structure - n.StructureDamage)
                        )
                    ),
                    Weapon(n.Id),
                    NpcMotion.Contact(
                        n,
                        effective[n.Id],
                        aggression.GetValueOrDefault(n.Id)?.TargetShipId ?? 0,
                        NpcMotion.Home(n, catalog)
                    ),
                    database.MotionStamps.GetValueOrDefault(systemId),
                    database.CollisionStamps.GetValueOrDefault(n.Id, -1) == database.MotionStamps.GetValueOrDefault(systemId),
                    n.Role,
                    n.FactionId
                );
            })
            .ToArray();
        transaction.Commit();
        if (population is not null)
        {
            database.PopulationStamps[systemId] = populationStamp;
        }
        return contacts;
    }

    private sealed record Profile(ItemType Type, IReadOnlyDictionary<DogmaAttributeId, double> Attributes, HullDefense Defense);

    private Profile ReadProfile(CatalogDbContext catalog, int typeId)
    {
        if (population is NpcPopulationSeeder && spawnCatalog?.Profiles.FirstOrDefault(p => p.TypeId == typeId) is { } saved)
        {
            // Normal catalog inputs are immutable. Test fixtures deliberately
            // edit their catalogs, so their injected seeder retains fresh reads.
            ItemType cached = new()
            {
                Id = saved.TypeId,
                Name = saved.Name,
                GroupId = saved.GroupId,
                Mass = saved.Mass,
                Radius = saved.Radius,
                Group = new() { Id = saved.GroupId, CategoryId = NpcEncounterDefinitions.EntityCategory },
            };
            return new(cached, saved.Attributes, ShipHealth.DefenseFrom(saved.Attributes));
        }
        var type = catalog.Set<ItemType>().Include(t => t.Group).Single(t => t.Id == typeId);
        var attributes = PilotFittingStore.ReadAttributes(catalog, typeId);
        return new(type, attributes, ShipHealth.DefenseFrom(attributes));
    }

    public bool WasDestroyed(long npcId)
    {
        using WorldDbContext world = new(database.Path);
        return world.Set<WorldNpc>().Any(n => n.Id == npcId && n.DestroyedAt != null);
    }
}
