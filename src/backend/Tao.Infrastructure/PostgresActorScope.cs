using Npgsql;

namespace Tao.Infrastructure;

/// <summary>
/// Binds a trusted JWT tenant and subject to a single PostgreSQL transaction.
/// Scope is always local to that transaction and cannot leak to a pooled connection.
/// The caller must never populate actor values from unverified client input.
/// </summary>
public sealed class PostgresActorScope : IAsyncDisposable
{
    private readonly NpgsqlConnection _connection;
    private readonly NpgsqlTransaction _transaction;

    private PostgresActorScope(NpgsqlConnection connection, NpgsqlTransaction transaction)
    {
        _connection = connection;
        _transaction = transaction;
    }

    public NpgsqlConnection Connection => _connection;
    public NpgsqlTransaction Transaction => _transaction;

    public static async Task<PostgresActorScope> OpenAsync(NpgsqlDataSource db,
        string tenantId, string ownerSubject, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(tenantId) || tenantId.Length > 128 ||
            string.IsNullOrWhiteSpace(ownerSubject) || ownerSubject.Length > 255)
            throw new ArgumentException("A verified tenant and subject are required.");

        var connection = await db.OpenConnectionAsync(ct);
        try
        {
            var transaction = await connection.BeginTransactionAsync(ct);
            try
            {
                await using var command = new NpgsqlCommand("""
                    SELECT set_config('tao.tenant_id', @tenant, true),
                           set_config('tao.owner_sub', @owner, true)
                    """, connection, transaction);
                command.Parameters.AddWithValue("tenant", tenantId);
                command.Parameters.AddWithValue("owner", ownerSubject);
                await command.ExecuteNonQueryAsync(ct);
                return new PostgresActorScope(connection, transaction);
            }
            catch
            {
                await transaction.DisposeAsync();
                throw;
            }
        }
        catch
        {
            await connection.DisposeAsync();
            throw;
        }
    }

    public Task CommitAsync(CancellationToken ct) => _transaction.CommitAsync(ct);

    public async ValueTask DisposeAsync()
    {
        await _transaction.DisposeAsync();
        await _connection.DisposeAsync();
    }
}
