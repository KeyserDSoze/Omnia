using System.Security.Cryptography;
using System.Text.Json;

namespace Omnia.Infrastructure;

/// <summary>
/// Local, cooperative append-only journal prototype for synthetic tests.
/// Each append verifies the prior checkpoint before writing a new entry.
/// This is NOT a production issuer or independent anti-rollback authority.
/// The journal and checkpoint are on the SAME filesystem; a privileged
/// adversary can replace both, and OS crashes may leave a stale lock.
/// </summary>
public static class RecoveryJournalLocalAppender
{
    private const UnixFileMode PrivateFile =
        UnixFileMode.UserRead | UnixFileMode.UserWrite;
    private const UnixFileMode PrivateDirectory =
        UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;
    private const UnixFileMode GroupOther =
        UnixFileMode.GroupRead | UnixFileMode.GroupWrite |
        UnixFileMode.GroupExecute | UnixFileMode.OtherRead |
        UnixFileMode.OtherWrite | UnixFileMode.OtherExecute;

    /// <summary>
    /// Reads two distinct, private binary keys with the same fail-closed
    /// safeguards as the offline auditor. Used by synthetic CI recovery,
    /// not exposed through a production HTTP/CLI writer.
    /// </summary>
    public static RecoveryJournalVerifier.Verification AppendFromPrivateFiles(
        string journalPath, string checkpointPath,
        string journalKeyPath, string checkpointKeyPath,
        string tenantId, string ownerSubject)
    {
        var journalKey = RecoveryJournalFileAudit.ReadPrivate(journalKeyPath, 32);
        try
        {
            var checkpointKey =
                RecoveryJournalFileAudit.ReadPrivate(checkpointKeyPath, 32);
            try
            {
                return Append(journalPath, checkpointPath,
                    journalKey, checkpointKey, tenantId, ownerSubject);
            }
            finally { CryptographicOperations.ZeroMemory(checkpointKey); }
        }
        finally { CryptographicOperations.ZeroMemory(journalKey); }
    }

    public static RecoveryJournalVerifier.Verification Append(
        string journalPath, string checkpointPath,
        ReadOnlySpan<byte> journalKey, ReadOnlySpan<byte> checkpointKey,
        string tenantId, string ownerSubject) =>
        AppendKnownEvent(journalPath, checkpointPath, journalKey,
            checkpointKey, Guid.NewGuid(), tenantId, ownerSubject);

