using System.Collections;
using System.Linq.Expressions;

namespace Eden.World.Runtime.Economy;

internal sealed class EconomyIndex<T>(string name, Func<T, object?> value)
    where T : class
{
    private static readonly object NullKey = new();
    private readonly Dictionary<object, HashSet<EconomyKey>> _keys = [];
    internal string Name => name;

    internal IReadOnlyCollection<EconomyKey> Find(object? key) => _keys.TryGetValue(key ?? NullKey, out var found) ? found : [];

    internal void Clear() => _keys.Clear();

    internal void Add(EconomyKey id, T row)
    {
        object key = value(row) ?? NullKey;
        if (!_keys.TryGetValue(key, out var keys))
        {
            keys = [];
            _keys.Add(key, keys);
        }
        keys.Add(id);
    }

    internal void Remove(EconomyKey id, T row)
    {
        object key = value(row) ?? NullKey;
        if (_keys.TryGetValue(key, out var keys))
        {
            keys.Remove(id);
            if (keys.Count == 0)
            {
                _keys.Remove(key);
            }
        }
    }
}

internal sealed class EconomyPredicate(ParameterExpression row, ParameterExpression state) : ExpressionVisitor
{
    protected override Expression VisitMember(MemberExpression node) =>
        node.Expression == row ? Expression.Property(state, node.Member.Name) : base.VisitMember(node);

    protected override Expression VisitParameter(ParameterExpression node) =>
        node == row ? throw new InvalidOperationException("A row argument requires its linked private materialization.") : node;
}

internal static class EconomyCandidates
{
    internal static IReadOnlyCollection<EconomyKey>? Find(
        Expression expression,
        ParameterExpression row,
        Func<string, object?, IReadOnlyCollection<EconomyKey>?> lookup
    )
    {
        if (expression is BinaryExpression { NodeType: ExpressionType.AndAlso } and)
        {
            var left = Find(and.Left, row, lookup);
            var right = Find(and.Right, row, lookup);
            return left is null ? right
                : right is null || left.Count <= right.Count ? left
                : right;
        }
        if (expression is BinaryExpression { NodeType: ExpressionType.Equal } equal)
        {
            return Equal(equal.Left, equal.Right) ?? Equal(equal.Right, equal.Left);
        }
        if (expression is MethodCallExpression { Method.Name: "Contains" } contains)
        {
            var argument = contains.Arguments.LastOrDefault();
            var collection = contains.Object ?? contains.Arguments.FirstOrDefault();
            if (
                argument is MemberExpression { Expression: var source } member
                && source == row
                && collection is not null
                && Independent(collection, row)
            )
            {
                var values = Expression
                    .Lambda<Func<IEnumerable>>(Expression.Convert(collection, typeof(IEnumerable)))
                    .Compile(preferInterpretation: true)();
                HashSet<EconomyKey> keys = [];
                foreach (var value in values)
                {
                    if (lookup(member.Member.Name, value) is not { } found)
                    {
                        return null;
                    }
                    keys.UnionWith(found);
                }
                return keys;
            }
        }
        return null;

        IReadOnlyCollection<EconomyKey>? Equal(Expression first, Expression second)
        {
            if (first is not MemberExpression { Expression: var source } member || source != row || !Independent(second, row))
            {
                return null;
            }
            var value = Expression.Lambda<Func<object?>>(Expression.Convert(second, typeof(object))).Compile(preferInterpretation: true)();
            return lookup(member.Member.Name, value);
        }
    }

    private static bool Independent(Expression expression, ParameterExpression row)
    {
        var visitor = new Dependency(row);
        visitor.Visit(expression);
        return !visitor.Found;
    }

    private sealed class Dependency(ParameterExpression row) : ExpressionVisitor
    {
        internal bool Found { get; private set; }

        protected override Expression VisitParameter(ParameterExpression node)
        {
            Found |= node == row;
            return node;
        }
    }
}
