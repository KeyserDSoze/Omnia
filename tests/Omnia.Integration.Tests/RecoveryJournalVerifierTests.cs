using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Omnia.Infrastructure;
using Xunit;

namespace Omnia.Integration.Tests;

/// <summary>
/// All IDs/data are synthetic. This tests authenticated, bounded recovery
/// manifest format; it cannot establish trusted external journal custody.
/// </summary>
public sealed class RecoveryJournalVerifierTests
{
    [Fact]
    public void CanonicalHmacJsonDoesNotEscapeBase64PlusAndEquals()
    {
        var payload = RecoveryJournalVerifier.EntryPayload(
            1, Guid.NewGuid().ToString("N"), "erase-actor",
            "synthetic-tenant", "synthetic-owner", "ab+/cd==");
        var anchor = RecoveryJournalVerifier.AnchorPayload(1, "ab+/cd==");
        Assert.Contains("\"previousDigest\":\"ab+/cd==\"",
            Encoding.UTF8.GetString(payload));
        Assert.Contains("\"tipDigest\":\"ab+/cd==\"",
            Encoding.UTF8.GetString(anchor));
        Assert.DoesNotContain("\\u002B", Encoding.UTF8.GetString(payload));
    }

    [Fact]
    public void AcceptsOrderedSignedJournalOnlyWithMatchingIndependentAnchor()
    {
        var fixture = new JournalFixture();
        fixture.Add("synthetic-tenant-a", "synthetic-owner-a");
        fixture.Add("synthetic-tenant-b", "synthetic-owner-b");
        var (journal, anchor) = fixture.GetArtifacts();

        var verified = RecoveryJournalVerifier.Verify(
            journal, anchor, fixture.JournalKey, fixture.AnchorKey);
        Assert.Equal(2, verified.Count);
        Assert.Equal(fixture.Tip, verified.TipDigest);
    }

    [Fact]
    public void RejectsTruncatedJournalAndIndependentAnchorRollback()
    {
        var fixture = new JournalFixture();
        fixture.Add("synthetic-tenant-a", "synthetic-owner-a");
        var oldAnchor = fixture.GetArtifacts().Anchor;
        fixture.Add("synthetic-tenant-b", "synthetic-owner-b");
        var (journal, anchor) = fixture.GetArtifacts();

        var first = Encoding.UTF8.GetBytes(
            Encoding.UTF8.GetString(journal).Split('\n')[0] + "\n");
        Reject(fixture, first, anchor);
        Reject(fixture, journal, oldAnchor);
        Reject(fixture, [], anchor);
        Reject(fixture, journal, []);
    }

    [Fact]
    public void RejectsGapsDuplicatesReorderingAndAlteredOrForgedEntries()
    {
        var f = new JournalFixture();
        f.Add("synthetic-tenant-a", "synthetic-owner-a");
        f.Add("synthetic-tenant-b", "synthetic-owner-b");
        var (journal, anchor) = f.GetArtifacts();
        var lines = Encoding.UTF8.GetString(journal).Split('\n');
        Reject(f, Encoding.UTF8.GetBytes(lines[1] + "\n" + lines[0] + "\n"), anchor);
        Reject(f, Encoding.UTF8.GetBytes(lines[0] + "\n" + lines[0] + "\n"), anchor);
        Reject(f, Encoding.UTF8.GetBytes(
            Encoding.UTF8.GetString(journal).Replace(
                "synthetic-owner-b", "synthetic-owner-z", StringComparison.Ordinal)), anchor);
        Reject(f, Encoding.UTF8.GetBytes(
            Encoding.UTF8.GetString(journal).Replace(
                "\"sequence\":2", "\"sequence\":3", StringComparison.Ordinal)), anchor);
        Reject(f, Encoding.UTF8.GetBytes(
            Encoding.UTF8.GetString(journal).Replace(
                "\"operation\":\"erase-actor\"",
                "\"operation\":\"revoke-consent\"", StringComparison.Ordinal)), anchor);
        Reject(f, Encoding.UTF8.GetBytes(
            Encoding.UTF8.GetString(journal).TrimEnd('\n')), anchor);
        Reject(f, Encoding.UTF8.GetBytes(
            lines[0].Replace("\"schemaVersion\":1",
                "\"schemaVersion\":1,\"schemaVersion\":1", StringComparison.Ordinal)
            + "\n" + lines[1] + "\n"), anchor);
    }

