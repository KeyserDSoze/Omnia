using Npgsql;
using NpgsqlTypes;

namespace Omnia.Infrastructure;

/// <summary>
/// A recorded erasure REQUEST (not a committed deletion). Its independent
/// witness receipt may survive primary PITR. It MUST NOT authorize deletion,
/// imply source commit success, or issue recovery-ready state.
/// </summary>
public sealed record PreEraseIntentReceipt(
    long Sequence, Guid RequestId, string TenantId,
    string OwnerSubject, string IntentType);

/// <summary>
/// CI-only restricted preflight of an already authorized SYNTHETIC actor.
/// A dedicated SQL role cannot append completed-erasure receipts.
/// </summary>
public sealed class PostgresPreEraseIntentWriter(NpgsqlDataSource witness)
{
    public Task<long> RecordAsync(
        Guid requestId, string tenant, string owner, CancellationToken ct)
    {
        Validate(requestId, tenant, owner);
        return RecordCoreAsync(requestId, tenant, owner, ct);
    }

    private async Task<long> RecordCoreAsync(Guid requestId,
        string tenant, string owner, CancellationToken ct)
    {
        await VerifyWriterAsync(witness, ct);
        await using var cmd = witness.CreateCommand("""
            SELECT witness.append_erase_intent(@id, @tenant, @owner)
            """);
        cmd.Parameters.AddWithValue("id", requestId);
        cmd.Parameters.Add("tenant", NpgsqlDbType.Varchar).Value = tenant;
        cmd.Parameters.Add("owner", NpgsqlDbType.Varchar).Value = owner;
        return (long)(await cmd.ExecuteScalarAsync(ct)
            ?? throw new InvalidDataException("ERASURE_INTENT_NOT_RECORDED."));
    }

    public static Task VerifyWriterAsync(
        NpgsqlDataSource db, CancellationToken ct = default) =>
        PreEraseIntentRoleGuard.VerifyAsync(db, wantWriter: true, ct);

    internal static void Validate(Guid id, string tenant, string owner)
    {
        if (id == Guid.Empty ||
            !ValidScope(tenant, 128) || !ValidScope(owner, 255))
            throw new ArgumentException("Invalid pre-erase intent scope.");
    }

    private static bool ValidScope(string? value, int bound) =>
        !string.IsNullOrWhiteSpace(value) && value.Length <= bound &&
        value.All(c => !char.IsControl(c));
}

/// <summary>
/// Read-only independent intent inspector. No implicit replay.
/// </summary>
public sealed class PostgresPreEraseIntentReader(NpgsqlDataSource witness)
{
    public async Task<PreEraseIntentReceipt?> FindAsync(
        Guid id, CancellationToken ct)
    {
        if (id == Guid.Empty)
            throw new ArgumentException("Intent identifier required.", nameof(id));
        await VerifyReaderAsync(witness, ct);
        await using var cmd = witness.CreateCommand("""
            SELECT sequence, request_id, tenant_id, owner_sub, intent_type
              FROM witness.find_erase_intent(@id)
            """);
        cmd.Parameters.AddWithValue("id", id);
        await using var rows = await cmd.ExecuteReaderAsync(ct);
        if (!await rows.ReadAsync(ct)) return null;
        var item = FromRow(rows);
        if (await rows.ReadAsync(ct))
            throw new InvalidDataException("ERASURE_INTENT_DUPLICATE.");
        return item;
    }

    public Task<IReadOnlyList<PreEraseIntentReceipt>> ReadBatchAsync(
        long afterSequence, int limit, CancellationToken ct)
    {
        if (afterSequence < 0)
            throw new ArgumentOutOfRangeException(nameof(afterSequence));
        if (limit is < 1 or > 100)
            throw new ArgumentOutOfRangeException(nameof(limit));
        return ReadBatchCoreAsync(afterSequence, limit, ct);
    }

