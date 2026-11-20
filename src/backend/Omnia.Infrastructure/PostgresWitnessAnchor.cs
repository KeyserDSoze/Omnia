using Npgsql;
using NpgsqlTypes;

namespace Omnia.Infrastructure;

/// <summary>
/// Only a checkpoint digest/count/MAC are stored in the THIRD disposable
/// PostgreSQL cluster. No actor identifiers or plaintext note content.
/// Equality is structural to compare fresh SQL round trips safely.
/// </summary>
public sealed class WitnessAnchorState(int count, byte[] checkpoint)
    : IEquatable<WitnessAnchorState>
{
    public int Count { get; } = count;
    public byte[] Checkpoint { get; } = checkpoint;

    public bool Equals(WitnessAnchorState? other) =>
        other is not null && Count == other.Count &&
        Checkpoint.AsSpan().SequenceEqual(other.Checkpoint);
    public override bool Equals(object? obj) =>
        obj is WitnessAnchorState other && Equals(other);
    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(Count);
        foreach (var item in Checkpoint) hash.Add(item);
        return hash.ToHashCode();
    }
}

/// <summary>
/// Dedicated restricted writer can ONLY call a fixed monotonic SQL
/// advance operation, never read or mutate raw tables directly.
/// This is NOT a production checkpoint authority or vault.
/// </summary>
public sealed class PostgresWitnessAnchor(NpgsqlDataSource anchorWriter)
{
    public async Task<int> AdvanceAsync(int count, byte[] checkpoint,
        CancellationToken ct)
    {
        if (count is < 1 or > WitnessCheckpoint.MaxReceipts)
            throw new ArgumentOutOfRangeException(nameof(count));
        ArgumentNullException.ThrowIfNull(checkpoint);
        if (checkpoint.Length is < 100 or > WitnessCheckpoint.MaxCheckpointBytes)
            throw new ArgumentOutOfRangeException(nameof(checkpoint));
        await using var command = anchorWriter.CreateCommand("""
            SELECT anchor.advance_checkpoint(@count, @payload)
            """);
        command.Parameters.AddWithValue("count", count);
        command.Parameters.Add(new NpgsqlParameter("payload", NpgsqlDbType.Bytea)
        {
            Value = checkpoint
        });
        return (int)(await command.ExecuteScalarAsync(ct)
            ?? throw new InvalidDataException("ANCHOR_ADVANCE_UNCONFIRMED."));
    }

    public static Task VerifyWriterAsync(NpgsqlDataSource db,
        CancellationToken ct = default) =>
        WitnessAnchorRoleGuard.VerifyAsync(db, wantWriter: true, ct);
}

/// <summary>
/// EXECUTE-only independent reader never receives credentials for witness
/// writes. An absent head is a denial, not proof of empty erasure history.
/// </summary>
public sealed class PostgresWitnessAnchorReader(NpgsqlDataSource anchorReader)
{
    public async Task<WitnessAnchorState?> ReadAsync(CancellationToken ct)
    {
        await using var cmd = anchorReader.CreateCommand("""
            SELECT checkpoint_count, checkpoint FROM anchor.current_checkpoint()
            """);
        await using var rows = await cmd.ExecuteReaderAsync(ct);
        if (!await rows.ReadAsync(ct)) return null;
        var entry = new WitnessAnchorState(rows.GetInt32(0), rows.GetFieldValue<byte[]>(1));
        if (await rows.ReadAsync(ct))
            throw new InvalidDataException("ANCHOR_MULTIPLE_HEADS.");
        return entry;
    }

    public static Task VerifyReaderAsync(NpgsqlDataSource db,
        CancellationToken ct = default) =>
        WitnessAnchorRoleGuard.VerifyAsync(db, wantWriter: false, ct);
}