    [Fact]
    public void RejectsWrongKeysTamperedAnchorAndUnexpectedFields()
    {
        var f = new JournalFixture();
        f.Add("synthetic-tenant-a", "synthetic-owner-a");
        var (journal, anchor) = f.GetArtifacts();
        var wrong = RandomNumberGenerator.GetBytes(32);
        Assert.Throws<InvalidDataException>(() =>
            RecoveryJournalVerifier.Verify(journal, anchor, wrong, f.AnchorKey));
        Assert.Throws<InvalidDataException>(() =>
            RecoveryJournalVerifier.Verify(journal, anchor, f.JournalKey, wrong));

        var alteredAnchor = Encoding.UTF8.GetBytes(
            Encoding.UTF8.GetString(anchor).Replace(
                "\"count\":1", "\"count\":2", StringComparison.Ordinal));
        Reject(f, journal, alteredAnchor);
        var ambiguous = Encoding.UTF8.GetBytes(
            Encoding.UTF8.GetString(anchor).Replace(
                "\"count\":1", "\"count\":1,\"count\":1", StringComparison.Ordinal));
        Reject(f, journal, ambiguous);
        var extra = Encoding.UTF8.GetBytes(
            Encoding.UTF8.GetString(anchor).Replace(
                "\"count\":1", "\"count\":1,\"ready\":true", StringComparison.Ordinal));
        Reject(f, journal, extra);
        Assert.Throws<InvalidDataException>(() =>
            RecoveryJournalVerifier.Verify(journal, anchor, new byte[3], f.AnchorKey));
    }

    [Fact]
    public void RejectsEmptyOrOversizedJournalsBeforeTrustingContent()
    {
        var f = new JournalFixture();
        f.Add("synthetic-tenant", "synthetic-owner");
        var (journal, anchor) = f.GetArtifacts();
        Reject(f, new byte[1024 * 1024 + 1], anchor);
        Reject(f, journal, new byte[4097]);
        Reject(f, Encoding.UTF8.GetBytes("\n"), anchor);
        Reject(f, Encoding.UTF8.GetBytes(
            Encoding.UTF8.GetString(journal) + "\n"), anchor);
    }

