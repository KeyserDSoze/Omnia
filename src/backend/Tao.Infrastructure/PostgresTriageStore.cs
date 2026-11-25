using Npgsql;
using Tao.Application;
using Tao.Domain;

namespace Tao.Infrastructure;

/// <summary>
/// Actor-scoped (FORCE RLS) triage decisions and the audit trail of provider writes.
/// Only item keys and decisions are stored; provider content is always fetched live.
/// Decisions expire after 30 days, audit rows after 180 days (bounded retention purge).
/// </summary>
public sealed class PostgresTriageStore(NpgsqlDataSource db) : ITriageStore
{
    public const int MaxKeysPerRead = 500;

    public async Task<IReadOnlyDictionary<string, TriageRecord>> GetAsync(string tenantId, string ownerSubject,
        IReadOnlyCollection<string> keys, CancellationToken ct)
    {
        if (keys.Count == 0) return new Dictionary<string, TriageRecord>();
        if (keys.Count > MaxKeysPerRead) throw new ArgumentException("Too many triage keys.", nameof(keys));
        await using var actor = await PostgresActorScope.OpenAsync(db, tenantId, ownerSubject, ct);
        await using var command = new NpgsqlCommand("""
            SELECT item_key, decision, decided_at, snoozed_until, applied_to_source
            FROM triage_decisions
            WHERE tenant_id = @tenant AND owner_sub = @owner AND item_key = ANY(@keys) AND expires_at > now()
            """, actor.Connection, actor.Transaction);
        command.Parameters.AddWithValue("tenant", tenantId);
        command.Parameters.AddWithValue("owner", ownerSubject);
        command.Parameters.AddWithValue("keys", keys.ToArray());
        var records = new Dictionary<string, TriageRecord>(StringComparer.Ordinal);
        await using (var reader = await command.ExecuteReaderAsync(ct))
        {
            while (await reader.ReadAsync(ct))
            {
                if (!TriageDecisions.TryParse(reader.GetString(1), out var decision)) continue;
                records[reader.GetString(0)] = new TriageRecord(decision,
                    reader.GetFieldValue<DateTimeOffset>(2),
                    reader.IsDBNull(3) ? null : reader.GetFieldValue<DateTimeOffset>(3),
                    reader.GetBoolean(4));
            }
        }
        await actor.CommitAsync(ct);
        return records;
    }

    public async Task SaveAsync(string tenantId, string ownerSubject, string key, TriageRecord record,
        CancellationToken ct)
    {
        await using var actor = await PostgresActorScope.OpenAsync(db, tenantId, ownerSubject, ct);
        await PostgresActorLock.AcquireAsync(actor.Connection, actor.Transaction, tenantId, ownerSubject, ct);
        await using var command = new NpgsqlCommand("""
            INSERT INTO triage_decisions
                (tenant_id, owner_sub, item_key, decision, decided_at, snoozed_until, applied_to_source,
                 expires_at)
            VALUES (@tenant, @owner, @key, @decision, @decided, @snoozed, @applied, now() + interval '30 days')
            ON CONFLICT (tenant_id, owner_sub, item_key) DO UPDATE SET
                decision = EXCLUDED.decision, decided_at = EXCLUDED.decided_at,
                snoozed_until = EXCLUDED.snoozed_until,
                applied_to_source = triage_decisions.applied_to_source OR EXCLUDED.applied_to_source,
                expires_at = EXCLUDED.expires_at
            """, actor.Connection, actor.Transaction);
        command.Parameters.AddWithValue("tenant", tenantId);
        command.Parameters.AddWithValue("owner", ownerSubject);
        command.Parameters.AddWithValue("key", key);
        command.Parameters.AddWithValue("decision", TriageDecisions.ToWire(record.Decision));
        command.Parameters.AddWithValue("decided", record.DecidedAt);
        command.Parameters.AddWithValue("snoozed", (object?)record.SnoozedUntil ?? DBNull.Value);
        command.Parameters.AddWithValue("applied", record.AppliedToSource);
        await command.ExecuteNonQueryAsync(ct);
        await actor.CommitAsync(ct);
    }

    public async Task AuditAsync(string tenantId, string ownerSubject, Guid accountId, string action,
        string outcome, CancellationToken ct)
    {
        await using var actor = await PostgresActorScope.OpenAsync(db, tenantId, ownerSubject, ct);
        await PostgresActorLock.AcquireAsync(actor.Connection, actor.Transaction, tenantId, ownerSubject, ct);
        await using var command = new NpgsqlCommand("""
            INSERT INTO connector_actions (id, tenant_id, owner_sub, account_id, action, outcome)
            VALUES (@id, @tenant, @owner, @account, @action, @outcome)
            """, actor.Connection, actor.Transaction);
        command.Parameters.AddWithValue("id", Guid.NewGuid());
        command.Parameters.AddWithValue("tenant", tenantId);
        command.Parameters.AddWithValue("owner", ownerSubject);
        command.Parameters.AddWithValue("account", accountId);
        command.Parameters.AddWithValue("action", action);
        command.Parameters.AddWithValue("outcome", outcome);
        await command.ExecuteNonQueryAsync(ct);
        await actor.CommitAsync(ct);
    }

    public async Task<IReadOnlyList<TriageRecord>> RecentAsync(string tenantId, string ownerSubject,
        DateTimeOffset since, CancellationToken ct)
    {
        await using var actor = await PostgresActorScope.OpenAsync(db, tenantId, ownerSubject, ct);
        await using var command = new NpgsqlCommand("""
            SELECT decision, decided_at, snoozed_until, applied_to_source FROM triage_decisions
            WHERE tenant_id = @tenant AND owner_sub = @owner AND decided_at >= @since AND expires_at > now()
            ORDER BY decided_at DESC
            LIMIT 2000
            """, actor.Connection, actor.Transaction);
        command.Parameters.AddWithValue("tenant", tenantId);
        command.Parameters.AddWithValue("owner", ownerSubject);
        command.Parameters.AddWithValue("since", since.ToUniversalTime());
        var records = new List<TriageRecord>();
        await using (var reader = await command.ExecuteReaderAsync(ct))
        {
            while (await reader.ReadAsync(ct))
            {
                if (!TriageDecisions.TryParse(reader.GetString(0), out var decision)) continue;
                records.Add(new TriageRecord(decision, reader.GetFieldValue<DateTimeOffset>(1),
                    reader.IsDBNull(2) ? null : reader.GetFieldValue<DateTimeOffset>(2), reader.GetBoolean(3)));
            }
        }
        await actor.CommitAsync(ct);
        return records;
    }
}
