using System.Security.Cryptography;
using System.Text;
using Omnia.Infrastructure;
using Xunit;

namespace Omnia.Integration.Tests;

/// <summary>
/// File appends use only synthetic fixtures. No production issuer is exposed.
/// A local lock/checkpoint does not constitute independent durable custody.
/// </summary>
public sealed class RecoveryJournalLocalAppendTests
{
    [Fact]
    public void InitialAndSubsequentAppendsAreVerifiedAndNeverRewritePriorEvents()
    {
        using var fixture = new PrivateFiles();
        var one = fixture.Append("tenant-first", "synthetic-subject-a");
        Assert.Equal(1, one.Count);
        var previous = File.ReadAllBytes(fixture.Journal);
        var checkpointAtOne = File.ReadAllBytes(fixture.Checkpoint);
        var two = fixture.Append("tenant-other", "synthetic-subject-b");
        Assert.Equal(2, two.Count);
        Assert.NotEqual(one.TipDigest, two.TipDigest);
        Assert.StartsWith(Encoding.UTF8.GetString(previous),
            File.ReadAllText(fixture.Journal), StringComparison.Ordinal);
        Assert.NotEqual(Convert.ToBase64String(checkpointAtOne),
            Convert.ToBase64String(File.ReadAllBytes(fixture.Checkpoint)));
        Assert.Equal(2, fixture.Verify().Count);
        Assert.False(File.Exists(fixture.LockPath));
        Assert.Equal(2, File.ReadAllLines(fixture.Journal).Length);

        // Separate process/instance can resume from the authenticated
        // checkpoint without knowing the previous in-memory tip.
        var three = RecoveryJournalLocalAppender.Append(fixture.Journal,
            fixture.Checkpoint, fixture.Key, fixture.AnchorKey,
            "tenant-third", "synthetic-subject-c");
        Assert.Equal(3, three.Count);
        Assert.Equal(3, fixture.Verify().Count);
    }

    [Fact]
    public void StableOutboxUuidIsIdempotentButConflictingActorFailsClosed()
    {
        using var fixture = new PrivateFiles();
        var id = Guid.NewGuid();
        var first = RecoveryJournalLocalAppender.AppendKnownEvent(
            fixture.Journal, fixture.Checkpoint, fixture.Key,
            fixture.AnchorKey, id, "tenant-one", "actor-one");
        Assert.Equal(1, first.Count);
        var before = File.ReadAllBytes(fixture.Journal);

        var duplicate = RecoveryJournalLocalAppender.AppendKnownEvent(
            fixture.Journal, fixture.Checkpoint, fixture.Key,
            fixture.AnchorKey, id, "tenant-one", "actor-one");
        Assert.Equal(first, duplicate);
        Assert.Equal(before, File.ReadAllBytes(fixture.Journal));

        Assert.Throws<InvalidDataException>(() =>
            RecoveryJournalLocalAppender.AppendKnownEvent(
                fixture.Journal, fixture.Checkpoint, fixture.Key,
                fixture.AnchorKey, id, "tenant-two", "actor-two"));
        Assert.Throws<InvalidDataException>(() =>
            RecoveryJournalLocalAppender.AppendKnownEvent(
                fixture.Journal, fixture.Checkpoint, fixture.Key,
                fixture.AnchorKey, Guid.Empty, "tenant-one", "actor-one"));
        Assert.Equal(before, File.ReadAllBytes(fixture.Journal));
        Assert.Equal(1, fixture.Verify().Count);
    }

    [Fact]
    public void MissingOrCorruptCheckpointNeverAuthorizesASecondAppend()
    {
        using var fixture = new PrivateFiles();
        fixture.Append("tenant-a", "subject-a");
        var before = File.ReadAllBytes(fixture.Journal);
        var saved = File.ReadAllBytes(fixture.Checkpoint);
        File.Delete(fixture.Checkpoint);
        Assert.Throws<InvalidDataException>(() =>
            fixture.Append("tenant-b", "subject-b"));
        Assert.Equal(before, File.ReadAllBytes(fixture.Journal));
        File.WriteAllBytes(fixture.Checkpoint, saved);
        fixture.Secure(fixture.Checkpoint);
        File.WriteAllText(fixture.Checkpoint, "{}");
        fixture.Secure(fixture.Checkpoint);
        Assert.Throws<InvalidDataException>(() =>
            fixture.Append("tenant-b", "subject-b"));
        Assert.Equal(before, File.ReadAllBytes(fixture.Journal));
        File.WriteAllBytes(fixture.Checkpoint, saved);
        fixture.Secure(fixture.Checkpoint);
        Assert.Equal(2, fixture.Append("tenant-b", "subject-b").Count);
    }

