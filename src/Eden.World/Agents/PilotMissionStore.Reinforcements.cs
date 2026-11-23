using Eden.Data.Catalog.Agents;
using Eden.Data.World.Agents;
using Eden.Gameplay.Agents;
using Eden.World.Catalog;
using Eden.World.Runtime;
using Eden.World.Runtime.Economy;
using Eden.World.Storage;

namespace Eden.World.Agents;

internal sealed partial class PilotMissionStore
{
    public void Advance()
    {
        if (LiveWorld.Find(database.Path) is not { } live)
        {
            return;
        }
        long now = Now;
        foreach (int system in live.Economy.SystemsWithDueMissionReinforcements(now))
        {
            LiveWorld.Execute(database.Path, system, () => AdvanceMissionReinforcements(database, system, now), nameof(Advance));
        }
    }

    internal static void AdvanceMissionReinforcements(WorldDatabase database, int system, long now)
    {
        if (LiveWorld.Find(database.Path) is not { } live || live.Economy.HasActiveAction)
        {
            return;
        }
        var due = live.Economy.DueMissionReinforcements(system, now);
        if (due.Length == 0)
        {
            return;
        }
        using var ownership = live.Enter();
        using var movement = database.EnterMovement();
        using var world = live.Economy.Begin(new(systems: [system]));
        using CatalogDbContext catalog = new(database.CatalogPath);
        foreach (var (id, wave) in due)
        {
            if (world.Set<SavedMissionReinforcement>().Find(id, wave) is not { } row)
            {
                continue;
            }
            if (world.Set<SavedMission>().Find(id) is not { State: MissionState.Accepted } mission)
            {
                world.Remove(row);
            }
            else if (!Expire(world, catalog, mission, now))
            {
                SettleReinforcement(world, catalog, mission, MissionCatalog.Find(catalog, mission.MissionId), row, now);
            }
        }
        world.Commit();
    }

    private static bool RoomStartsOnArrival(MissionProfile profile, int room) =>
        Enumerable
            .Range(0, profile.Definition.Waves.Length)
            .Any(w =>
                profile.WaveRoom(w) == room
                && profile.Definition.Waves[w].Trigger is MissionWaveTrigger.PreviousWaveCleared or MissionWaveTrigger.RoomArrivalDelay
            );

    internal static void StartMissionRoom(
        WorldAction world,
        CatalogDbContext catalog,
        SavedMission mission,
        MissionProfile profile,
        int room,
        long now
    )
    {
        if (world.Set<SavedMissionRoom>().Find(mission.Id, room) is not null || profile.Rooms.Length == 1 && mission.Wave >= 0)
        {
            return;
        }
        var timers = Enumerable
            .Range(0, profile.Definition.Waves.Length)
            .Where(w => IsArrivalTimer(profile, w) && profile.WaveRoom(w) == room)
            .ToArray();
        if (timers.Length > 0 && world is not EconomyTransaction)
        {
            throw new InvalidDataException("Timed mission encounters require the resident world.");
        }
        int first = NextNormalWave(profile, room, -1);
        if (first >= 0)
        {
            Spawn(world, catalog, mission, profile, first, now);
        }
        else if (timers.Length > 0)
        {
            world.Add(new SavedMissionRoom { MissionId = mission.Id, RoomId = room });
        }
        foreach (int wave in timers)
        {
            world.Add(
                new SavedMissionReinforcement
                {
                    MissionId = mission.Id,
                    Wave = wave,
                    SystemId = mission.SystemId,
                    DueAt = checked(now + profile.Definition.Waves[wave].DelayTicks),
                }
            );
        }
    }

    private static bool IsArrivalTimer(MissionProfile profile, int wave) =>
        profile.Definition.Waves[wave].Trigger == MissionWaveTrigger.RoomArrivalDelay;

    internal static bool WaveStarted(WorldAction world, SavedMission mission, MissionProfile profile, int wave) =>
        IsArrivalTimer(profile, wave)
            ? world.Filter<SavedMissionMember>(m => m.MissionId == mission.Id && m.Wave == wave).Any()
            : (
                world.Set<SavedMissionRoom>().Find(mission.Id, profile.WaveRoom(wave))?.Wave
                ?? (profile.Rooms.Length == 1 ? mission.Wave : -1)
            ) >= wave;

    internal static void SettleMissionReinforcements(
        WorldAction world,
        CatalogDbContext catalog,
        SavedMission mission,
        MissionProfile profile,
        long now
    )
    {
        if (world is not EconomyTransaction || !profile.Definition.Waves.Any(w => w.Trigger == MissionWaveTrigger.RoomArrivalDelay))
        {
            return;
        }
        var due = world
            .Filter<SavedMissionReinforcement>(r => r.MissionId == mission.Id && r.DueAt <= now)
            .OrderBy(r => r.DueAt)
            .ThenBy(r => r.Wave)
            .ToArray();
        foreach (var row in due)
        {
            SettleReinforcement(world, catalog, mission, profile, row, now);
        }
    }

    private static void SettleReinforcement(
        WorldAction world,
        CatalogDbContext catalog,
        SavedMission mission,
        MissionProfile profile,
        SavedMissionReinforcement row,
        long now
    )
    {
        if (
            row.SystemId != mission.SystemId
            || row.DueAt > now
            || row.Wave < 0
            || row.Wave >= profile.Definition.Waves.Length
            || !IsArrivalTimer(profile, row.Wave)
        )
        {
            throw new InvalidDataException("Saved mission reinforcement does not match its timed wave.");
        }
        Spawn(world, catalog, mission, profile, row.Wave, now);
        world.Remove(row);
    }
}
