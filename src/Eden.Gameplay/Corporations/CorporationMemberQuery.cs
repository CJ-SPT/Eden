using Eden.Gameplay.Characters;

namespace Eden.Gameplay.Corporations;

// Build 3662168 corp_ui_member_find query operators.
public enum CorporationQueryJoin
{
    Or = 1,
    And = 2,
}

public enum CorporationQueryOperator
{
    Equal = 1,
    Greater = 2,
    GreaterOrEqual = 3,
    Less = 4,
    LessOrEqual = 5,
    NotEqual = 6,
    HasBit = 7,
    NotHasBit = 8,
    Contains = 9,
    Like = 10,
    StartsWith = 11,
    EndsWith = 12,
    Is = 13,
}

public enum CorporationQueryField
{
    Roles,
    RolesAtHQ,
    RolesAtBase,
    RolesAtOther,
    GrantableRoles,
    GrantableRolesAtHQ,
    GrantableRolesAtBase,
    GrantableRolesAtOther,
    Base,
    Joined,
    Name,
    Titles,
}

public sealed record CorporationMemberCriterion(
    CorporationQueryJoin? Join,
    CorporationQueryField Field,
    CorporationQueryOperator Operator,
    long? Number = null,
    string? Text = null
);

public sealed record CorporationMemberQuery(IReadOnlyList<CorporationMemberCriterion> Criteria, bool IncludeImplied, bool SearchTitles);

public interface ICorporationMemberQueries
{
    IReadOnlyList<long> Query(long actorId, CorporationMemberQuery query);
    IReadOnlyList<long> AboveAverageShareholders(long actorId);
    IReadOnlyList<CorporationMemberTracking> Tracking(long actorId, bool full);
}

public sealed record CorporationMemberTracking(CorporationMember Member, PilotActivity Activity, long? LocationId, int? ShipTypeId);

public static class CorporationMemberQueryRules
{
    public const int MaximumCriteria = 64;
    public const int MaximumText = 100;

    public static void Validate(CorporationMemberQuery query)
    {
        if (query.Criteria.Count > MaximumCriteria)
        {
            throw new InvalidDataException("A member query can contain at most 64 conditions.");
        }
        for (int index = 0; index < query.Criteria.Count; index++)
        {
            var term = query.Criteria[index];
            bool validOperator = term.Field switch
            {
                CorporationQueryField.Name => term.Operator is >= CorporationQueryOperator.Contains and <= CorporationQueryOperator.Is,
                CorporationQueryField.Base => term.Operator is CorporationQueryOperator.Equal or CorporationQueryOperator.NotEqual,
                CorporationQueryField.Joined => term.Operator is >= CorporationQueryOperator.Equal and <= CorporationQueryOperator.NotEqual,
                _ => term.Operator is CorporationQueryOperator.HasBit or CorporationQueryOperator.NotHasBit,
            };
            if (
                !Enum.IsDefined(term.Field)
                || !validOperator
                || (index == 0 ? term.Join is not null : term.Join is null || !Enum.IsDefined(term.Join.Value))
                || (
                    term.Field == CorporationQueryField.Name
                        ? term.Number.HasValue || term.Text is null || term.Text.Length > MaximumText || term.Text.Any(char.IsControl)
                        : term.Text is not null || term.Number < 0 || term.Field != CorporationQueryField.Base && term.Number is null
                )
            )
            {
                throw new InvalidDataException("Invalid member query condition.");
            }
        }
    }

    public static bool Matches(
        CorporationMember member,
        IReadOnlyList<CorporationMemberCriterion> criteria,
        IReadOnlyList<string>? names = null
    )
    {
        if (criteria.Count == 0)
        {
            return false;
        }
        // The native builder has AND/OR with no parentheses; preserve AND precedence.
        bool group = true;
        bool result = false;
        foreach (var term in criteria)
        {
            if (term.Join == CorporationQueryJoin.Or)
            {
                result |= group;
                group = true;
            }
            group &= Matches(member, term, names);
        }
        return result || group;
    }

    private static bool Matches(CorporationMember member, CorporationMemberCriterion term, IReadOnlyList<string>? names)
    {
        if (term.Field == CorporationQueryField.Name)
        {
            string value = term.Text!;
            return (names ?? [member.Name]).Any(name =>
                term.Operator switch
                {
                    CorporationQueryOperator.Contains => name.Contains(value, StringComparison.OrdinalIgnoreCase),
                    CorporationQueryOperator.StartsWith => name.StartsWith(value, StringComparison.OrdinalIgnoreCase),
                    CorporationQueryOperator.EndsWith => name.EndsWith(value, StringComparison.OrdinalIgnoreCase),
                    CorporationQueryOperator.Is => name.Equals(value, StringComparison.OrdinalIgnoreCase),
                    CorporationQueryOperator.Like => Like(name, value),
                    _ => false,
                }
            );
        }
        var access = member.Access ?? new(new());
        var roles = access.Roles;
        long? number = term.Field switch
        {
            CorporationQueryField.Roles => roles.Roles,
            CorporationQueryField.RolesAtHQ => roles.RolesAtHQ,
            CorporationQueryField.RolesAtBase => roles.RolesAtBase,
            CorporationQueryField.RolesAtOther => roles.RolesAtOther,
            CorporationQueryField.GrantableRoles => roles.GrantableRoles,
            CorporationQueryField.GrantableRolesAtHQ => roles.GrantableRolesAtHQ,
            CorporationQueryField.GrantableRolesAtBase => roles.GrantableRolesAtBase,
            CorporationQueryField.GrantableRolesAtOther => roles.GrantableRolesAtOther,
            CorporationQueryField.Base => access.BaseId,
            CorporationQueryField.Joined => member.JoinedAt,
            CorporationQueryField.Titles => access.TitleMask,
            _ => null,
        };
        return term.Operator switch
        {
            CorporationQueryOperator.Equal => number == term.Number,
            CorporationQueryOperator.NotEqual => number != term.Number,
            CorporationQueryOperator.Greater => number > term.Number,
            CorporationQueryOperator.GreaterOrEqual => number >= term.Number,
            CorporationQueryOperator.Less => number < term.Number,
            CorporationQueryOperator.LessOrEqual => number <= term.Number,
            CorporationQueryOperator.HasBit => (number & term.Number) == term.Number,
            CorporationQueryOperator.NotHasBit => (number & term.Number) == 0,
            _ => false,
        };
    }

    private static bool Like(string name, string pattern)
    {
        // Bounded SQL-LIKE matching without SQL execution or a backtracking regular expression.
        bool[] previous = new bool[name.Length + 1];
        previous[0] = true;
        foreach (char token in pattern)
        {
            bool[] next = new bool[previous.Length];
            next[0] = token == '%' && previous[0];
            for (int index = 1; index < next.Length; index++)
            {
                next[index] =
                    token == '%'
                        ? previous[index] || next[index - 1]
                        : previous[index - 1] && (token == '_' || char.ToUpperInvariant(token) == char.ToUpperInvariant(name[index - 1]));
            }
            previous = next;
        }
        return previous[^1];
    }
}
