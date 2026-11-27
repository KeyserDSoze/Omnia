using System.Security.Cryptography;
using System.Text;
using Npgsql;
using Tao.Application;
using Tao.Domain;

namespace Tao.Infrastructure;

/// <summary>Reminders of one actor (FORCE RLS); the text is sealed with an AAD bound to tenant, owner and row.</summary>
public sealed class PostgresReminderStore(NpgsqlDataSource db, IEnvelopeEncryption crypto) : IReminderStore
{
    private const string Columns =
        "id, sealed_text, repeat, at_time, on_date, time_zone, enabled, next_run_at, last_sent_at, created_at";

    public async Task<IReadOnlyList<Reminder>> ListAsync(string tenantId, string ownerSubject, CancellationToken ct)
    {
        await using var actor = await PostgresActorScope.OpenAsync(db, tenantId, ownerSubject, ct);
        var rows = new List<Reminder>();
        await using (var command = Command(actor, tenantId, ownerSubject,
            $"SELECT {Columns} FROM reminders WHERE tenant_id = @tenant AND owner_sub = @owner ORDER BY created_at, id"))
        await using (var reader = await command.ExecuteReaderAsync(ct))
            while (await reader.ReadAsync(ct)) rows.Add(Read(reader, tenantId, ownerSubject));
        await actor.CommitAsync(ct);
        return rows;
    }

    public async Task<Reminder?> GetAsync(string tenantId, string ownerSubject, Guid id, CancellationToken ct)
    {
        await using var actor = await PostgresActorScope.OpenAsync(db, tenantId, ownerSubject, ct);
        Reminder? found = null;
        await using (var command = Command(actor, tenantId, ownerSubject,
            $"SELECT {Columns} FROM reminders WHERE tenant_id = @tenant AND owner_sub = @owner AND id = @id"))
        {
            command.Parameters.AddWithValue("id", id);
            await using var reader = await command.ExecuteReaderAsync(ct);
            if (await reader.ReadAsync(ct)) found = Read(reader, tenantId, ownerSubject);
        }
        await actor.CommitAsync(ct);
        return found;
    }

    public async Task<Reminder> AddAsync(string tenantId, string ownerSubject, ReminderDraft draft, DateTimeOffset? next,
        CancellationToken ct)
    {
        var id = Guid.NewGuid();
        var plain = Encoding.UTF8.GetBytes(draft.Text);
        byte[] envelope;
        try { envelope = crypto.Seal(plain, Scope(tenantId, ownerSubject, id)); }
        finally { CryptographicOperations.ZeroMemory(plain); }
        await using var actor = await PostgresActorScope.OpenAsync(db, tenantId, ownerSubject, ct);
        await PostgresActorLock.AcquireAsync(actor.Connection, actor.Transaction, tenantId, ownerSubject, ct);
        await using (var count = Command(actor, tenantId, ownerSubject,
            "SELECT count(*) FROM reminders WHERE tenant_id = @tenant AND owner_sub = @owner"))
            if ((long)(await count.ExecuteScalarAsync(ct) ?? 0L) >= ReminderRules.MaxPerActor)
                throw new TooManyRemindersException();
        DateTimeOffset createdAt;
        await using (var insert = Command(actor, tenantId, ownerSubject, """
            INSERT INTO reminders (id, tenant_id, owner_sub, sealed_text, repeat, at_time, on_date, time_zone, enabled, next_run_at)
            VALUES (@id, @tenant, @owner, @sealed, @repeat, @at, @date, @zone, true, @next)
            RETURNING created_at
            """))
        {
            insert.Parameters.AddWithValue("id", id);
            insert.Parameters.AddWithValue("sealed", envelope);
            insert.Parameters.AddWithValue("repeat", Repeat(draft.Repeat));
            insert.Parameters.AddWithValue("at", draft.At);
            insert.Parameters.AddWithValue("date", (object?)draft.Date ?? DBNull.Value);
            insert.Parameters.AddWithValue("zone", draft.TimeZone);
            insert.Parameters.AddWithValue("next", (object?)next ?? DBNull.Value);
            createdAt = new DateTimeOffset((DateTime)(await insert.ExecuteScalarAsync(ct))!);
        }
        await actor.CommitAsync(ct);
        return new Reminder(id, draft.Text, draft.Repeat, draft.TimeZone, draft.At, draft.Date, true, next, null, createdAt);
    }

    public async Task<bool> DeleteAsync(string tenantId, string ownerSubject, Guid id, CancellationToken ct)
    {
        await using var actor = await PostgresActorScope.OpenAsync(db, tenantId, ownerSubject, ct);
        int rows;
        await using (var command = Command(actor, tenantId, ownerSubject,
            "DELETE FROM reminders WHERE tenant_id = @tenant AND owner_sub = @owner AND id = @id"))
        {
            command.Parameters.AddWithValue("id", id);
            rows = await command.ExecuteNonQueryAsync(ct);
        }
        await actor.CommitAsync(ct);
        return rows > 0;
    }

