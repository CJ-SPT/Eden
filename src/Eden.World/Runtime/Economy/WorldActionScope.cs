using Eden.World.Runtime;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace Eden.World.Runtime.Economy;

internal sealed class WorldActionScope : IDisposable
{
    private readonly WorldDbContext? _sql;
    private readonly IDbContextTransaction? _transaction;
    private readonly EconomyTransaction? _resident;
    internal WorldAction World { get; }

    internal WorldActionScope(WorldDatabase database, TransactionParticipants? participants = null)
    {
        if (LiveWorld.Find(database.Path) is { } live)
        {
            _resident = live.Economy.Begin(participants);
            World = _resident;
        }
        else
        {
            _sql = new(database.Path) { Participants = participants };
            _transaction = _sql.Database.BeginTransaction();
            World = new SqlWorldAction(_sql);
        }
    }

    internal static T Inspect<T>(WorldDatabase database, Func<WorldAction, T> read)
    {
        if (LiveWorld.Find(database.Path) is { } live)
        {
            return live.Economy.Inspect(read);
        }
        using WorldDbContext world = new(database.Path) { Participants = TransactionParticipants.None };
        return read(new SqlWorldAction(world));
    }

    internal static void Inspect(WorldDatabase database, Action<WorldAction> read) =>
        Inspect(
            database,
            world =>
            {
                read(world);
                return true;
            }
        );

    internal void Commit()
    {
        if (_resident is not null)
        {
            _resident.Commit();
        }
        else
        {
            _sql!.SaveChanges();
            _transaction!.Commit();
            World.PublishActions();
        }
    }

    private bool _disposed;

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;
        _resident?.Dispose();
        _transaction?.Dispose();
        _sql?.Dispose();
    }
}
