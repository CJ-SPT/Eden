using Eden.Gameplay.Characters;

namespace Eden.Protocol.Tests.Persistence;

public sealed class CharacterSheetRulesTests
{
    [Theory]
    [InlineData(16, 20, 19, 24, 20, "RespecAttributesTooLow")]
    [InlineData(28, 17, 17, 20, 17, "RespecAttributesTooHigh")]
    [InlineData(20, 20, 20, 20, 20, "RespecAttributesMisallocated")]
    [InlineData(17, 17, 17, 17, 17, "RespecAttributesMisallocated")]
    public void RejectsInvalidAllocation(int intelligence, int memory, int charisma, int perception, int willpower, string error)
    {
        var attributes = new CharacterAttributes(intelligence, memory, charisma, perception, willpower);
        Assert.Equal(
            error,
            Assert.Throws<InvalidCharacterSheet>(() => CharacterSheetRules.Remap(NeuralRemapState.Default, attributes, 0)).Message
        );
    }

    [Fact]
    public void TimedRemapPrecedesBonusAndBonusRetainsTimer()
    {
        CharacterAttributes first = new(17, 17, 17, 27, 21);
        CharacterAttributes second = new(27, 21, 17, 17, 17);
        var initial = NeuralRemapState.Default;
        Assert.Same(initial, CharacterSheetRules.Remap(initial, initial.Attributes, 1));
        var timed = CharacterSheetRules.Remap(initial, first, 1);
        Assert.Equal(2, timed.BonusRemaps);
        Assert.Equal(1 + TimeSpan.FromDays(365).Ticks, timed.NextTimedRemap);
        var bonus = CharacterSheetRules.Remap(timed, second, 2);
        Assert.Equal(1, bonus.BonusRemaps);
        Assert.Equal(timed.NextTimedRemap, bonus.NextTimedRemap);
        var last = CharacterSheetRules.Remap(bonus, first, 3);
        Assert.Equal(0, last.BonusRemaps);
        var error = Assert.Throws<InvalidCharacterSheet>(() => CharacterSheetRules.Remap(last, second, 4));
        Assert.Equal("RespecTooSoon", error.Message);
        Assert.Equal(timed.NextTimedRemap, error.NextTime);
        var renewed = CharacterSheetRules.Remap(last, second, last.NextTimedRemap!.Value);
        Assert.Equal(0, renewed.BonusRemaps);
        Assert.Equal(last.NextTimedRemap + CharacterSheetRules.RemapInterval.Ticks, renewed.NextTimedRemap);
    }
}
