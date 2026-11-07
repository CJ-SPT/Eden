using System.Globalization;
using System.Text.Json;
using Eden.Data.World.Inventory;
using Eden.Gameplay.Agents;
using Eden.Gameplay.Characters;
using Eden.Gameplay.Combat;
using Eden.Gameplay.Contracts;
using Eden.Gameplay.Docking;
using Eden.Gameplay.Dogma;
using Eden.Gameplay.Industry;
using Eden.Gameplay.Inventory;
using Eden.Gameplay.Market;
using Eden.Gameplay.Skills;
using Microsoft.EntityFrameworkCore;

namespace Eden.Data.World.Characters;

public sealed class Account
{
    public string UserName { get; set; } = "";
    public long CreatedAt { get; set; }
    public long Id { get; set; }
    public AccountCloneGrade CloneGrade { get; set; } = AccountCloneGrade.Alpha;
}

public sealed class Pilot
{
    public long Id { get; set; }
    public long AccountId { get; set; }
    public Account Account { get; set; } = null!;
    public string Name { get; set; } = "";
    public int CorporationId { get; set; }
    public int RaceId { get; set; }
    public int BloodlineId { get; set; }
    public int GenderId { get; set; } = -1;
    public int AncestryId { get; set; }
    public int SchoolId { get; set; }
    public long CreatedAt { get; set; }
    public byte[]? Appearance { get; set; }
    public byte[]? Portrait { get; set; }
    public long ActiveShipId { get; set; }
    public ShipSafetyLevel SafetyLevel { get; set; } = ShipSafetyLevel.Full;
    public Ship ActiveShip { get; set; } = null!;
}

public sealed class SavedAgentContact
{
    public long PilotId { get; set; }
    public long AgentId { get; set; }
}

public sealed class SavedBlockedContact
{
    public long PilotId { get; set; }
    public long ContactId { get; set; }
}

public sealed class SavedBooster
{
    public long ItemId { get; set; }
    public long PilotId { get; set; }
    public int TypeId { get; set; }
    public int Slot { get; set; }
    public int Bonus { get; set; }
    public int RemainingInjectionUses { get; set; }
    public long StartedAt { get; set; }
    public long ExpiresAt { get; set; }

    public ActiveBooster Snapshot() => new(ItemId, TypeId, Slot, Bonus, StartedAt, ExpiresAt);
}

public sealed class SavedBoosterState
{
    public long PilotId { get; set; }
    public long Revision { get; set; }
}

public sealed class SavedCharacterDeletion
{
    public long PilotId { get; set; }
    public long ReadyAt { get; set; }
    public long? DeletedAt { get; set; }
}

public sealed class SavedCharacterSetting
{
    public long PilotId { get; set; }
    public string Key { get; set; } = "";
    public string? Text { get; set; }
    public byte[]? Binary { get; set; }
}

public sealed class SavedCharacterSheet
{
    public long PilotId { get; set; }
    public string Biography { get; set; } = "";
    public int Intelligence { get; set; } = CharacterAttributes.Default.Intelligence;
    public int Memory { get; set; } = CharacterAttributes.Default.Memory;
    public int Charisma { get; set; } = CharacterAttributes.Default.Charisma;
    public int Perception { get; set; } = CharacterAttributes.Default.Perception;
    public int Willpower { get; set; } = CharacterAttributes.Default.Willpower;
    public int BonusRemaps { get; set; } = CharacterSheetRules.StartingBonusRemaps;
    public long? NextTimedRemap { get; set; }

    public NeuralRemapState Snapshot() => new(new(Intelligence, Memory, Charisma, Perception, Willpower), BonusRemaps, NextTimedRemap);

    public void Apply(NeuralRemapState state)
    {
        Intelligence = state.Attributes.Intelligence;
        Memory = state.Attributes.Memory;
        Charisma = state.Attributes.Charisma;
        Perception = state.Attributes.Perception;
        Willpower = state.Attributes.Willpower;
        BonusRemaps = state.BonusRemaps;
        NextTimedRemap = state.NextTimedRemap;
    }
}

public sealed class SavedCombatDamage
{
    public long VictimId { get; set; }
    public long AttackerId { get; set; }
    public string PartyJson { get; set; } = "";
    public double Damage { get; set; }
}

public sealed class SavedCombatReport
{
    public long Id { get; set; }
    public long PilotId { get; set; }
    public string SourceKey { get; set; } = "";
    public long Time { get; set; }
    public bool IsLoss { get; set; }
    public string Data { get; set; } = "";

