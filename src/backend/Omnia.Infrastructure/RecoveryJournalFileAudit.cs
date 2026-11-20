using System.Security.Cryptography;

namespace Omnia.Infrastructure;

/// <summary>
/// Read-only, offline audit adapter. The journal, independent checkpoint and
/// two distinct 32-byte keys must be supplied through separate PRIVATE files
/// controlled outside the restored PostgreSQL cluster. A local file is not
/// proof of remote custody, monotonicity or completeness by itself.
/// </summary>
public static class RecoveryJournalFileAudit
{
    public static RecoveryJournalVerifier.Verification VerifyFiles(
        string journalPath, string checkpointPath,
        string journalKeyPath, string checkpointKeyPath)
    {
        var journal = ReadPrivate(journalPath,
            RecoveryJournalVerifier.MaxJournalBytes);
        var checkpoint = ReadPrivate(checkpointPath,
            RecoveryJournalVerifier.MaxAnchorBytes);
        var key = ReadPrivate(journalKeyPath, 32);
        var anchorKey = ReadPrivate(checkpointKeyPath, 32);
        try
        {
            return RecoveryJournalVerifier.Verify(
                journal, checkpoint, key, anchorKey);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(journal);
            CryptographicOperations.ZeroMemory(checkpoint);
            CryptographicOperations.ZeroMemory(key);
            CryptographicOperations.ZeroMemory(anchorKey);
        }
    }

    internal static byte[] ReadPrivate(string path, int maximum)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(path) ||
                !Path.IsPathFullyQualified(path))
                throw Invalid();

            var info = new FileInfo(path);
            if (!info.Exists || info.Attributes.HasFlag(FileAttributes.ReparsePoint) ||
                info.Length is < 1 || info.Length > maximum)
                throw Invalid();

            if (!OperatingSystem.IsWindows())
            {
                var mode = File.GetUnixFileMode(path);
                const UnixFileMode prohibited =
                    UnixFileMode.GroupRead | UnixFileMode.GroupWrite |
                    UnixFileMode.GroupExecute | UnixFileMode.OtherRead |
                    UnixFileMode.OtherWrite | UnixFileMode.OtherExecute;
                if ((mode & prohibited) != 0 ||
                    (mode & UnixFileMode.UserRead) == 0)
                    throw Invalid();
            }

            using var stream = new FileStream(path, FileMode.Open,
                FileAccess.Read, FileShare.Read, bufferSize: 4096,
                options: FileOptions.SequentialScan);
            if (stream.Length is < 1 || stream.Length > maximum)
                throw Invalid();
            var bytes = new byte[checked((int)stream.Length)];
            stream.ReadExactly(bytes);
            if (stream.ReadByte() != -1)
            {
                CryptographicOperations.ZeroMemory(bytes);
                throw Invalid();
            }
            return bytes;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException
            or ArgumentException or NotSupportedException or OverflowException)
        {
            throw Invalid();
        }
    }

    private static InvalidDataException Invalid() =>
        new("RECOVERY_JOURNAL_FILE_INVALID: private audit input unavailable.");
}
