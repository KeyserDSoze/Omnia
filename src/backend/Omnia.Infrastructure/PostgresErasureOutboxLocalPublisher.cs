using System.Security.Cryptography;
using Npgsql;

namespace Omnia.Infrastructure;

/// <summary>
/// Synthetic, local-only bounded bridge from committed PostgreSQL erasure
/// outbox to the existing authenticated filesystem journal. Source login
/// is EXECUTE-only, and each event preserves its original durable UUID.
/// Re-scanning from the start is intentional: (created_at,id) is NOT a
/// commit-order high-watermark. No DB ACK is written, no global traffic
/// release is ever authorized and the local filesystem is NOT offsite.
/// </summary>
public sealed class PostgresErasureOutboxLocalPublisher(
    NpgsqlDataSource dispatchDb, string journalPath, string checkpointPath,
    string journalKeyPath, string checkpointKeyPath)
{
    public async Task<int> PublishAllAsync(int maxEvents, CancellationToken ct)
    {
        if (maxEvents is < 1 or > RecoveryJournalVerifier.MaxEntries)
            throw new ArgumentOutOfRangeException(nameof(maxEvents));

        // Check the SQL identity BEFORE reading signing keys or opening
        // any file. Wrong runtime/audit/retention credentials fail closed.
        await PostgresErasureDispatchReader.VerifyRoleAsync(dispatchDb, ct);
        var journalKey = RecoveryJournalFileAudit.ReadPrivate(journalKeyPath, 32);
        try
        {
            var checkpointKey =
                RecoveryJournalFileAudit.ReadPrivate(checkpointKeyPath, 32);
            try
            {
                var reader = new PostgresErasureDispatchReader(dispatchDb);
                DateTimeOffset? cursorAt = null;
                Guid? cursorId = null;
                var scanned = 0;

                while (true)
                {
                    ct.ThrowIfCancellationRequested();
                    // No persisted DB cursor or ACK; every fresh invocation
                    // rescans from the beginning and deduplicates at sink.
                    var batchSize = Math.Min(
                        PostgresErasureDispatchReader.MaxBatchSize,
                        maxEvents - scanned);
                    if (batchSize == 0)
                    {
                        // MaxEvents is an operational safety limit, not a
                        // successful proof of all events having been delivered.
                        if ((await reader.ReadBatchAsync(1,
                            cursorAt, cursorId, ct)).Count > 0)
                            throw new InvalidOperationException(
                                "Dispatch scan exceeded its configured bound.");
                        return scanned;
                    }

                    var batch = await reader.ReadBatchAsync(
                        batchSize, cursorAt, cursorId, ct);
                    foreach (var entry in batch)
                    {
                        ct.ThrowIfCancellationRequested();
                        if (entry.EventType != "erase-actor")
                            throw new InvalidDataException(
                                "Unsupported erasure event type.");
                        RecoveryJournalLocalAppender.AppendKnownEvent(
                            journalPath, checkpointPath, journalKey,
                            checkpointKey, entry.Id,
                            entry.TenantId, entry.OwnerSubject);
                        scanned++;
                    }
                    if (batch.Count == 0)
                        return scanned;
                    cursorAt = batch[^1].OccurredAt;
                    cursorId = batch[^1].Id;
                }
            }
            finally { CryptographicOperations.ZeroMemory(checkpointKey); }
        }
        finally { CryptographicOperations.ZeroMemory(journalKey); }
    }
}