    [Fact]
    public void CrashWindowOrJournalTruncationStopsFutureWritesFailClosed()
    {
        using var fixture = new PrivateFiles();
        fixture.Append("tenant-a", "subject-a");
        // Model power loss after append fsync, before checkpoint rename.
        File.AppendAllText(fixture.Journal, "{\"sequence\":2}\n");
        var before = File.ReadAllBytes(fixture.Journal);
        Assert.Throws<InvalidDataException>(() =>
            fixture.Append("tenant-b", "subject-b"));
        Assert.Equal(before, File.ReadAllBytes(fixture.Journal));
        Assert.Throws<InvalidDataException>(() => fixture.Verify());
    }

    [Fact]
    public void ActiveWriterLockAndUnsafeFilesystemAreRejected()
    {
        using var fixture = new PrivateFiles();
        fixture.Append("tenant-a", "subject-a");
        using (var lockFile = new FileStream(fixture.LockPath,
            FileMode.CreateNew, FileAccess.Write, FileShare.None))
        {
            Assert.Throws<InvalidDataException>(() =>
                fixture.Append("tenant-b", "subject-b"));
        }
        File.Delete(fixture.LockPath);
        Assert.Equal(2, fixture.Append("tenant-b", "subject-b").Count);
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(fixture.Journal,
                UnixFileMode.UserRead | UnixFileMode.UserWrite |
                UnixFileMode.OtherRead);
            Assert.Throws<InvalidDataException>(() =>
                fixture.Append("tenant-c", "subject-c"));
            fixture.Secure(fixture.Journal);
            File.SetUnixFileMode(fixture.Directory,
                UnixFileMode.UserRead | UnixFileMode.UserWrite |
                UnixFileMode.UserExecute | UnixFileMode.GroupRead);
            Assert.Throws<InvalidDataException>(() =>
                fixture.Append("tenant-c", "subject-c"));
        }
    }

    [Fact]
    public void InvalidKeysAndSubjectIdentifiersCannotCreateNewJournal()
    {
        using var fixture = new PrivateFiles();
        Assert.Throws<InvalidDataException>(() =>
            RecoveryJournalLocalAppender.Append(fixture.Journal,
                fixture.Checkpoint, new byte[4], fixture.AnchorKey,
                "tenant", "subject"));
        Assert.Throws<InvalidDataException>(() =>
            RecoveryJournalLocalAppender.Append(fixture.Journal,
                fixture.Checkpoint, fixture.Key, fixture.Key,
                "tenant", "subject"));
        Assert.Throws<InvalidDataException>(() =>
            fixture.Append("tenant", "subject\nunsafe"));
        Assert.Throws<InvalidDataException>(() =>
            fixture.Append("", "subject"));
        Assert.False(File.Exists(fixture.Journal));
        Assert.False(File.Exists(fixture.Checkpoint));
    }

    private sealed class PrivateFiles : IDisposable
    {
        public string Directory { get; } = Path.Combine(Path.GetTempPath(),
            "omnia-append-journal-" + Guid.NewGuid().ToString("N"));
        public string Journal => Path.Combine(Directory, "journal.jsonl");
        public string Checkpoint => Path.Combine(Directory, "checkpoint.json");
        public string LockPath => Journal + ".writer.lock";
        public byte[] Key { get; } = RandomNumberGenerator.GetBytes(32);
        public byte[] AnchorKey { get; } = RandomNumberGenerator.GetBytes(32);

        public PrivateFiles()
        {
            System.IO.Directory.CreateDirectory(Directory);
            if (!OperatingSystem.IsWindows())
                File.SetUnixFileMode(Directory,
                    UnixFileMode.UserRead | UnixFileMode.UserWrite |
                    UnixFileMode.UserExecute);
        }

        public RecoveryJournalVerifier.Verification Append(string tenant, string owner) =>
            RecoveryJournalLocalAppender.Append(Journal, Checkpoint,
                Key, AnchorKey, tenant, owner);

        public RecoveryJournalVerifier.Verification Verify() =>
            RecoveryJournalVerifier.Verify(File.ReadAllBytes(Journal),
                File.ReadAllBytes(Checkpoint), Key, AnchorKey);

        public void Secure(string file)
        {
            if (!OperatingSystem.IsWindows())
                File.SetUnixFileMode(file,
                    UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }

        public void Dispose()
        {
            CryptographicOperations.ZeroMemory(Key);
            CryptographicOperations.ZeroMemory(AnchorKey);
            if (System.IO.Directory.Exists(Directory))
            {
                if (!OperatingSystem.IsWindows())
                    File.SetUnixFileMode(Directory,
                        UnixFileMode.UserRead | UnixFileMode.UserWrite |
                        UnixFileMode.UserExecute);
                System.IO.Directory.Delete(Directory, recursive: true);
            }
        }
    }
}