    public async Task<bool> SetEnabledAsync(string tenantId, string ownerSubject, Guid id, bool enabled, DateTimeOffset? next,
        CancellationToken ct)
    {
        await using var actor = await PostgresActorScope.OpenAsync(db, tenantId, ownerSubject, ct);
        int rows;
        await using (var command = Command(actor, tenantId, ownerSubject, """
            UPDATE reminders SET enabled = @enabled, next_run_at = @next, claimed_until = NULL
            WHERE tenant_id = @tenant AND owner_sub = @owner AND id = @id
            """))
        {
            command.Parameters.AddWithValue("id", id);
            command.Parameters.AddWithValue("enabled", enabled);
            command.Parameters.AddWithValue("next", (object?)next ?? DBNull.Value);
            rows = await command.ExecuteNonQueryAsync(ct);
        }
        await actor.CommitAsync(ct);
        return rows > 0;
    }

    public async Task CompleteAsync(string tenantId, string ownerSubject, Guid id, DateTimeOffset? sentAt, DateTimeOffset? next,
        CancellationToken ct)
    {
        await using var actor = await PostgresActorScope.OpenAsync(db, tenantId, ownerSubject, ct);
        await using (var command = Command(actor, tenantId, ownerSubject, """
            UPDATE reminders SET last_sent_at = COALESCE(@sent, last_sent_at), next_run_at = @next, claimed_until = NULL
            WHERE tenant_id = @tenant AND owner_sub = @owner AND id = @id
            """))
        {
            command.Parameters.AddWithValue("id", id);
            command.Parameters.AddWithValue("sent", (object?)sentAt ?? DBNull.Value);
            command.Parameters.AddWithValue("next", (object?)next ?? DBNull.Value);
            await command.ExecuteNonQueryAsync(ct);
        }
        await actor.CommitAsync(ct);
    }

    private Reminder Read(NpgsqlDataReader reader, string tenantId, string ownerSubject)
    {
        var id = reader.GetGuid(0);
        var plain = crypto.Open(reader.GetFieldValue<byte[]>(1), Scope(tenantId, ownerSubject, id));
        try
        {
            return new Reminder(id, Encoding.UTF8.GetString(plain), ParseRepeat(reader.GetString(2)),
                reader.GetString(5), reader.GetFieldValue<TimeOnly>(3),
                reader.IsDBNull(4) ? null : reader.GetFieldValue<DateOnly>(4), reader.GetBoolean(6),
                reader.IsDBNull(7) ? null : reader.GetFieldValue<DateTimeOffset>(7),
                reader.IsDBNull(8) ? null : reader.GetFieldValue<DateTimeOffset>(8), reader.GetFieldValue<DateTimeOffset>(9));
        }
        finally { CryptographicOperations.ZeroMemory(plain); }
    }

    private static NpgsqlCommand Command(PostgresActorScope actor, string tenantId, string ownerSubject, string sql)
    {
        var command = new NpgsqlCommand(sql, actor.Connection, actor.Transaction);
        command.Parameters.AddWithValue("tenant", tenantId);
        command.Parameters.AddWithValue("owner", ownerSubject);
        return command;
    }

    internal static EncryptionScope Scope(string tenantId, string ownerSubject, Guid id) =>
        new(tenantId, ownerSubject, "reminder.text", id.ToString("D"));

    private static string Repeat(ReminderRepeat repeat) => repeat.ToString().ToLowerInvariant();

    private static ReminderRepeat ParseRepeat(string value) =>
        Enum.Parse<ReminderRepeat>(value, ignoreCase: true);
}

/// <summary>
/// The cross-actor view of due reminders for the dispatcher: one fixed SECURITY DEFINER function, reachable only
/// by the restricted worker role, that returns identifiers and nothing else.
/// </summary>
public sealed class PostgresReminderClaims(NpgsqlDataSource workerDb) : IReminderClaims
{
    public async Task<IReadOnlyList<ClaimedReminder>> ClaimDueAsync(int batchSize, TimeSpan lease, CancellationToken ct)
    {
        if (batchSize is < 1 or > 200) throw new ArgumentOutOfRangeException(nameof(batchSize));
        await using var command = workerDb.CreateCommand("SELECT o_tenant, o_owner, o_id FROM public.tao_claim_due_reminders(@batch, @lease)");
        command.Parameters.AddWithValue("batch", batchSize);
        command.Parameters.AddWithValue("lease", (int)lease.TotalSeconds);
        var claimed = new List<ClaimedReminder>();
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            claimed.Add(new ClaimedReminder(reader.GetString(0), reader.GetString(1), reader.GetGuid(2)));
        return claimed;
    }

    /// <summary>Finished one-off reminders and push subscriptions that no longer work.</summary>
    public async Task<int> PurgeStaleAsync(int batchSize, CancellationToken ct)
    {
        if (batchSize is < 1 or > 500) throw new ArgumentOutOfRangeException(nameof(batchSize));
        await using var command = workerDb.CreateCommand("SELECT public.tao_purge_stale_notifications(@batch)");
        command.Parameters.AddWithValue("batch", batchSize);
        return (int)(await command.ExecuteScalarAsync(ct) ?? 0);
    }
}