    public ShipCombatReport Snapshot() =>
        (JsonSerializer.Deserialize<ShipCombatReport>(Data) ?? throw new InvalidDataException("Invalid saved combat report.")) with
        {
            Id = Id,
        };
}

public sealed class SavedCompletedTask
{
    public long PilotId { get; set; }
    public long Id { get; set; }
    public long Time { get; set; }
}

public sealed class SavedContact
{
    public long PilotId { get; set; }
    public long ContactId { get; set; }
    public double Standing { get; set; }
    public bool InWatchlist { get; set; }
    public long LabelMask { get; set; }
}

public sealed class SavedContactLabel
{
    public long PilotId { get; set; }
    public long Id { get; set; }
    public string Name { get; set; } = "";
    public int? Color { get; set; }
}

public sealed class SavedHomeCloneTransfer
{
    public long Id { get; set; }
    public long PilotId { get; set; }
    public long OldShipId { get; set; }
    public long NewShipId { get; set; }
    public int SourceStationId { get; set; }
    public int DestinationStationId { get; set; }
    public long Time { get; set; }
}

public sealed class SavedHomeStation
{
    public long PilotId { get; set; }
    public int StationId { get; set; }
}

public sealed class SavedHomeStationPayment
{
    public long Id { get; set; }
    public long PilotId { get; set; }
    public int StationId { get; set; }
    public long OperatorId { get; set; }
    public long AmountCents { get; set; }
    public long BalanceCents { get; set; }
    public long Time { get; set; }

    public HomeStationPayment Snapshot() => new(Id, PilotId, StationId, OperatorId, AmountCents, BalanceCents, Time);
}

public sealed class SavedImplantState
{
    public long PilotId { get; set; }
    public long Revision { get; set; }
}

public sealed class SavedJumpClone
{
    public long Id { get; set; }
    public long PilotId { get; set; }
    public bool Active { get; set; }
    public int? StationId { get; set; }
    public string Name { get; set; } = "";
}

public sealed class SavedJumpClonePayment
{
    public long Id { get; set; }
    public long PilotId { get; set; }
    public int StationId { get; set; }
    public long OperatorId { get; set; }
    public JumpClonePaymentKind Kind { get; set; }
    public long AmountCents { get; set; }
    public long BalanceCents { get; set; }
    public long Time { get; set; }

    // Audit identities deliberately survive body/capsule deletion.
    public long CloneId { get; set; }
    public int? DestinationStationId { get; set; }
    public long? OldShipId { get; set; }
    public long? NewShipId { get; set; }

    public JumpClonePayment Snapshot() => new(Id, PilotId, StationId, OperatorId, Kind, AmountCents, BalanceCents, Time);
}

public sealed class SavedJumpCloneTimer
{
    public long PilotId { get; set; }
    public long Time { get; set; }
}

public sealed class SavedNpcStanding
{
    public long PilotId { get; set; }
    public long SourceId { get; set; }
    public double Value { get; set; }
}

public sealed class SavedPilotImplant
{
    public long ItemId { get; set; }
    public long CloneId { get; set; }
    public int TypeId { get; set; }
    public int Slot { get; set; }
    public DogmaAttributeId Attribute { get; set; }
    public int Bonus { get; set; }

    public InstalledImplant Snapshot() => new(ItemId, CloneId, new(TypeId, Slot, Attribute, Bonus));
}

public sealed class SavedPilotNote
{
    public long PilotId { get; set; }
    public long TargetId { get; set; }
    public string Text { get; set; } = "";
}

public sealed class SavedShipDirt
{
    public long ShipId { get; set; }
    public long Timestamp { get; set; }
}

public sealed class SavedStandingTransaction
{
    public long Id { get; set; }
    public long PilotId { get; set; }
    public long SourceId { get; set; }
    public long Time { get; set; }
    public StandingEvent Event { get; set; }
    public double Before { get; set; }
    public double After { get; set; }
    public int VictimCorporationId { get; set; }
    public int SystemId { get; set; }
    public int VictimTypeId { get; set; }

    public StandingTransaction Snapshot() =>
        new(Id, PilotId, SourceId, Time, Event, Before, After, VictimCorporationId, SystemId, VictimTypeId);
}

public sealed class SavedTaskCounter
{
    public long PilotId { get; set; }
    public string Name { get; set; } = "";
    public long Count { get; set; }
}
