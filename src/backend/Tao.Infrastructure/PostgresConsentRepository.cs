using Npgsql;
using Tao.Application;
using Tao.Domain;

namespace Tao.Infrastructure;

/// <summary>
/// A first-party consent ledger, tenant-scoped and fail-closed. This is not an
/// integration-provider OAuth grant store or a GDPR consent/legal assessment.
/// </summary>
public sealed class PostgresConsentRepository(NpgsqlDataSource db)
    : IConsentRepository, IConsentGuard, IConsentPageRepository
{
    public async Task<ConsentGrant> GrantAsync(ConsentGrant grant, CancellationToken ct)
    {
        await using var scope = await PostgresActorScope.OpenAsync(db,
            grant.TenantId, grant.OwnerSubject, ct);
        var conn = scope.Connection;
        var tx = scope.Transaction;
        await PostgresActorLock.AcquireAsync(conn, tx,
            grant.TenantId, grant.OwnerSubject, ct);
        var scopeKey = $"{grant.TenantId}|{grant.OwnerSubject}|{grant.Source}|{grant.Purpose}";

        // Scope-specific lock serializes concurrent re-grants. Index still enforces uniqueness.
        await using (var gate = new NpgsqlCommand(
            "SELECT pg_advisory_xact_lock(hashtext(@scope)::bigint)", conn, tx))
        {
            gate.Parameters.AddWithValue("scope", scopeKey);
            await gate.ExecuteNonQueryAsync(ct);
        }

        await using (var old = new NpgsqlCommand("""
            UPDATE consent_grants SET revoked_at = @now
            WHERE tenant_id = @tenant AND owner_sub = @owner
                AND source = @source AND purpose = @purpose AND revoked_at IS NULL
            """, conn, tx))
        {
            old.Parameters.AddWithValue("now", grant.GrantedAt);
            old.Parameters.AddWithValue("tenant", grant.TenantId);
            old.Parameters.AddWithValue("owner", grant.OwnerSubject);
            old.Parameters.AddWithValue("source", grant.Source);
            old.Parameters.AddWithValue("purpose", grant.Purpose);
            await old.ExecuteNonQueryAsync(ct);
        }

        await using (var insert = new NpgsqlCommand("""
            INSERT INTO consent_grants(id, tenant_id, owner_sub, source, purpose,
                policy_version, granted_at, expires_at)
            VALUES (@id, @tenant, @owner, @source, @purpose, @version, @granted, @expires)
            """, conn, tx))
        {
            insert.Parameters.AddWithValue("id", grant.Id);
            insert.Parameters.AddWithValue("tenant", grant.TenantId);
            insert.Parameters.AddWithValue("owner", grant.OwnerSubject);
            insert.Parameters.AddWithValue("source", grant.Source);
            insert.Parameters.AddWithValue("purpose", grant.Purpose);
            insert.Parameters.AddWithValue("version", grant.PolicyVersion);
            insert.Parameters.AddWithValue("granted", grant.GrantedAt);
            insert.Parameters.AddWithValue("expires", grant.ExpiresAt);
            await insert.ExecuteNonQueryAsync(ct);
        }

        await scope.CommitAsync(ct);
        return grant;
    }

    public async Task<bool> RevokeAsync(string tenantId, string ownerSubject,
        Guid id, DateTimeOffset now, CancellationToken ct)
    {
        await using var scope = await PostgresActorScope.OpenAsync(db, tenantId, ownerSubject, ct);
        await PostgresActorLock.AcquireAsync(scope.Connection, scope.Transaction,
            tenantId, ownerSubject, ct);
        // An older device may still reference a superseded grant ID. Never
        // treat revocation of that historical handle as successful while a
        // newer grant with the SAME source+purpose is still effective.
        // Fetch scope only from a persisted, actor-owned row under FORCE RLS,
        // never from request input (the HTTP request selects only the UUID).
        (string Source, string Purpose) targetScope;
        await using (var target = new NpgsqlCommand("""
            SELECT source, purpose
            FROM consent_grants
            WHERE id = @id AND tenant_id = @tenant AND owner_sub = @owner
            FOR UPDATE
            """, scope.Connection, scope.Transaction))
        {
            target.Parameters.AddWithValue("id", id);
            target.Parameters.AddWithValue("tenant", tenantId);
            target.Parameters.AddWithValue("owner", ownerSubject);
            await using var reader = await target.ExecuteReaderAsync(ct);
            if (!await reader.ReadAsync(ct))
                return false; // Unknown or foreign grant: never enumerate it.
            targetScope = (reader.GetString(0), reader.GetString(1));
        }

        // Actor advisory lock serializes all first-party grant/regrant/revoke,
        // capture and erase operations. Withdrawal takes effect on EVERY
        // currently unrevoked record in this exact actor+source+purpose, even
        // when the supplied ID has already been superseded and revoked.
        // Keep the historical revoked_at timestamp unchanged for audit.
        await using (var revoke = new NpgsqlCommand("""
            UPDATE consent_grants SET revoked_at = @now
            WHERE tenant_id = @tenant AND owner_sub = @owner
              AND source = @source AND purpose = @purpose
              AND revoked_at IS NULL
            """, scope.Connection, scope.Transaction))
        {
            revoke.Parameters.AddWithValue("now", now);
            revoke.Parameters.AddWithValue("tenant", tenantId);
            revoke.Parameters.AddWithValue("owner", ownerSubject);
            revoke.Parameters.AddWithValue("source", targetScope.Source);
            revoke.Parameters.AddWithValue("purpose", targetScope.Purpose);
            await revoke.ExecuteNonQueryAsync(ct);
        }

        await scope.CommitAsync(ct);
        // Known, actor-owned ID is idempotently revocable even if all
        // matching grants were already revoked in an earlier request.
        return true;
    }

    public async Task<IReadOnlyList<ConsentGrant>> ListAsync(string tenantId,
        string ownerSubject, CancellationToken ct)
    {
        await using var scope = await PostgresActorScope.OpenAsync(db, tenantId, ownerSubject, ct);
        await using var command = new NpgsqlCommand("""
            SELECT id, tenant_id, owner_sub, source, purpose,
                policy_version, granted_at, expires_at, revoked_at
            FROM consent_grants
            WHERE tenant_id = @tenant AND owner_sub = @owner
            -- The bounded public ledger is also used to show/revoke the
            -- manual capture grant. Historical renewals must NEVER hide
            -- a still-effective manual grant behind 200 newer old records.
            -- PostgreSQL now() is stable for this actor-scoped transaction.
            ORDER BY
                (source = 'manual' AND purpose = 'memory.capture'
                 AND revoked_at IS NULL AND granted_at <= now() + interval '2 minutes'
                 AND expires_at > now()) DESC,
                (revoked_at IS NULL AND granted_at <= now() + interval '2 minutes'
                 AND expires_at > now()) DESC,
                granted_at DESC, id DESC LIMIT 200
            """, scope.Connection, scope.Transaction);
        command.Parameters.AddWithValue("tenant", tenantId);
        command.Parameters.AddWithValue("owner", ownerSubject);
        var grants = new List<ConsentGrant>();
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            grants.Add(ConsentGrant.Restore(reader.GetGuid(0), reader.GetString(1),
                reader.GetString(2), reader.GetString(3), reader.GetString(4),
                reader.GetString(5), reader.GetFieldValue<DateTimeOffset>(6),
                reader.GetFieldValue<DateTimeOffset>(7),
                reader.IsDBNull(8) ? null : reader.GetFieldValue<DateTimeOffset>(8)));
        return grants;
    }

    public async Task<ConsentPage> PageAsync(string tenantId, string ownerSubject,
        int limit, ConsentPageCursor? before, CancellationToken ct)
    {
        // Even callers bypassing application validation cannot request a huge
        // page. Keep all reads in the restricted actor-scoped FORCE RLS role.
        if (limit is < 1 or > 50)
            throw new ArgumentOutOfRangeException(nameof(limit));

        await using var scope = await PostgresActorScope.OpenAsync(
            db, tenantId, ownerSubject, ct);
        // Row ordering is independent of mutable active/revoked state, so a
        // normal revoke does not reorder remaining pages. A keyset is only a
        // position; it is NOT a cross-request snapshot under concurrency.
        var sql = before is null ? """
            SELECT id, tenant_id, owner_sub, source, purpose,
                policy_version, granted_at, expires_at, revoked_at
            FROM consent_grants
            WHERE tenant_id = @tenant AND owner_sub = @owner
            ORDER BY granted_at DESC, id DESC LIMIT @limit
            """ : """
            SELECT id, tenant_id, owner_sub, source, purpose,
                policy_version, granted_at, expires_at, revoked_at
            FROM consent_grants
            WHERE tenant_id = @tenant AND owner_sub = @owner
                AND (granted_at, id) < (@beforeAt, @beforeId)
            ORDER BY granted_at DESC, id DESC LIMIT @limit
            """;
        await using var command = new NpgsqlCommand(
            sql, scope.Connection, scope.Transaction);
        command.Parameters.AddWithValue("tenant", tenantId);
        command.Parameters.AddWithValue("owner", ownerSubject);
        command.Parameters.AddWithValue("limit", limit + 1);
        if (before is not null)
        {
            command.Parameters.AddWithValue("beforeAt", before.GrantedAt);
            command.Parameters.AddWithValue("beforeId", before.Id);
        }

        var grants = new List<ConsentGrant>(limit + 1);
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            grants.Add(ConsentGrant.Restore(reader.GetGuid(0), reader.GetString(1),
                reader.GetString(2), reader.GetString(3), reader.GetString(4),
                reader.GetString(5), reader.GetFieldValue<DateTimeOffset>(6),
                reader.GetFieldValue<DateTimeOffset>(7),
                reader.IsDBNull(8) ? null : reader.GetFieldValue<DateTimeOffset>(8)));

        var hasMore = grants.Count > limit;
        if (hasMore) grants.RemoveAt(grants.Count - 1);
        var last = grants.Count == 0 ? null : grants[^1];
        return new ConsentPage(grants, hasMore && last is not null
            ? new ConsentPageCursor(last.GrantedAt, last.Id).Encode() : null);
    }

    public async Task EnsureActiveAsync(string tenantId, string ownerSubject,
        string source, string purpose, CancellationToken ct)
    {
        await using var scope = await PostgresActorScope.OpenAsync(db, tenantId, ownerSubject, ct);
        await using var command = new NpgsqlCommand("""
            SELECT 1 FROM consent_grants
            WHERE tenant_id = @tenant AND owner_sub = @owner AND source = @source
                AND purpose = @purpose AND revoked_at IS NULL
                -- granted_at comes from the API clock: tolerate small API/DB skew.
                AND granted_at <= now() + interval '2 minutes' AND expires_at > now()
            LIMIT 1
            """, scope.Connection, scope.Transaction);
        command.Parameters.AddWithValue("tenant", tenantId);
        command.Parameters.AddWithValue("owner", ownerSubject);
        command.Parameters.AddWithValue("source", source);
        command.Parameters.AddWithValue("purpose", purpose);
        if (await command.ExecuteScalarAsync(ct) is null)
            throw new ConsentRequiredException();
    }
}
