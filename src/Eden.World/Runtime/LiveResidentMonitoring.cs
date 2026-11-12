using Eden.Data.World.Combat;
using Eden.Data.World.Combat.Npcs;
using Eden.Data.World.Propulsion;
using Eden.Gameplay.Combat;
using Eden.Gameplay.Propulsion;
using Eden.World.Runtime.Economy;

namespace Eden.World.Runtime;

internal sealed partial class LiveReadModels
{
    private sealed record NpcEffectView(bool HasRows, IReadOnlyList<NpcEwarContact> Contacts, IReadOnlyList<NpcHostileAttempt> Attempts);

    private readonly ResidentSnapshot<long, NpcEffectView> _residentNpcTargets = new("resident.npcEffectTarget");
    private readonly ResidentSnapshot<long, IReadOnlyList<NpcEwarContact>> _residentNpcSources = new("resident.npcEffectSource");
    private readonly ResidentSnapshot<long, IReadOnlyList<TackleEffect>> _residentTackleTargets = new("resident.tackleTarget");
    private readonly ResidentSnapshot<long, IReadOnlyList<ElectronicWarfareContact>> _residentPlayerEwarTargets = new(
        "resident.playerEwarTarget"
    );

    private bool CanReadResident => owner.Economy.IsLoaded && owner.SqlConnections == 0;

    private T ReadResidentVersion<TKey, T>(ResidentSnapshot<TKey, T> cache, TKey key, Func<long> revision, Func<WorldAction, T> read)
        where TKey : notnull =>
        owner.Run(() =>
        {
            using var ownership = owner.CurrentSystem is null ? owner.EnterShared() : owner.Enter();
            if (owner.HasPersistenceOwnership && owner.Economy.HasActiveAction)
            {
                return owner.Economy.Inspect(read);
            }
            long version = revision();
            if (cache.TryRead(key, version, out var cached))
            {
                return cached;
            }
            var value = owner.Economy.Inspect(read);
            cache.Publish(
                key,
                value,
                version,
                checked(version + 1),
                checked(ResidentSnapshot<TKey, T>.MinimumEntries + owner.CapsuleerJobs.Count)
            );
            return value;
        });

    private bool ReadResidentGuard(Func<bool> committed, Func<WorldAction, bool> pending) =>
        owner.Run(() =>
        {
            using var ownership = owner.CurrentSystem is null ? owner.EnterShared() : owner.Enter();
            return owner.HasPersistenceOwnership && owner.Economy.HasActiveAction ? owner.Economy.Inspect(pending) : committed();
        });

    internal bool? ShipDestroyed(long ship) =>
        CanReadResident
            ? ReadResidentGuard(() => owner.Economy.HasShipLoss(ship), world => world.Set<ShipLoss>().Find(ship) is not null)
            : null;

    private NpcEffectView ResidentNpcTarget(long ship) =>
        ReadResidentVersion(
            _residentNpcTargets,
            ship,
            () => owner.Economy.NpcEwarTargetVersion(ship),
            world =>
            {
                var rows = world.Set<WorldNpcEwar>().Where(row => row.TargetShipId == ship).ToArray();
                return new(
                    rows.Length != 0,
                    Array.AsReadOnly(rows.Select(row => row.Contact()).OfType<NpcEwarContact>().ToArray()),
                    Array.AsReadOnly(
                        rows.Where(row => row.StartedAt > 0).Select(row => new NpcHostileAttempt(row.NpcId, row.StartedAt)).ToArray()
                    )
                );
            }
        );

    private IReadOnlyList<NpcEwarContact> ResidentNpcSource(long npc) =>
        ReadResidentVersion(
            _residentNpcSources,
            npc,
            () => owner.Economy.NpcEwarSourceVersion(npc),
            world =>
                Array.AsReadOnly(
                    world
                        .Set<WorldNpcEwar>()
                        .Where(row => row.NpcId == npc && row.AppliedJson != null)
                        .AsEnumerable()
                        .Select(row => row.Contact()!)
                        .ToArray()
                )
        );

    private IReadOnlyList<TackleEffect> ResidentTackleTarget(long ship) =>
        ReadResidentVersion(
            _residentTackleTargets,
            ship,
            () => owner.Economy.TackleTargetVersion(ship),
            world =>
                Array.AsReadOnly(
                    world
                        .Set<WorldTackle>()
                        .Where(row => row.TargetId == ship && row.TargetKind == TackleTargetKind.Ship)
                        .OrderBy(row => row.ModuleId)
                        .AsEnumerable()
                        .Select(row => row.Contact())
                        .ToArray()
                )
        );

    private IReadOnlyList<ElectronicWarfareContact> ResidentPlayerEwarTarget(long target) =>
        ReadResidentVersion(
            _residentPlayerEwarTargets,
            target,
            () => owner.Economy.PlayerEwarTargetVersion(target),
            world =>
                Array.AsReadOnly(
                    world
                        .Set<WorldPlayerEwar>()
                        .Where(row => row.TargetId == target)
                        .OrderBy(row => row.ModuleId)
                        .ThenBy(row => row.TargetId)
                        .AsEnumerable()
                        .Select(row => row.Contact())
                        .ToArray()
                )
        );

    private bool ResidentNpcMissileWork(long pilot, long now, int historySeconds) =>
        ReadResidentGuard(
            () =>
            {
                if (!owner.Economy.IsPilot(pilot))
                {
                    throw new InvalidDataException("Unknown character.");
                }
                return owner.Economy.HasNpcMissileWork(pilot, now - historySeconds * TimeSpan.TicksPerSecond);
            },
            world =>
            {
                if (!world.Pilots.Any(row => row.Id == pilot))
                {
                    throw new InvalidDataException("Unknown character.");
                }
                long oldest = now - historySeconds * TimeSpan.TicksPerSecond;
                return world
                    .Set<WorldMissile>()
                    .Any(row =>
                        row.SourceKind == MissileSourceKind.Npc
                        && (row.PilotId == pilot && row.Status == MissileFlightStatus.Flying || row.EndedAt < oldest)
                    );
            }
        );
}
