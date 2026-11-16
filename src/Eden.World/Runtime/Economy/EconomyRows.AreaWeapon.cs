using Eden.Data.World.Combat;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;

namespace Eden.World.Runtime.Economy;

internal sealed partial class ResidentEconomy
{
    private sealed record AreaWeaponRow(
        long Id,
        long PilotId,
        long SourceShipId,
        long ModuleId,
        int SystemId,
        long ResolvesAt,
        string Snapshot
    );

    private readonly System.Collections.Concurrent.ConcurrentDictionary<int, int> _areaWeaponSystems = new();

    internal bool HasAreaWeapons(int system) => _areaWeaponSystems.GetValueOrDefault(system) > 0;

    private void AreaWeaponChanged(AreaWeaponRow? before, AreaWeaponRow? after)
    {
        if (before?.SystemId == after?.SystemId)
        {
            return;
        }
        if (before is not null)
        {
            _areaWeaponSystems.AddOrUpdate(before.SystemId, 0, (_, count) => count - 1);
        }
        if (after is not null)
        {
            _areaWeaponSystems.AddOrUpdate(after.SystemId, 1, (_, count) => count + 1);
        }
    }

    private void RegisterAreaWeapons()
    {
        Register(
            new EconomyTable<SavedAreaWeapon, AreaWeaponRow>(
                "SavedAreaWeapon",
                row => new(row.Id),
                row => new(row.Id, row.PilotId, row.SourceShipId, row.ModuleId, row.SystemId, row.ResolvesAt, row.Snapshot),
                row => new SavedAreaWeapon
                {
                    Id = row.Id,
                    PilotId = row.PilotId,
                    SourceShipId = row.SourceShipId,
                    ModuleId = row.ModuleId,
                    SystemId = row.SystemId,
                    ResolvesAt = row.ResolvesAt,
                    Snapshot = row.Snapshot,
                },
                ProjectAreaWeapons,
                "Id,PilotId,SourceShipId,ModuleId,SystemId,ResolvesAt,Snapshot",
                reader =>
                    new(
                        reader.GetInt64(1),
                        reader.GetInt64(2),
                        reader.GetInt64(3),
                        reader.GetInt64(4),
                        reader.GetInt32(5),
                        reader.GetInt64(6),
                        reader.GetString(7)
                    ),
                row => new(row.Id),
                indexes: [new("SystemId", row => row.SystemId), new("ModuleId", row => row.ModuleId)],
                changed: AreaWeaponChanged,
                rowIdIsKey: true
            )
        );
    }

    private static void ProjectAreaWeapons(WorldDbContext world, IReadOnlyList<EconomyChange<AreaWeaponRow>> changes)
    {
        foreach (var change in changes)
        {
            if (change.Value is not { } row)
            {
                world.Database.ExecuteSql($"DELETE FROM SavedAreaWeapon WHERE Id={change.Key.Id}");
                continue;
            }
            world.Database.ExecuteSql(
                $"""
                INSERT INTO SavedAreaWeapon(Id,PilotId,SourceShipId,ModuleId,SystemId,ResolvesAt,Snapshot)
                VALUES({row.Id},{row.PilotId},{row.SourceShipId},{row.ModuleId},{row.SystemId},{row.ResolvesAt},{row.Snapshot})
                ON CONFLICT(Id) DO UPDATE SET ResolvesAt=excluded.ResolvesAt,Snapshot=excluded.Snapshot
                """
            );
        }
    }
}
