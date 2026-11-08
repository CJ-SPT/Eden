using Eden.Gameplay.Combat;
using Eden.Protocol.Combat;
using Eden.Protocol.Serialization;

namespace Eden.Protocol.Tests.Combat;

public sealed class CombatMessageTests
{
    [Theory]
    [InlineData(0, true)]
    [InlineData(0, false)]
    [InlineData(562, true)]
    [InlineData(562, false)]
    public void IncomingWeaponPresenceSelectsCompatibleHitAndMissTemplates(int weaponType, bool miss)
    {
        var quality = miss ? CombatHitQuality.Miss : CombatHitQuality.Standard;
        const int npcType = 3744; // Pinned Silverfish Alvi, a hull-integrated rogue-drone weapon.
        const long npcId = 7_200_000_000_000;
        IncomingCombatMessage incoming = new(
            quality == CombatHitQuality.Miss ? 0 : 12,
            quality,
            npcId,
            npcType,
            weaponType,
            new DamageVector(0, 1, 2, 3)
        );
        var arguments = Assert.IsType<object?[]>(BlueMarshal.Decode(BlueMarshal.Encode(new DamageMessageNotification(incoming).Arguments)));
        var fields = Assert.IsType<Dictionary<string, object?>>(Assert.Single(arguments));
        Assert.Equal(npcId, fields["source"]);
        Assert.Equal(npcId, fields["attackerID"]);
        Assert.Equal((long)quality, fields["hitQuality"]);
        Assert.Equal(weaponType > 0 ? "otherPlayer" : "otherPlayerWeapons", BlueMarshal.Text(fields["attackType"]));
        if (weaponType > 0)
        {
            Assert.Equal((long)weaponType, fields["weapon"]);
        }
        else
        {
            Assert.DoesNotContain("weapon", fields.Keys);
        }
    }
}
