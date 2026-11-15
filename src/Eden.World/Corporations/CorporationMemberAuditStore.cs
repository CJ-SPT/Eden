using System.Text.Json;
using Eden.Data.World.Corporations;
using Eden.Gameplay.Corporations;
using Eden.Infrastructure.Registration;
using Eden.World.Runtime;
using Eden.World.Runtime.Economy;
using Eden.World.Storage;

namespace Eden.World.Corporations;

[Injectable]
internal sealed class CorporationMemberAuditStore(WorldDatabase database) : ICorporationMemberAudit
{
    public CorporationMemberAuditResult Read(long actorId, CorporationMemberAuditQuery query)
    {
        CorporationMemberAuditRules.Validate(query);
        var resident =
            LiveWorld.Find(database.Path) ?? throw new InvalidDataException("Member auditing requires an active resident world.");
        return resident.Economy.Inspect(world =>
        {
            var corporation = CorporationRoleStore.Current(world, actorId);
            if (!CorporationRoleStore.Has(world, actorId, corporation, CorporationRole.Auditor))
            {
                throw new InvalidDataException("Corporation auditing authority is required.");
            }
            var employment = world
                .Set<SavedCorporationEmployment>()
                .Where(row => row.PilotId == query.MemberId)
                .OrderBy(row => row.Id)
                .ToArray();
            List<CorporationMemberAuditEvent> events = [];
            for (int index = 0; index < employment.Length; index++)
            {
                var job = employment[index];
                if (job.CorporationId != corporation.Id)
                {
                    continue;
                }
                var type =
                    corporation.CreatorId == query.MemberId && job.StartedAt == corporation.CreatedAt
                        ? CorporationAuditEventType.Created
                        : CorporationAuditEventType.Joined;
                events.Add(new(job.Id, job.StartedAt, type, query.MemberId, corporation.Id));
                if (index + 1 < employment.Length)
                {
                    var next = employment[index + 1];
                    events.Add(new(next.Id, next.StartedAt, CorporationAuditEventType.Left, query.MemberId, corporation.Id));
                }
            }
            // Each member receives their own changes and changes they issue once. The resident recipient index avoids
            // scanning other pilots' notification histories, and the corporation check keeps past employers private.
            var roles = world
                .Set<SavedCorporationChange>()
                .Where(row => row.PilotId == query.MemberId && row.RoleJson != null)
                .OrderByDescending(row => row.Id)
                .AsEnumerable()
                .Select(row => JsonSerializer.Deserialize<CorporationRoleChange>(row.RoleJson!)!)
                .Where(change =>
                    change.CorporationId == corporation.Id && (change.PilotId == query.MemberId || change.IssuerId == query.MemberId)
                )
                .SelectMany(CorporationMemberAuditRules.RoleEntries)
                .Where(entry => InRange(entry.Time, query))
                .OrderByDescending(entry => entry.Time)
                .Take(query.Limit)
                .ToArray();
            return new CorporationMemberAuditResult(
                events.Where(entry => InRange(entry.Time, query)).OrderByDescending(entry => entry.Time).Take(query.Limit).ToArray(),
                roles
            );
        });
    }

    private static bool InRange(long time, CorporationMemberAuditQuery query) => time >= query.From && time < query.To;
}