    /// <summary>
    /// Idempotent synthetic delivery keyed by a real committed outbox UUID.
    /// Replay of the SAME verified event is a no-op; conflicting reuse
    /// of an event UUID fails closed, never silently overwrites history.
    /// </summary>
    public static RecoveryJournalVerifier.Verification AppendKnownEvent(
        string journalPath, string checkpointPath,
        ReadOnlySpan<byte> journalKey, ReadOnlySpan<byte> checkpointKey,
        Guid committedEventId, string tenantId, string ownerSubject)
    {
        if (committedEventId == Guid.Empty ||
            journalKey.Length != 32 || checkpointKey.Length != 32 ||
            CryptographicOperations.FixedTimeEquals(journalKey, checkpointKey) ||
            !ValidActor(tenantId, 128) || !ValidActor(ownerSubject, 255))
            throw Invalid();

        string lockPath;
        try
        {
            if (string.IsNullOrWhiteSpace(journalPath) ||
                string.IsNullOrWhiteSpace(checkpointPath) ||
                !Path.IsPathFullyQualified(journalPath) ||
                !Path.IsPathFullyQualified(checkpointPath) ||
                string.Equals(journalPath, checkpointPath, StringComparison.Ordinal))
                throw Invalid();

            var journalDir = Path.GetDirectoryName(journalPath);
            var checkpointDir = Path.GetDirectoryName(checkpointPath);
            if (string.IsNullOrWhiteSpace(journalDir) ||
                !string.Equals(journalDir, checkpointDir, StringComparison.Ordinal))
                throw Invalid();

            var directory = new DirectoryInfo(journalDir);
            if (!directory.Exists ||
                directory.Attributes.HasFlag(FileAttributes.ReparsePoint))
                throw Invalid();
            if (!OperatingSystem.IsWindows())
            {
                var mode = File.GetUnixFileMode(journalDir);
                if ((mode & PrivateDirectory) != PrivateDirectory ||
                    (mode & GroupOther) != 0)
                    throw Invalid();
            }
            lockPath = journalPath + ".writer.lock";
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or
                                   UnauthorizedAccessException or NotSupportedException)
        {
            throw Invalid();
        }

        FileStream? writerLock = null;
        try
        {
            // Exclusive create fails closed if another writer or a crash
            // left a lock. NEVER auto-break a stale lock: manual review first.
            writerLock = new FileStream(lockPath, FileMode.CreateNew,
                FileAccess.Write, FileShare.None);
            SetPrivate(lockPath);
            writerLock.Flush(flushToDisk: true);

            var journalExists = File.Exists(journalPath);
            var checkpointExists = File.Exists(checkpointPath);
            if (journalExists != checkpointExists)
                throw Invalid();

            byte[] priorJournal = [];
            byte[] priorCheckpoint = [];
            var previousCount = 0;
            var previousTip = RecoveryJournalVerifier.GenesisDigest;
            if (journalExists)
            {
                priorJournal = RecoveryJournalFileAudit.ReadPrivate(
                    journalPath, RecoveryJournalVerifier.MaxJournalBytes);
                priorCheckpoint = RecoveryJournalFileAudit.ReadPrivate(
                    checkpointPath, RecoveryJournalVerifier.MaxAnchorBytes);
                var checkedState = RecoveryJournalVerifier.Verify(
                    priorJournal, priorCheckpoint, journalKey, checkpointKey);
                previousCount = checkedState.Count;
                previousTip = checkedState.TipDigest;

                // A verified entry with this UUID must represent EXACTLY
                // the same actor and operation. Journal bytes were already
                // MAC/chain checked; never trust an unverified entry.
                var knownId = committedEventId.ToString("N");
                var lines = System.Text.Encoding.UTF8.GetString(priorJournal)
                    .Split('\n', StringSplitOptions.RemoveEmptyEntries);
                foreach (var line in lines)
                {
                    using var record = JsonDocument.Parse(line);
                    var item = record.RootElement;
                    if (item.GetProperty("eventId").GetString() != knownId)
                        continue;
                    if (item.GetProperty("tenantId").GetString() != tenantId ||
                        item.GetProperty("ownerSubject").GetString() != ownerSubject ||
                        item.GetProperty("operation").GetString() != "erase-actor")
                        throw Invalid();
                    return checkedState; // Re-delivery has no side effects.
                }
            }

            if (previousCount >= RecoveryJournalVerifier.MaxEntries)
                throw Invalid();

            var sequence = previousCount + 1;
            var eventId = committedEventId.ToString("N");
            const string operation = "erase-actor";
            var payload = RecoveryJournalVerifier.EntryPayload(
                sequence, eventId, operation, tenantId, ownerSubject, previousTip);
            var tag = HMACSHA256.HashData(journalKey, payload);
            var newTip = Convert.ToBase64String(
                SHA256.HashData([.. payload, .. tag]));
            var newEntry = JsonSerializer.SerializeToUtf8Bytes(new
            {
                schemaVersion = 1, sequence, eventId, operation,
                tenantId, ownerSubject, previousDigest = previousTip,
                mac = Convert.ToBase64String(tag)
            });
            if (newEntry.Length > 1024 ||
                priorJournal.Length + newEntry.Length + 1 >
                    RecoveryJournalVerifier.MaxJournalBytes)
                throw Invalid();

            // 1. Append the signed record and fsync. A crash here, BEFORE
            // checkpoint publication, MUST leave a mismatch and block reuse.
            using (var output = new FileStream(journalPath,
                journalExists ? FileMode.Open : FileMode.CreateNew,
                FileAccess.ReadWrite, FileShare.None))
            {
                if (journalExists)
                {
                    if (output.Length != priorJournal.Length)
                        throw Invalid();
                    var current = new byte[priorJournal.Length];
                    output.ReadExactly(current);
                    if (!CryptographicOperations.FixedTimeEquals(current, priorJournal))
                        throw Invalid();
                }
                else SetPrivate(journalPath);
                output.Seek(0, SeekOrigin.End);
                output.Write(newEntry);
                output.WriteByte((byte)'\n');
                output.Flush(flushToDisk: true);
            }

            // 2. Write a private checkpoint to a sibling temporary file,
            // fsync, then rename over the old checkpoint on the SAME volume.
            // Never overwrite journal history to "fix" mismatched state.
            var checkpointPayload = RecoveryJournalVerifier.AnchorPayload(
                sequence, newTip);
            var checkpointTag = HMACSHA256.HashData(
                checkpointKey, checkpointPayload);
            var checkpoint = JsonSerializer.SerializeToUtf8Bytes(new
            {
                schemaVersion = 1, count = sequence, tipDigest = newTip,
                mac = Convert.ToBase64String(checkpointTag)
            });

            var temporaryPath = checkpointPath + "." +
                Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                using (var temp = new FileStream(temporaryPath,
                    FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    SetPrivate(temporaryPath);
                    temp.Write(checkpoint);
                    temp.Flush(flushToDisk: true);
                }
                if (checkpointExists)
                {
                    var currentCheckpoint = RecoveryJournalFileAudit.ReadPrivate(
                        checkpointPath, RecoveryJournalVerifier.MaxAnchorBytes);
                    if (!CryptographicOperations.FixedTimeEquals(
                        currentCheckpoint, priorCheckpoint))
                        throw Invalid();
                }
                else if (File.Exists(checkpointPath))
                    throw Invalid();

                File.Move(temporaryPath, checkpointPath, overwrite: true);
            }
            finally
            {
                if (File.Exists(temporaryPath))
                    File.Delete(temporaryPath);
            }

            // Verify the persisted result, not just the value calculated.
            return RecoveryJournalVerifier.Verify(
                RecoveryJournalFileAudit.ReadPrivate(
                    journalPath, RecoveryJournalVerifier.MaxJournalBytes),
                RecoveryJournalFileAudit.ReadPrivate(
                    checkpointPath, RecoveryJournalVerifier.MaxAnchorBytes),
                journalKey, checkpointKey);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                                   or ArgumentException or NotSupportedException)
        {
            throw Invalid();
        }
        finally
        {
            if (writerLock is not null)
            {
                writerLock.Dispose();
                // Only remove the lock created by this invocation.
                // A process crash leaves it behind and fails closed.
                try { File.Delete(lockPath); }
                catch (IOException) { /* operator intervention required */ }
                catch (UnauthorizedAccessException) { /* fail closed */ }
            }
        }
    }

    private static void SetPrivate(string path)
    {
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(path, PrivateFile);
    }

    private static bool ValidActor(string? value, int maxLength) =>
        value is { Length: > 0 } && value.Length <= maxLength &&
        !string.IsNullOrWhiteSpace(value) &&
        value.All(c => !char.IsControl(c));

    private static InvalidDataException Invalid() =>
        new("RECOVERY_JOURNAL_APPEND_DENIED: journal/checkpoint state untrusted.");
}
