using Npgsql;
using Tao.Application;

namespace Tao.Infrastructure;

/// <summary>
/// Actor-scoped (FORCE RLS) daily counters of assistant model calls. The call budget is one atomic
/// upsert: concurrent requests cannot exceed it. Rows expire after 35 days (bounded retention purge).
/// </summary>
public sealed class PostgresAssistantUsageStore(NpgsqlDataSource db) : IAssistantUsageStore
{
    public async Task<AssistantUsage> GetAsync(string tenantId, string ownerSubject, DateOnly day,
        CancellationToken ct)
    {
        await using var actor = await PostgresActorScope.OpenAsync(db, tenantId, ownerSubject, ct);
        await using var command = new NpgsqlCommand("""
            SELECT calls, tokens FROM assistant_usage
            WHERE tenant_id = @tenant AND owner_sub = @owner AND day = @day
            """, actor.Connection, actor.Transaction);
        Bind(command, tenantId, ownerSubject, day);
        AssistantUsage result = new(0, 0);
        await using (var reader = await command.ExecuteReaderAsync(ct))
            if (await reader.ReadAsync(ct)) result = new AssistantUsage(reader.GetInt32(0), reader.GetInt64(1));
        await actor.CommitAsync(ct);
        return result;
    }

    public async Task<bool> TryReserveCallAsync(string tenantId, string ownerSubject, DateOnly day, int maxCalls,
        CancellationToken ct)
    {
        await using var actor = await PostgresActorScope.OpenAsync(db, tenantId, ownerSubject, ct);
        await PostgresActorLock.AcquireAsync(actor.Connection, actor.Transaction, tenantId, ownerSubject, ct);
        await using var command = new NpgsqlCommand("""
            INSERT INTO assistant_usage (tenant_id, owner_sub, day, calls)
            SELECT @tenant, @owner, @day, 1 WHERE @max >= 1
            ON CONFLICT (tenant_id, owner_sub, day) DO UPDATE SET calls = assistant_usage.calls + 1
            WHERE assistant_usage.calls < @max
            """, actor.Connection, actor.Transaction);
        Bind(command, tenantId, ownerSubject, day);
        command.Parameters.AddWithValue("max", maxCalls);
        var taken = await command.ExecuteNonQueryAsync(ct) == 1;
        await actor.CommitAsync(ct);
        return taken;
    }

    public async Task AddTokensAsync(string tenantId, string ownerSubject, DateOnly day, long tokens,
        CancellationToken ct)
    {
        await using var actor = await PostgresActorScope.OpenAsync(db, tenantId, ownerSubject, ct);
        await PostgresActorLock.AcquireAsync(actor.Connection, actor.Transaction, tenantId, ownerSubject, ct);
        await using var command = new NpgsqlCommand("""
            INSERT INTO assistant_usage (tenant_id, owner_sub, day, tokens)
            VALUES (@tenant, @owner, @day, @tokens)
            ON CONFLICT (tenant_id, owner_sub, day) DO UPDATE SET tokens = assistant_usage.tokens + EXCLUDED.tokens
            """, actor.Connection, actor.Transaction);
        Bind(command, tenantId, ownerSubject, day);
        command.Parameters.AddWithValue("tokens", Math.Max(0, tokens));
        await command.ExecuteNonQueryAsync(ct);
        await actor.CommitAsync(ct);
    }

    private static void Bind(NpgsqlCommand command, string tenantId, string ownerSubject, DateOnly day)
    {
        command.Parameters.AddWithValue("tenant", tenantId);
        command.Parameters.AddWithValue("owner", ownerSubject);
        command.Parameters.AddWithValue("day", day);
    }
}