internal static class WitnessAnchorRoleGuard
{
    internal static async Task VerifyAsync(NpgsqlDataSource db,
        bool wantWriter, CancellationToken ct)
    {
        await using var cmd = db.CreateCommand("""
            SELECT NOT (
                r.rolsuper OR r.rolbypassrls
                OR pg_has_role(r.oid,
                    (SELECT relowner FROM pg_class
                     WHERE oid = 'anchor.checkpoints'::regclass), 'MEMBER')
                OR has_table_privilege(r.oid, 'anchor.checkpoints',
                    'SELECT, INSERT, UPDATE, DELETE, TRUNCATE, REFERENCES, TRIGGER')
                OR has_table_privilege(r.oid, 'anchor.head',
                    'SELECT, INSERT, UPDATE, DELETE, TRUNCATE, REFERENCES, TRIGGER')
            )
            AND has_function_privilege(r.oid,
                'anchor.advance_checkpoint(integer,bytea)'::regprocedure,
                'EXECUTE') = @writer
            AND has_function_privilege(r.oid,
                'anchor.current_checkpoint()'::regprocedure,
                'EXECUTE') <> @writer
              FROM pg_roles r WHERE r.rolname = current_user
            """);
        cmd.Parameters.AddWithValue("writer", wantWriter);
        if (await cmd.ExecuteScalarAsync(ct) is not true)
            throw new InvalidOperationException("ANCHOR_ROLE_DENIED.");
    }
}

/// <summary>
/// CI-only orchestration. The old anchor MUST be checked against a read-only
/// witness snapshot BEFORE any new checkpoint is signed or submitted.
/// Never invoke after disaster to replace a previously durable external head.
/// </summary>
public sealed class PostgresWitnessAnchorIssuer(
    NpgsqlDataSource witnessReader,
    NpgsqlDataSource anchorWriter,
    NpgsqlDataSource anchorReader)
{
    public async Task<int> IssueAsync(byte[] signingKey, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(signingKey);
        if (signingKey.Length != 32)
            throw new InvalidDataException("ANCHOR_KEY_INVALID.");
        await PostgresErasureWitnessReader.VerifyReaderAsync(witnessReader, ct);
        await PostgresWitnessAnchor.VerifyWriterAsync(anchorWriter, ct);
        await PostgresWitnessAnchorReader.VerifyReaderAsync(anchorReader, ct);

        var current = await new PostgresWitnessAnchorReader(anchorReader)
            .ReadAsync(ct);
        var snapshot = await PostgresErasureWitnessSnapshot.ReadAsync(
            witnessReader, WitnessCheckpoint.MaxReceipts, ct);
        if (current is not null)
        {
            if (WitnessCheckpoint.Verify(snapshot, current.Checkpoint, signingKey)
                != current.Count)
                throw new InvalidDataException("ANCHOR_EXISTING_PREFIX_INVALID.");
        }
        var next = WitnessCheckpoint.Create(snapshot, signingKey);
        if (WitnessCheckpoint.Verify(snapshot, next, signingKey) != snapshot.Count)
            throw new InvalidDataException("ANCHOR_PROPOSED_PREFIX_INVALID.");
        return await new PostgresWitnessAnchor(anchorWriter).AdvanceAsync(
            snapshot.Count, next, ct);
    }
}

/// <summary>
/// Read-only offline check against the independently persisted prefix.
/// This does NOT release API quarantine or attest source completeness.
/// </summary>
public sealed class PostgresWitnessAnchorVerifier(
    NpgsqlDataSource witnessReader, NpgsqlDataSource anchorReader)
{
    public async Task<int> VerifyAsync(byte[] signingKey, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(signingKey);
        if (signingKey.Length != 32)
            throw new InvalidDataException("ANCHOR_KEY_INVALID.");
        await PostgresErasureWitnessReader.VerifyReaderAsync(witnessReader, ct);
        await PostgresWitnessAnchorReader.VerifyReaderAsync(anchorReader, ct);
        var anchored = await new PostgresWitnessAnchorReader(anchorReader)
            .ReadAsync(ct) ?? throw new InvalidDataException("ANCHOR_MISSING.");
        var snapshot = await PostgresErasureWitnessSnapshot.ReadAsync(
            witnessReader, WitnessCheckpoint.MaxReceipts, ct);
        var checkedCount = WitnessCheckpoint.Verify(
            snapshot, anchored.Checkpoint, signingKey);
        if (checkedCount != anchored.Count)
            throw new InvalidDataException("ANCHOR_COUNT_MISMATCH.");
        return checkedCount;
    }
}
