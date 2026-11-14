using Eden.Gameplay.Combat;
using Eden.Gameplay.Corporations;
using Eden.Gameplay.Loot;

namespace Eden.Protocol.Tests.Loot;

public sealed class LootRightsRulesTests
{
    [Theory]
    [InlineData(0.8, ShipSafetyLevel.Partial, true)]
    [InlineData(0.1, ShipSafetyLevel.Disabled, true)]
    [InlineData(0, ShipSafetyLevel.Full, false)]
    [InlineData(-0.8, ShipSafetyLevel.Full, false)]
    public void TheftDependsOnSystemSecurity(double security, ShipSafetyLevel safety, bool theft)
    {
        Assert.Equal(theft, LootRightsRules.CheckTake(false, security, safety));
        Assert.False(LootRightsRules.CheckTake(true, security, ShipSafetyLevel.Full));
    }

    [Theory]
    [InlineData(0.1)]
    [InlineData(0.5)]
    [InlineData(1)]
    public void FullSafetyBlocksUnauthorizedTaking(double security) =>
        Assert.Throws<InvalidDataException>(() => LootRightsRules.CheckTake(false, security, ShipSafetyLevel.Full));

    [Fact]
    public void AlphaRestrictionUsesTheExistingHighSecurityBoundary()
    {
        Assert.Throws<InvalidDataException>(() => LootRightsRules.CheckTake(false, 0.45, ShipSafetyLevel.Disabled, true));
        Assert.True(LootRightsRules.CheckTake(false, 0.44, ShipSafetyLevel.Partial, true));
        Assert.False(LootRightsRules.CheckTake(true, 1, ShipSafetyLevel.Disabled, true));
    }

    [Fact]
    public void TheftExtendsAnActiveCriminalFlagWithoutCreatingCombatTimers()
    {
        const long now = 1000;
        var before = CrimewatchTimers.Empty with { Criminal = new(now - 1, now + 1) };
        var after = LootRightsRules.Theft(before, now);
        Assert.Equal(now + CrimewatchRules.FlagDuration.Ticks, after.Criminal.ExpiresAt);
        Assert.Equal(before.Suspect, after.Suspect);
        Assert.Equal(before.Weapons, after.Weapons);
        Assert.Equal(before.Pvp, after.Pvp);
        Assert.Equal(before.Npc, after.Npc);
        Assert.True(LootRightsRules.Theft(before, now + 1).Suspect.Running(now + 1));
    }

    [Fact]
    public void PublicOwnerFleetAndPlayerCorporationRightsDoNotGrantNpcCorporationAccess()
    {
        const long pilot = 90000001;
        const int npcCorporation = 1000001;
        int corporation = CorporationRules.FirstCorporationId;
        Assert.True(LootRightsRules.CanTake(pilot, npcCorporation, new(pilot, npcCorporation, false), false));
        Assert.True(LootRightsRules.CanTake(pilot, npcCorporation, new(pilot + 1, npcCorporation, true), false));
        Assert.True(LootRightsRules.CanTake(pilot, npcCorporation, new(pilot + 1, npcCorporation, false), true));
        Assert.True(LootRightsRules.CanTake(pilot, corporation, new(pilot + 1, corporation, false), false));
        Assert.False(LootRightsRules.CanTake(pilot, npcCorporation, new(pilot + 1, npcCorporation, false), false));
        Assert.False(LootRightsRules.CanTake(pilot, corporation, new(pilot + 1, corporation + 1, false), false));
    }
}
