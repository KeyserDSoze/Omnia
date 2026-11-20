using System.Security.Cryptography;
using Npgsql;

namespace Omnia.Infrastructure;

/// <summary>
/// Private-file adapter for a synthetic witness checkpoint OUTSIDE PGDATA.
/// This is not WORM, a monotonic authority or a vault. CI files are disposable
/// and may be rolled back together with the witness by a privileged operator.
/// </summary>
public static class WitnessCheckpointFileAudit
{
    public static async Task<int> VerifyAsync(
        NpgsqlDataSource witness, string checkpointPath,
        string keyPath, CancellationToken ct)
    {
        await PostgresErasureWitnessReader.VerifyReaderAsync(witness, ct);
        var key = RecoveryJournalFileAudit.ReadPrivate(keyPath, 32);
        var checkpoint = RecoveryJournalFileAudit.ReadPrivate(
            checkpointPath, WitnessCheckpoint.MaxCheckpointBytes);
        try
        {
            var records = await PostgresErasureWitnessSnapshot.ReadAsync(
                witness, WitnessCheckpoint.MaxReceipts, ct);
            return WitnessCheckpoint.Verify(records, checkpoint, key);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
            CryptographicOperations.ZeroMemory(checkpoint);
        }
    }

    /// <summary>
    /// Issue a NEW, non-overwriting synthetic checkpoint; never replaces
    /// or "repairs" an earlier anchor. Production must use external
    /// immutable/monotonic custody, not this local demonstration.
    /// </summary>
    public static async Task<int> IssueNewAsync(
        NpgsqlDataSource witness, string checkpointPath,
        string keyPath, CancellationToken ct)
    {
        await PostgresErasureWitnessReader.VerifyReaderAsync(witness, ct);
        var key = RecoveryJournalFileAudit.ReadPrivate(keyPath, 32);
        try
        {
            RequirePrivateNewPath(checkpointPath);
            var records = await PostgresErasureWitnessSnapshot.ReadAsync(
                witness, WitnessCheckpoint.MaxReceipts, ct);
            var bytes = WitnessCheckpoint.Create(records, key);
            try
            {
                using var stream = new FileStream(checkpointPath,
                    FileMode.CreateNew, FileAccess.Write, FileShare.None,
                    bufferSize: 4096, options: FileOptions.WriteThrough);
                if (!OperatingSystem.IsWindows())
                    File.SetUnixFileMode(checkpointPath,
                        UnixFileMode.UserRead | UnixFileMode.UserWrite);
                stream.Write(bytes);
                stream.Flush(flushToDisk: true);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(bytes);
            }
            return records.Count;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
        }
    }

    private static void RequirePrivateNewPath(string checkpointPath)
    {
        if (string.IsNullOrWhiteSpace(checkpointPath) ||
            !Path.IsPathFullyQualified(checkpointPath))
            throw new InvalidDataException("WITNESS_CHECKPOINT_PATH_INVALID.");

        var directory = Path.GetDirectoryName(checkpointPath);
        if (string.IsNullOrWhiteSpace(directory) ||
            !Directory.Exists(directory) ||
            new DirectoryInfo(directory).Attributes.HasFlag(
                FileAttributes.ReparsePoint))
            throw new InvalidDataException("WITNESS_CHECKPOINT_PATH_INVALID.");
        if (!OperatingSystem.IsWindows())
        {
            var allowed = UnixFileMode.UserRead | UnixFileMode.UserWrite |
                          UnixFileMode.UserExecute;
            var mode = File.GetUnixFileMode(directory);
            if ((mode & allowed) != allowed || (mode & ~allowed) != 0)
                throw new InvalidDataException("WITNESS_CHECKPOINT_PATH_INVALID.");
        }
        // CreateNew is the final atomic no-overwrite guard against races
        // and path reuse, including symlinks.
        if (File.Exists(checkpointPath))
            throw new InvalidDataException("WITNESS_CHECKPOINT_EXISTS.");
    }
}
