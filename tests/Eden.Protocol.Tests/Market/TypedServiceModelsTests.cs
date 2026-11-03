using Eden.Gameplay.Dogma;
using Eden.Gameplay.Inventory;
using Eden.Protocol.Docking;
using Eden.Protocol.Industry;
using Eden.Protocol.Market;
using Eden.Protocol.Serialization;
using Eden.Protocol.Skills;

namespace Eden.Protocol.Tests.Market;

public sealed class TypedServiceModelsTests
{
    [Fact]
    public void WalletEntriesExposeSortableDatesAndPreserveTheJournalWireRecord()
    {
        WalletJournalEntry entry = new(42, 123, WalletEntryType.MarketTransaction, 90000001, 1000049, 34, -125, 50000);
        Assert.Equal(123, entry.Time);
        Equal(
            new BlueInstance(
                "utillib.KeyVal",
                new Dictionary<string, object?>
                {
                    ["transactionID"] = 42L,
                    ["transactionDate"] = 123L,
                    ["entryTypeID"] = (int)WalletEntryType.MarketTransaction,
                    ["ownerID1"] = 90000001L,
                    ["ownerID2"] = 1000049L,
                    ["referenceID"] = 34L,
                    ["amount"] = -1.25,
                    ["balance"] = 500d,
                    ["description"] = null,
                }
            ),
            entry
        );
        DateTime now = new(2026, 11, 2, 12, 0, 0, DateTimeKind.Utc);
        Assert.True(new WalletJournalFilter(null, null).Includes(now.AddDays(-30).ToFileTimeUtc(), now));
        Assert.False(new WalletJournalFilter(null, null).Includes(now.AddDays(-30).AddTicks(-1).ToFileTimeUtc(), now));
        Assert.True(new WalletJournalFilter(2026, 11).Includes(now.ToFileTimeUtc(), now));
        Assert.False(new WalletJournalFilter(2026, 10).Includes(now.ToFileTimeUtc(), now));
    }

    [Fact]
    public void BrainEffectsKeepNewStyleObjectsAndNamedGrayMatterCollections()
    {
        BrainEffect effect = new(
            10,
            BrainModifier.LocationRequiredSkill,
            DogmaAttributeId.WeaponCycleTime,
            BrainOperation.PostPercent,
            [3300],
            [3300],
            -6
        );
        Equal(
            new BlueNewObject(
                BlueNewObject.BrainEffectClass,
                new Dictionary<string, object?>
                {
                    ["fromAttrib"] = null,
                    ["toItemID"] = 10L,
                    ["modifierType"] = "LRS",
                    ["toAttribID"] = (int)DogmaAttributeId.WeaponCycleTime,
                    ["operation"] = 6,
                    ["extras"] = new object?[] { 3300 },
                    ["skills"] = new List<object?> { 3300 },
                    ["value"] = -6d,
                }
            ),
            effect
        );
        PilotBrainState state = new(123, [], [effect], []);
        Assert.Single(state.ShipEffects);
        var update = state.Update();
        Assert.Equal(state.Version, update.Version);
        Assert.Equal(
            BlueMarshal.Encode(
                new object?[]
                {
                    new List<object?>(),
                    new List<object?> { effect },
                    new List<object?>(),
                }
            ),
            update.GrayMatter
        );
    }

    [Fact]
    public void ManufacturingLocationsKeepTheNativeNewStyleClass()
    {
        IndustryFacility facility = new(60003760, 1529, 1000035, 30000142);
        Equal(
            new BlueNewObject(
                BlueNewObject.IndustryLocationClass,
                new Dictionary<string, object?>
                {
                    ["itemID"] = 60003760,
                    ["typeID"] = 1529,
                    ["ownerID"] = 90000001L,
                    ["flagID"] = (int)InventoryFlag.Hangar,
                    ["canView"] = true,
                    ["canTake"] = true,
                    ["solarSystemID"] = 30000142,
                }
            ),
            new IndustryLocationReply(facility, 90000001)
        );
        Equal(
            new object?[]
            {
                42L,
                new Dictionary<long, object?> { [34] = 27L },
            },
            new IndustryMonitorReply(42, new Dictionary<int, long> { [34] = 27 })
        );
    }

    [Fact]
    public void SkillQueueRequestParsesTypedTargetsAndRejectsBrokenPositions()
    {
        var request = SkillQueueRequest.Read(
            [new Dictionary<long, object?> { [0] = new object?[] { 3300L, 3L } }],
            new() { ["activate"] = 1L }
        );
        Assert.True(request!.Activate);
        Assert.Equal(new TrainingTarget(3300, 3), Assert.Single(request.Targets));
        Assert.Throws<InvalidTrainingQueue>(() =>
            SkillQueueRequest.Read([new Dictionary<long, object?> { [1] = new object?[] { 3300L, 3L } }], null)
        );
        Assert.Null(SkillQueueRequest.Read([], null));
    }

    private static void Equal(object original, IBlueWritable model) =>
        Assert.Equal(BlueMarshal.Encode(original), BlueMarshal.Encode(model));
}