    private async Task<IReadOnlyList<PreEraseIntentReceipt>> ReadBatchCoreAsync(
        long afterSequence, int limit, CancellationToken ct)
    {
        await VerifyReaderAsync(witness, ct);
        await using var cmd = witness.CreateCommand("""
            SELECT sequence, request_id, tenant_id, owner_sub, intent_type
              FROM witness.list_erase_intents(@after, @limit)
            """);
        cmd.Parameters.AddWithValue("after", afterSequence);
        cmd.Parameters.AddWithValue("limit", limit);
        var result = new List<PreEraseIntentReceipt>();
        await using var rows = await cmd.ExecuteReaderAsync(ct);
        while (await rows.ReadAsync(ct))
        {
            if (result.Count >= limit)
                throw new InvalidDataException("ERASURE_INTENT_BOUND_EXCEEDED.");
            var item = FromRow(rows);
            if (item.Sequence <= afterSequence ||
                (result.Count > 0 && item.Sequence <= result[^1].Sequence))
                throw new InvalidDataException("ERASURE_INTENT_ORDER_INVALID.");
            result.Add(item);
        }
        return result;
    }

    private static PreEraseIntentReceipt FromRow(NpgsqlDataReader rows)
    {
        var result = new PreEraseIntentReceipt(
            rows.GetInt64(0), rows.GetGuid(1), rows.GetString(2),
            rows.GetString(3), rows.GetString(4));
        if (result.Sequence <= 0 || result.IntentType != "erase-actor-intent" ||
            result.RequestId == Guid.Empty)
            throw new InvalidDataException("ERASURE_INTENT_RECEIPT_INVALID.");
        return result;
    }

    public static Task VerifyReaderAsync(
        NpgsqlDataSource db, CancellationToken ct = default) =>
        PreEraseIntentRoleGuard.VerifyAsync(db, wantWriter: false, ct);
}

internal static class PreEraseIntentRoleGuard
{
    internal static async Task VerifyAsync(
        NpgsqlDataSource db, bool wantWriter, CancellationToken ct)
    {
        await using var cmd = db.CreateCommand("""
            SELECT NOT (
                r.rolsuper OR r.rolbypassrls
                OR pg_has_role(r.oid,
                    (SELECT relowner FROM pg_class
                     WHERE oid='witness.erasure_intents'::regclass), 'MEMBER')
                OR has_table_privilege(r.oid, 'witness.erasure_intents',
                    'SELECT, INSERT, UPDATE, DELETE, TRUNCATE, REFERENCES, TRIGGER')
                OR has_table_privilege(r.oid, 'witness.intent_head',
                    'SELECT, INSERT, UPDATE, DELETE, TRUNCATE, REFERENCES, TRIGGER')
                OR has_table_privilege(r.oid, 'witness.erasure_events',
                    'SELECT, INSERT, UPDATE, DELETE, TRUNCATE, REFERENCES, TRIGGER')
                OR has_table_privilege(r.oid, 'witness.journal_head',
                    'SELECT, INSERT, UPDATE, DELETE, TRUNCATE, REFERENCES, TRIGGER')
                OR has_function_privilege(r.oid,
                    'witness.append_erasure(uuid,varchar,varchar,varchar)'::regprocedure,
                    'EXECUTE')
                OR has_function_privilege(r.oid,
                    'witness.find_erasure(uuid)'::regprocedure, 'EXECUTE')
                OR has_function_privilege(r.oid,
                    'witness.list_erasures(bigint,integer)'::regprocedure,
                    'EXECUTE')
            )
            AND has_function_privilege(r.oid,
                'witness.append_erase_intent(uuid,varchar,varchar)'::regprocedure,
                'EXECUTE') = @writer
            AND has_function_privilege(r.oid,
                'witness.find_erase_intent(uuid)'::regprocedure,
                'EXECUTE') <> @writer
            AND has_function_privilege(r.oid,
                'witness.list_erase_intents(bigint,integer)'::regprocedure,
                'EXECUTE') <> @writer
              FROM pg_roles r WHERE r.rolname=current_user
            """);
        cmd.Parameters.AddWithValue("writer", wantWriter);
        if (await cmd.ExecuteScalarAsync(ct) is not true)
            throw new InvalidOperationException("ERASURE_INTENT_ROLE_DENIED.");
    }
}
