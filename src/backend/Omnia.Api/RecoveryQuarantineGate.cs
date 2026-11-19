using Microsoft.Extensions.Configuration;

namespace Omnia.Api;

/// <summary>
/// Deliberately narrow, operator-controlled recovery quarantine for the HTTP
/// process. The state file MUST be hosted outside restored PGDATA and kept
/// outside backups. This is NOT a signed erasure journal or release proof.
/// </summary>
public static class RecoveryQuarantineGate
{
    private const string Option = "Recovery:AccessStateFile";
    private const int MaxBytes = 32;

    /// <summary>
    /// Called before DI registration, database connections and migrations.
    /// Production/other non-Development environments require an explicit
    /// operator-owned private file containing exactly "ready\n".
    /// </summary>
    public static void Verify(IConfiguration config, bool isDevelopment)
    {
        if (!IsReady(config, isDevelopment))
            throw new InvalidOperationException(
                "RECOVERY_QUARANTINED: API access requires an explicit private recovery-ready state.");
    }

    /// <summary>
    /// Rechecks the file on EVERY HTTP request so changing to quarantine or
    /// losing the file causes 503, not continued normal HTTP traffic.
    /// The check is only one defense: it does not stop other worker processes
    /// or in-flight operations and does not authenticate a restore.
    /// </summary>
    public static bool IsReady(IConfiguration config, bool isDevelopment)
    {
        ArgumentNullException.ThrowIfNull(config);
        var path = config[Option];
        if (path is null) return isDevelopment;
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path))
            return false;

        try
        {
            var file = new FileInfo(path);
            if (!file.Exists || file.Length is <= 0 or > MaxBytes ||
                file.Attributes.HasFlag(FileAttributes.ReparsePoint))
                return false;

            if (!OperatingSystem.IsWindows())
            {
                var mode = File.GetUnixFileMode(path);
                var exposed = UnixFileMode.GroupRead | UnixFileMode.GroupWrite |
                    UnixFileMode.GroupExecute | UnixFileMode.OtherRead |
                    UnixFileMode.OtherWrite | UnixFileMode.OtherExecute;
                if ((mode & exposed) != 0 || (mode & UnixFileMode.UserRead) == 0)
                    return false;
            }

            var bytes = File.ReadAllBytes(path);
            return bytes.Length <= MaxBytes &&
                bytes.AsSpan().SequenceEqual("ready\n"u8);
        }
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
        catch (ArgumentException) { return false; }
        catch (NotSupportedException) { return false; }
    }
}
