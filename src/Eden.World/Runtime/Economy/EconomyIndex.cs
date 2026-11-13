using System.Collections;
using System.Linq.Expressions;

namespace Eden.World.Runtime.Economy;

internal enum EconomyIndexMember
{
    FullKey,
    RowId,
}

internal sealed class EconomyIndex<T>(
    string name,
    Func<T, object?> value,
    IEqualityComparer<object>? comparer = null,
    EconomyIndexMember member = EconomyIndexMember.FullKey
)
    where T : class
{
    private static readonly object NullKey = new();
    private readonly Dictionary<object, IBucket> _keys = new(comparer);
    internal string Name => name;

    internal IReadOnlyCollection<EconomyKey> Find(object? key) => _keys.TryGetValue(key ?? NullKey, out var found) ? found : [];

    internal void Clear() => _keys.Clear();

    internal void Add(EconomyKey id, T row) => Add(id, value(row) ?? NullKey);

    private void Add(EconomyKey id, object key)
    {
        if (!_keys.TryGetValue(key, out var keys))
        {
            _keys.Add(
                key,
                member switch
                {
                    EconomyIndexMember.RowId => new Bucket<long, RowId>(id),
                    _ => new Bucket<EconomyKey, FullKey>(id),
                }
            );
            return;
        }
        keys.Add(id);
    }

    internal void Remove(EconomyKey id, T row) => Remove(id, value(row) ?? NullKey);

    private void Remove(EconomyKey id, object key)
    {
        if (_keys.TryGetValue(key, out var keys) && keys.Remove(id))
        {
            _keys.Remove(key);
        }
    }

    internal void Replace(EconomyKey id, T before, T after)
    {
        object old = value(before) ?? NullKey;
        object next = value(after) ?? NullKey;
        if (!Equals(old, next))
        {
            Remove(id, old);
            Add(id, next);
        }
    }

    private interface IBucket : IReadOnlyCollection<EconomyKey>
    {
        void Add(EconomyKey id);
        bool Remove(EconomyKey id);
    }

    private interface IMember<TMember>
    {
        static abstract TMember Pack(EconomyKey key);
        static abstract EconomyKey Unpack(EconomyKey first, TMember member);
    }

    private readonly struct FullKey : IMember<EconomyKey>
    {
        public static EconomyKey Pack(EconomyKey key) => key;

        public static EconomyKey Unpack(EconomyKey first, EconomyKey member) => member;
    }

    private readonly struct RowId : IMember<long>
    {
        public static long Pack(EconomyKey key) => key.Id;

        public static EconomyKey Unpack(EconomyKey first, long member) => new(member);
    }

    // Allocate a set only when more than one row shares a membership value.
    private sealed class Bucket<TMember, TFormat>(EconomyKey first) : IBucket
        where TFormat : struct, IMember<TMember>
    {
        private EconomyKey _first = first;
        private HashSet<TMember>? _many;
        public int Count => _many?.Count ?? 1;

        public void Add(EconomyKey id)
        {
            if (_many is not null)
            {
                _many.Add(TFormat.Pack(id));
            }
            else if (id != _first)
            {
                _many = [TFormat.Pack(_first), TFormat.Pack(id)];
            }
        }

        public bool Remove(EconomyKey id)
        {
            if (_many is null)
            {
                return _first == id;
            }
            _many.Remove(TFormat.Pack(id));
            if (_many.Count == 1)
            {
                _first = TFormat.Unpack(_first, _many.Single());
                _many = null;
            }
            return false;
        }

        public IEnumerator<EconomyKey> GetEnumerator()
        {
            if (_many is not null)
            {
                foreach (var id in _many)
                {
                    yield return TFormat.Unpack(_first, id);
                }
            }
            else
            {
                yield return _first;
            }
        }

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
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