    [Fact]
    public void FileAuditRequiresAbsolutePrivateRegularFilesAndKeepsSecretsOutOfResult()
    {
        var fixture = new JournalFixture();
        fixture.Add("synthetic-tenant-a", "synthetic-owner-a");
        using var files = new PrivateJournalFiles(fixture);
        var verified = RecoveryJournalFileAudit.VerifyFiles(
            files.Journal, files.Anchor, files.JournalKey, files.AnchorKey);
        Assert.Equal(1, verified.Count);

        Assert.Throws<InvalidDataException>(() =>
            RecoveryJournalFileAudit.VerifyFiles(
                "relative.jsonl", files.Anchor, files.JournalKey, files.AnchorKey));
        Assert.Throws<InvalidDataException>(() =>
            RecoveryJournalFileAudit.VerifyFiles(
                files.Journal + ".missing", files.Anchor,
                files.JournalKey, files.AnchorKey));

        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(files.Anchor,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead);
            Assert.Throws<InvalidDataException>(() =>
                RecoveryJournalFileAudit.VerifyFiles(
                    files.Journal, files.Anchor, files.JournalKey, files.AnchorKey));
            File.SetUnixFileMode(files.Anchor,
                UnixFileMode.UserRead | UnixFileMode.UserWrite);
            var symlink = files.Journal + ".link";
            File.CreateSymbolicLink(symlink, files.Journal);
            try
            {
                Assert.Throws<InvalidDataException>(() =>
                    RecoveryJournalFileAudit.VerifyFiles(
                        symlink, files.Anchor, files.JournalKey, files.AnchorKey));
            }
            finally { File.Delete(symlink); }
        }
    }

    private sealed class PrivateJournalFiles : IDisposable
    {
        private readonly string _directory = Path.Combine(Path.GetTempPath(),
            "omnia-audit-fixture-" + Guid.NewGuid().ToString("N"));
        public string Journal { get; }
        public string Anchor { get; }
        public string JournalKey { get; }
        public string AnchorKey { get; }

        public PrivateJournalFiles(JournalFixture f)
        {
            Directory.CreateDirectory(_directory);
            if (!OperatingSystem.IsWindows())
                File.SetUnixFileMode(_directory,
                    UnixFileMode.UserRead | UnixFileMode.UserWrite |
                    UnixFileMode.UserExecute);
            Journal = Path.Combine(_directory, "journal.jsonl");
            Anchor = Path.Combine(_directory, "checkpoint.json");
            JournalKey = Path.Combine(_directory, "journal.key");
            AnchorKey = Path.Combine(_directory, "anchor.key");
            var (journal, anchor) = f.GetArtifacts();
            Write(Journal, journal);
            Write(Anchor, anchor);
            Write(JournalKey, f.JournalKey);
            Write(AnchorKey, f.AnchorKey);
        }

        private static void Write(string path, byte[] bytes)
        {
            File.WriteAllBytes(path, bytes);
            if (!OperatingSystem.IsWindows())
                File.SetUnixFileMode(path,
                    UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }

        public void Dispose() => Directory.Delete(_directory, recursive: true);
    }

    private static void Reject(JournalFixture f, byte[] journal, byte[] anchor) =>
        Assert.Throws<InvalidDataException>(() =>
            RecoveryJournalVerifier.Verify(journal, anchor, f.JournalKey, f.AnchorKey));

    private sealed class JournalFixture
    {
        public byte[] JournalKey { get; } = RandomNumberGenerator.GetBytes(32);
        public byte[] AnchorKey { get; } = RandomNumberGenerator.GetBytes(32);
        private readonly List<byte[]> _lines = [];
        public string Tip { get; private set; } = RecoveryJournalVerifier.GenesisDigest;

        public void Add(string tenant, string owner)
        {
            var seq = _lines.Count + 1;
            var id = Guid.NewGuid().ToString("N");
            var signed = RecoveryJournalVerifier.EntryPayload(
                seq, id, "erase-actor", tenant, owner, Tip);
            var tag = HMACSHA256.HashData(JournalKey, signed);
            var bytes = JsonSerializer.SerializeToUtf8Bytes(new
            {
                schemaVersion = 1, sequence = seq, eventId = id,
                operation = "erase-actor", tenantId = tenant,
                ownerSubject = owner, previousDigest = Tip,
                mac = Convert.ToBase64String(tag)
            });
            _lines.Add(bytes);
            Tip = Convert.ToBase64String(SHA256.HashData([.. signed, .. tag]));
        }

        public (byte[] Journal, byte[] Anchor) GetArtifacts()
        {
            var anchorPayload = RecoveryJournalVerifier.AnchorPayload(_lines.Count, Tip);
            var anchorMac = HMACSHA256.HashData(AnchorKey, anchorPayload);
            var anchor = JsonSerializer.SerializeToUtf8Bytes(new
            {
                schemaVersion = 1, count = _lines.Count,
                tipDigest = Tip, mac = Convert.ToBase64String(anchorMac)
            });
            var journal = Encoding.UTF8.GetBytes(string.Join("\n",
                _lines.Select(Encoding.UTF8.GetString)) + "\n");
            return (journal, anchor);
        }
    }
}
