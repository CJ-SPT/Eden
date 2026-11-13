using Eden.World.Runtime.Economy;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace Eden.World.Storage;

public sealed partial class WorldDbContext
{
    internal EconomicProjectionState EconomicProjection { get; } = new();
    private IDisposable? _projectionScope;
    private WorldDbContext? _projectionOwner;

    internal WorldDbContext ProjectionContext()
    {
        WorldDbContext projection = new((SqliteConnection)Database.GetDbConnection()) { _projectionOwner = this };
        projection.Database.UseTransaction(Database.CurrentTransaction!.GetDbTransaction());
        projection._projectionScope = WorldConnections.Projecting(Database.GetDbConnection());
        return projection;
    }

    public override void Dispose()
    {
        try
        {
            base.Dispose();
        }
        finally
        {
            Interlocked.Exchange(ref _projectionScope, null)?.Dispose();
        }
    }

    public override async ValueTask DisposeAsync()
    {
        try
        {
            await base.DisposeAsync().ConfigureAwait(false);
        }
        finally
        {
            Interlocked.Exchange(ref _projectionScope, null)?.Dispose();
        }
    }
}

internal sealed class EconomicProjectionState
{
    internal HashSet<string> Tables { get; } = [];
    internal List<EconomyTableProjection> Projections { get; } = [];
    internal List<EconomySequence> Sequences { get; } = [];
    internal HashSet<object> Residents { get; } = new(ReferenceEqualityComparer.Instance);
    private readonly List<(object Owner, Action Commit)> _residents = [];

    private sealed record SavedProjection(string Name, int Tables, int Sequences, int Residents);

    private readonly List<SavedProjection> _savepoints = [];

    internal void Add(EconomyTableProjection[] projections, IReadOnlyList<EconomySequence> sequences)
    {
        Projections.AddRange(projections);
        Sequences.AddRange(sequences);
        Tables.UnionWith(projections.Select(projection => projection.Table.Name));
    }

    internal void AddResident(object owner, Action commit)
    {
        Residents.Add(owner);
        _residents.Add((owner, commit));
    }

    internal void CommitResidents()
    {
        foreach (var resident in _residents)
        {
            resident.Commit();
        }
    }

    internal void Savepoint(string operation, string name)
    {
        if (operation == "BEGIN")
        {
            _savepoints.Add(new(name, Projections.Count, Sequences.Count, _residents.Count));
            return;
        }
        int index = _savepoints.FindLastIndex(saved => saved.Name == name);
        if (index < 0)
        {
            return;
        }
        var saved = _savepoints[index];
        if (operation == "RELEASE")
        {
            _savepoints.RemoveRange(index, _savepoints.Count - index);
            return;
        }
        _savepoints.RemoveRange(index + 1, _savepoints.Count - index - 1);
        Projections.RemoveRange(saved.Tables, Projections.Count - saved.Tables);
        Sequences.RemoveRange(saved.Sequences, Sequences.Count - saved.Sequences);
        _residents.RemoveRange(saved.Residents, _residents.Count - saved.Residents);
        Residents.Clear();
        Residents.UnionWith(_residents.Select(resident => resident.Owner));
        Tables.Clear();
        Tables.UnionWith(Projections.Select(projection => projection.Table.Name));
    }

    internal void Clear()
    {
        Tables.Clear();
        Projections.Clear();
        Sequences.Clear();
        Residents.Clear();
        _residents.Clear();
        _savepoints.Clear();
    }
}
