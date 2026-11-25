using System.Buffers.Binary;
using System.Net;
using System.Security.Cryptography;
using Eden.Gameplay.Inventory;
using Eden.Protocol.Gateway.PlexVault.Localization;
using Eden.Protocol.Gateway.PlexVault.Log;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using InvoiceAttributes = Eden.Protocol.Gateway.PlexVault.Invoices.Attributes;

namespace Eden.Protocol.Gateway;

internal static class PlexVaultLogRequests
{
    private const string Api = "eve_public.plex.vault.transaction.api.";

    // Build 3662168 UI/PlexVault/Logs categories and summaries. The client resolves the character parameter.
    private const uint Inventory = 1021278;
    private const uint Trading = 1021780;
    private const uint Deposited = 1021300;
    private const uint Withdrawn = 1021301;
    private const uint SentInTrade = 1021857;
    private const uint ReceivedInTrade = 1021771;

    internal static GatewayReply? Read(string type, ByteString payload, long? authenticatedAccount, IPlexVault vault)
    {
        if (type is not (Api + "GetAllLoggedForUserRequest" or Api + "GetLogRequest" or Api + "GetStatisticsRequest"))
        {
            return null;
        }
        if (authenticatedAccount is not long account || account <= 0)
        {
            return new(HttpStatusCode.Unauthorized);
        }
        try
        {
            switch (type)
            {
                case Api + "GetAllLoggedForUserRequest":
                    _ = GetAllLoggedForUserRequest.Parser.ParseFrom(payload);
                    GetAllLoggedForUserResponse history = new();
                    history.Transactions.AddRange(
                        vault.History(account).Select(row => new PlexVault.Transactions.Identifier { Sequential = row.Id })
                    );
                    return new(HttpStatusCode.OK, history);
                case Api + "GetLogRequest":
                    long id = GetLogRequest.Parser.ParseFrom(payload).Identifier?.Sequential ?? 0;
                    return id > 0 ? new(HttpStatusCode.OK, Details(vault.HistoryEntry(account, id))) : new(HttpStatusCode.BadRequest);
                default:
                    _ = GetStatisticsRequest.Parser.ParseFrom(payload);
                    return new(HttpStatusCode.OK, Statistics(vault.History(account)));
            }
        }
        catch (InvalidProtocolBufferException)
        {
            return new(HttpStatusCode.BadRequest);
        }
    }

    private static GetLogResponse Details(PlexVaultEntry? row)
    {
        if (row is null)
        {
            return new() { Unavailable = true };
        }

        var actor = new PublicCharacter.Identifier { Sequential = checked((ulong)row.PilotId) };
        var summary = Label(
            row.Kind switch
            {
                PlexVaultTransferKind.Deposit => Deposited,
                PlexVaultTransferKind.TradeSent => SentInTrade,
                PlexVaultTransferKind.TradeReceived => ReceivedInTrade,
                _ => Withdrawn,
            }
        );
        summary.Parameters.Add(new Parameter { Key = "characterID", Integer = actor.Sequential });
        PublicCharacter.Identifier? other = row.CounterpartyPilotId is long id ? new() { Sequential = checked((ulong)id) } : null;
        if (other is not null)
        {
            summary.Parameters.Add(new Parameter { Key = "otherCharacterID", Integer = other.Sequential });
        }
        InvoiceAttributes invoice = new() { Category = Label(Category(row)), SummaryMessage = summary };
        bool fromActor = row.Kind is PlexVaultTransferKind.Deposit or PlexVaultTransferKind.TradeSent;
        if ((fromActor ? actor : other) is { } source)
        {
            invoice.SourceCharacter = source;
        }
        else
        {
            invoice.NoSource = true;
        }
        if ((fromActor ? other : actor) is { } destination)
        {
            invoice.DestinationCharacter = destination;
        }
        else
        {
            invoice.NoDestination = true;
        }
        Span<byte> entryId = stackalloc byte[sizeof(long)];
        BinaryPrimitives.WriteInt64BigEndian(entryId, row.Id);
        return new()
        {
            Transaction = new()
            {
                Timestamp = Time(row.Time),
                AmountTransferred = Currency(row.Amount),
                ResultingBalance = Currency(row.Balance),
            },
            InvoiceEntry = new()
            {
                Attributes = invoice,
                Id = new() { Uuid = ByteString.CopyFrom(SHA256.HashData(entryId).AsSpan(0, 16)) },
            },
        };
    }

    private static GetStatisticsResponse Statistics(IReadOnlyList<PlexVaultEntry> rows)
    {
        GetStatisticsResponse response = new()
        {
            EarliestTransaction = rows.Count == 0 ? Timestamp.FromDateTime(DateTime.UnixEpoch) : Time(rows.Min(row => row.Time)),
        };
        response.Entries.AddRange(
            rows.GroupBy(Category)
                .OrderBy(group => group.Key)
                .Select(group => new GetStatisticsResponse.Types.Entry
                {
                    Category = Label(group.Key),
                    Incomes = Currency(group.Sum(row => Math.Max(0L, row.Amount))),
                    Expenses = Currency(group.Sum(row => -Math.Min(0L, row.Amount))),
                    TransactionsCount = (uint)group.Count(),
                })
        );
        return response;
    }

    private static uint Category(PlexVaultEntry row) =>
        row.Kind is PlexVaultTransferKind.MarketWithdrawal or PlexVaultTransferKind.TradeSent or PlexVaultTransferKind.TradeReceived
            ? Trading
            : Inventory;

    private static FormattedMessage Label(uint id) => new() { Identifier = new() { Sequential = id } };

    private static Timestamp Time(long timestamp) => Timestamp.FromDateTime(DateTime.FromFileTimeUtc(timestamp));

    private static Plex.Currency Currency(long amount) => new() { TotalInCents = checked(amount * PlexVaultRules.CentsPerPlex) };
}
