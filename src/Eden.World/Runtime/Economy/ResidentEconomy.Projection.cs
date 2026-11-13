using System.Data.Common;
using Eden.Infrastructure.Diagnostics;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace Eden.World.Runtime.Economy;

internal sealed record EconomyTableProjection(IEconomyTable Table, IEconomyProjection Rows);

internal sealed partial class ResidentEconomy
{
    private readonly Dictionary<string, HashSet<string>> _projectionParents = new(StringComparer.Ordinal);
    private readonly Dictionary<string, HashSet<string>> _projectionChildren = new(StringComparer.Ordinal);

    private void LoadProjectionDependencies(WorldDbContext world)
    {
        _projectionParents.Clear();
        _projectionChildren.Clear();
        foreach (var entity in world.Model.GetEntityTypes())
        {
            if (entity.GetTableName() is not { } name)
            {
                continue;
            }
            _projectionParents.TryAdd(name, []);
            _projectionChildren.TryAdd(name, []);
            foreach (var key in entity.GetForeignKeys())
            {
                string parent = key.PrincipalEntityType.GetTableName()!;
                _projectionParents[name].Add(parent);
                if (!_projectionChildren.TryGetValue(parent, out var children))
                {
                    _projectionChildren.Add(parent, children = []);
                }
                children.Add(name);
            }
        }
        // Triggers can read or change tables not named in the original command.
        // Cache their dependencies once with the schema, outside warmed frames.
        using var command = world.Database.GetDbConnection().CreateCommand();
        command.CommandText = "SELECT tbl_name,sql FROM sqlite_schema WHERE type='trigger' AND sql IS NOT NULL";
        SessionPerformance.DatabaseCommand(command.CommandText);
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            if (_projectionParents.TryGetValue(reader.GetString(0), out var dependencies))
            {
                string sql = reader.GetString(1);
                foreach (string table in _projectionParents.Keys.Where(table => SqlTableReferences.Contains(sql, table)))
                {
                    dependencies.Add(table);
                }
            }
        }
    }

    private IEconomyTable[] ProjectionTables(IEnumerable<string> names, bool includeDependents)
    {
        HashSet<string> selected = new(names, StringComparer.Ordinal);
        HashSet<string> mutations = includeDependents ? new(selected, StringComparer.Ordinal) : [];
        Queue<string> pending = new(selected);
        while (pending.TryDequeue(out string? name))
        {
            if (_projectionParents.TryGetValue(name, out var parents))
            {
                foreach (string parent in parents.Where(selected.Add))
                {
                    pending.Enqueue(parent);
                }
            }
            // Pending deletes must preserve children moved away from their old parent.
            // SQL mutations also need current children for constraints and cascades.
            if (
                (mutations.Contains(name) || _tables.Values.Any(table => table.Name == name && table.HasDeletes))
                && _projectionChildren.TryGetValue(name, out var children)
            )
            {
                foreach (string child in children)
                {
                    if (selected.Add(child) || mutations.Add(child))
                    {
                        mutations.Add(child);
                        pending.Enqueue(child);
                    }
                }
            }
        }
        return _tables.Values.Where(table => selected.Contains(table.Name)).ToArray();
    }

    private EconomyCheckpoint ProjectionCheckpoint(IEnumerable<EconomyTableProjection> tables, IEnumerable<string> names)
    {
        var selected = names.ToHashSet(StringComparer.Ordinal);
        return new(
            tables.Select(table => table.Rows).Where(rows => rows.Count != 0).ToArray(),
            _dirtySequences.Where(selected.Contains).Select(name => new EconomySequence(name, _sequences[name])).ToArray()
        );
    }

    internal void BeforeSql(WorldDbContext context, DbCommand command)
    {
        if (!_loaded || _projecting || _dirtySequences.Count == 0 && !_tables.Values.Any(table => table.HasChanges))
        {
            return;
        }
        string sql = command.CommandText;
        bool schema = new[] { "CREATE ", "DROP ", "ALTER " }.Any(prefix =>
            sql.TrimStart().StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
        );
        var names = schema
            ? _tables.Values.Select(table => table.Name)
            : _projectionParents.Keys.Where(table => SqlTableReferences.Contains(sql, table));
        if (SqlTableReferences.Contains(sql, "sqlite_sequence"))
        {
            names = names.Concat(_dirtySequences);
        }
        if (context.Database.CurrentTransaction is null)
        {
            FlushTables(
                ProjectionTables(names, !sql.TrimStart().StartsWith("SELECT", StringComparison.OrdinalIgnoreCase))
                    .Select(table => table.Name)
            );
            return;
        }
        if (_transaction)
        {
            throw new InvalidOperationException("SQL cannot observe a speculative economic transaction.");
        }
        var state = context.EconomicProjection;
        var tables = ProjectionTables(names, !sql.TrimStart().StartsWith("SELECT", StringComparison.OrdinalIgnoreCase))
            .Where(table => !state.Tables.Contains(table.Name))
            .ToArray();
        var projections = tables.Select(table => new EconomyTableProjection(table, table.Capture())).ToArray();
        var snapshot = ProjectionCheckpoint(projections, tables.Select(table => table.Name));
        if (snapshot.Count != 0)
        {
            _projecting = true;
            try
            {
                using var projection = context.ProjectionContext();
                snapshot.Apply(projection);
            }
            finally
            {
                _projecting = false;
            }
        }
        state.Add(projections, snapshot.Sequences);
    }

    internal void CompleteProjection(WorldDbContext context)
    {
        foreach (var projection in context.EconomicProjection.Projections)
        {
            projection.Table.Projected(context, projection.Rows);
        }
        foreach (var sequence in context.EconomicProjection.Sequences)
        {
            _dirtySequences.Remove(sequence.Name);
        }
        context.EconomicProjection.Clear();
    }
}
