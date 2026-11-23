using System.Diagnostics;
using System.Text.Json;
using Eden.Data.World.Market;
using Eden.Gameplay.Market;
using Eden.Infrastructure.Diagnostics;
using Eden.World.Runtime.Combat;

namespace Eden.World.Runtime;

internal readonly record struct MarketLocation(int Station, int System, int Region, bool Bid, int TypeId);

internal sealed partial class LiveWorld
{
    private const int CachedWalletLimit = 512;
    private readonly Lock _walletBalanceGate = new();
    private readonly Dictionary<long, long> _walletBalances = [];
    private readonly Lock _loyaltyBalanceGate = new();
    private readonly Dictionary<long, IReadOnlyList<LoyaltyBalance>> _loyaltyBalances = [];

    internal long WalletBalance(long pilotId)
    {
        if (HasPersistenceOwnership && Economy.HasActiveAction)
        {
            return Economy.Inspect(world => world.Set<PilotWallet>().Find(pilotId)?.Cents ?? 0);
        }
        lock (_walletBalanceGate)
        {
            if (_walletBalances.TryGetValue(pilotId, out long cached))
            {
                return cached;
            }
        }
        // Load and publish under the same owner that observes committed wallet writes.
        // Cache hits need neither SQLite admission nor the owner held by the checkpoint copy.
        using var ownership = EnterShared("wallet balance");
        long balance = Economy.Inspect(world => world.Set<PilotWallet>().Find(pilotId)?.Cents ?? 0);
        lock (_walletBalanceGate)
        {
            if (_walletBalances.Count >= CachedWalletLimit)
            {
                _walletBalances.Clear();
            }
            _walletBalances[pilotId] = balance;
        }
        return balance;
    }

    private void ObserveWalletBalances(IReadOnlyCollection<CombatRowChange> changes)
    {
        if (!changes.Any(change => change.Table is "WorldWallet" or "schema"))
        {
            return;
        }
        lock (_walletBalanceGate)
        {
            if (changes.Any(change => change.Table == "schema"))
            {
                _walletBalances.Clear();
                return;
            }
            foreach (var change in changes)
            {
                if (change.Table == "WorldWallet" && _walletBalances.ContainsKey(change.Id))
                {
                    _walletBalances[change.Id] = Economy.Peek<PilotWallet>(change.Id)?.Cents ?? 0;
                }
            }
        }
    }

    internal IReadOnlyList<LoyaltyBalance> LoyaltyBalances(long pilotId)
    {
        if (_shared.Reads is null || HasOpenSqlTransaction || HasPersistenceOwnership && Economy.HasActiveAction)
        {
            return Run(Load);
        }
        lock (_loyaltyBalanceGate)
        {
            if (_loyaltyBalances.TryGetValue(pilotId, out var cached))
            {
                return cached;
            }
        }

        return Run(() =>
        {
            // Personal balances travel with the pilot. Load and publish under the owner that observes their writes.
            using var ownership = EnterShared("loyalty balance");
            var balances = Load();
            lock (_loyaltyBalanceGate)
            {
                if (_loyaltyBalances.Count >= CachedWalletLimit + CapsuleerJobs.Count)
                {
                    _loyaltyBalances.Clear();
                }
                _loyaltyBalances[pilotId] = balances;
            }
            return balances;
        });

        IReadOnlyList<LoyaltyBalance> Load() =>
            Economy.Inspect(world =>
            {
                if (!world.PilotExists(pilotId))
                {
                    throw new InvalidDataException("Unknown loyalty wallet owner.");
                }
                return Array.AsReadOnly(
                    world
                        .Filter<SavedLoyaltyBalance>(b => b.PilotId == pilotId && b.Points > 0)
                        .OrderBy(b => b.IssuerCorporationId)
                        .Select(b => new LoyaltyBalance(b.IssuerCorporationId, b.Points))
                        .ToArray()
                );
            });
    }

    private void ObserveLoyaltyBalances(IReadOnlyCollection<CombatRowChange> changes)
    {
        if (!changes.Any(change => change.Table is "WorldPilot" or "WorldLoyaltyWallet" or "schema"))
        {
            return;
        }
        lock (_loyaltyBalanceGate)
        {
            foreach (var change in changes)
            {
                if (change.Table == "schema" || change.Table == "WorldLoyaltyWallet" && !change.Resident)
                {
                    // SQL change notifications carry row IDs for the composite wallet key.
                    _loyaltyBalances.Clear();
                    return;
                }
                if (change.Table is "WorldPilot" or "WorldLoyaltyWallet")
                {
                    _loyaltyBalances.Remove(change.Id);
                }
            }
        }
    }

    // Resolve committed market membership once, on the already-open coordinated connection.
    // Unknown/deleted identities and definition changes retain conservative invalidation.
    private IReadOnlyList<MarketLocation>? MarketLocations(IReadOnlyCollection<CombatRowChange> changes)
    {
        if (changes.Any(c => c.Table is "schema" or "WorldMarketMembership"))
        {
            return null;
        }
        if (!MarketSummaries.HasViews && (InstitutionalSupply.Count == 0 || !changes.Any(c => c.Table == "WorldMarketDefinition")))
        {
            return [];
        }
        var npc = changes.Where(c => c.Table is "WorldMarketOrder" or "WorldRegionalMarketStock").Select(c => c.Id).Distinct().ToArray();
        var personal = changes.Where(c => c.Table == "WorldPersonalOrder").Select(c => c.Id).Distinct().ToArray();
        if (npc.Length + personal.Length == 0)
        {
            return [];
        }
        using var command = _memory.CreateCommand();
        command.CommandText = """
            SELECT StationId,SystemId,RegionId,Bid,TypeId FROM WorldMarketOrder WHERE Id IN (SELECT value FROM json_each($npc))
            UNION ALL
            SELECT StationId,SystemId,RegionId,Bid,TypeId FROM WorldPersonalOrder WHERE Id IN (SELECT value FROM json_each($personal))
            """;
        command.Parameters.AddWithValue("$npc", JsonSerializer.Serialize(npc));
        command.Parameters.AddWithValue("$personal", JsonSerializer.Serialize(personal));
        SessionPerformance.DatabaseCommand(command.CommandText);
        long started = Stopwatch.GetTimestamp();
        List<MarketLocation> locations = [];
        try
        {
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                locations.Add(new(reader.GetInt32(0), reader.GetInt32(1), reader.GetInt32(2), reader.GetBoolean(3), reader.GetInt32(4)));
            }
        }
        finally
        {
            SessionPerformance.DatabaseElapsed(Stopwatch.GetElapsedTime(started));
        }
        return locations.Count == npc.Length + personal.Length ? locations : null;
    }
}
