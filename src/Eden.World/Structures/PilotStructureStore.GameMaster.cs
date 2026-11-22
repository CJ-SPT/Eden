using Eden.Data.World.Structures;
using Eden.Gameplay.Structures;
using Eden.World.Catalog;
using Eden.World.Runtime;
using Eden.World.Runtime.Economy;
using Eden.World.Storage;

namespace Eden.World.Structures;

internal sealed partial class PilotStructureStore
{
    public void Execute(long structureId, StructureGameMasterCommand command)
    {
        var live = LiveWorld.Find(database.Path) ?? throw new InvalidDataException("Structure GM commands require a running world.");
        using var selection = live.EnterPersistence("structure GM command participants", TransactionParticipants.None);
        int system =
            live.Economy.Inspect(world => world.Structure(structureId)?.SystemId)
            ?? throw new InvalidDataException("The structure is unavailable.");
        using var world = live.Economy.Begin(new(systems: [system]));
        using CatalogDbContext catalog = new(database.CatalogPath);
        long now = Now;
        Advance(world, catalog, now, system);
        var structure = Require(world, structureId);
        switch (command)
        {
            case StructureGameMasterCommand.SetSkyhookState skyhook:
                if (structure.TypeId != Eden.Gameplay.Sovereignty.SovereigntyResourceRules.SkyhookTypeId)
                {
                    throw new InvalidDataException("Choose an Orbital Skyhook.");
                }
                SetOperatingState(
                    world,
                    catalog,
                    structure,
                    Eden.Gameplay.Sovereignty.SovereigntyResourceRules.StructureStateFor(skyhook.State),
                    now
                );
                break;
            case StructureGameMasterCommand.SetState { State: StructureState.Unanchored } when structure.State != StructureState.Unanchored:
                CheckGmDecommission(world, structure);
                structure.UnanchorAt = now;
                break;
            case StructureGameMasterCommand.SetState state:
                if (!Enum.IsDefined(state.State) || state.State == StructureState.Destroyed)
                {
                    throw new InvalidDataException("Unknown structure state.");
                }
                SetOperatingState(world, catalog, structure, state.State, now);
                if (!StructureRules.AllowsGmUnanchoring(state.State))
                {
                    structure.UnanchorAt = null;
                }
                StructureIndustry.Synchronize(world, catalog, structure, now);
                InterruptMoonExtraction(world, catalog, structure, now);
                break;
            case StructureGameMasterCommand.SetTimer timer:
                SetOperatingTimer(world, catalog, structure, now, Deadline(now, timer.Remaining));
                break;
            case StructureGameMasterCommand.SetDeployTimer timer:
                if (structure.State != StructureState.DeployVulnerable)
                {
                    throw new InvalidDataException("A deploy timer requires the deploy_vulnerable state.");
                }
                structure.DeployStartedAt = now;
                structure.DeployEndsAt = Deadline(now, timer.Remaining);
                break;
            case StructureGameMasterCommand.StartUnanchoring:
                CheckGmDecommission(world, structure);
                CheckGmUnanchorState(structure);
                structure.UnanchorAt ??= checked(
                    now
                    + StructureRules.Duration(
                        StructureRules.UnanchorDuration(StructureCatalog.Require(catalog, structure.TypeId)),
                        world.Settings.Structures.UnanchoringSpeed
                    )
                );
                break;
            case StructureGameMasterCommand.CancelUnanchoring:
                structure.UnanchorAt = null;
                break;
            case StructureGameMasterCommand.SetUnanchoringTimer timer:
                CheckGmDecommission(world, structure);
                CheckGmUnanchorState(structure);
                structure.UnanchorAt = Deadline(now, timer.Remaining);
                break;
            case StructureGameMasterCommand.SetAbandonTimer timer:
                bool fullPower = world.Filter<SavedStructureService>(m => m.StructureId == structure.Id && m.Online).Any();
                if (
                    !StructureRules.CanAbandon(structure.TypeId)
                    || StructureRules.Upkeep(structure.LowPowerAt, fullPower, now, structure.TypeId, structure.AbandonAt)
                        != StructureUpkeepState.LowPower
                )
                {
                    throw new InvalidDataException("Only structures eligible for abandonment in low power have an editable abandon timer.");
                }
                structure.AbandonAt = Deadline(now, timer.Remaining);
                break;
        }
        world.SaveChanges();
        if (
            structure.UnanchorAt <= now
            || structure.DeployEndsAt <= now
            || command is StructureGameMasterCommand.SetTimer { Remaining.Ticks: 0 }
        )
        {
            Advance(world, catalog, now, system);
        }
        world.Commit();
    }

    private static long Deadline(long now, TimeSpan remaining)
    {
        if (remaining < TimeSpan.Zero || remaining.Ticks > DateTime.SpecifyKind(DateTime.MaxValue, DateTimeKind.Utc).ToFileTimeUtc() - now)
        {
            throw new InvalidDataException("The remaining timer duration is outside the supported range.");
        }
        return now + remaining.Ticks;
    }

    private static void CheckGmUnanchorState(SavedStructure structure)
    {
        if (!StructureRules.AllowsGmUnanchoring(structure.State))
        {
            throw new InvalidDataException("The structure cannot begin decommissioning in its current state.");
        }
    }

    private static void CheckGmDecommission(WorldAction world, SavedStructure structure)
    {
        if (
            structure.TypeId == Eden.Gameplay.Sovereignty.EntosisRules.HubTypeId
            || world.Set<Eden.Data.World.Sovereignty.SavedSovereigntySkyhook>().Find(structure.Id) is not null
        )
        {
            throw new InvalidDataException("This structure uses the sovereignty decommissioning lifecycle.");
        }
    }

    private static void SetOperatingTimer(WorldAction world, CatalogDbContext catalog, SavedStructure structure, long now, long end)
    {
        var battle = StructureBattlePersistence.Read(world, catalog, structure);
        bool repair = StructureCombatRules.Vulnerable(structure.State) && structure.State != StructureState.Onlining;
        StructureBattlePersistence.Write(
            world,
            catalog,
            structure,
            battle with
            {
                StartedAt = now,
                EndsAt = repair ? null : end,
                RepairAt = repair ? end : null,
                PausedUntil = 0,
            }
        );
    }

    private static void SetOperatingState(
        WorldAction world,
        CatalogDbContext catalog,
        SavedStructure structure,
        StructureState state,
        long at,
        long? end = null
    )
    {
        var defense = StructureBattlePersistence.Profile(world, catalog, structure).Defense;
        StructureBattlePersistence.Write(
            world,
            catalog,
            structure,
            new(state, at, end, StructureCombatRules.StateHealth(state, defense), null, 0, new(0, 0, 0), 0)
        );
    }
}
