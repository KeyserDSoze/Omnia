using System.Security.Cryptography;
using System.Text.Json;
using Npgsql;
using Omnia.Domain;
using Omnia.Infrastructure;

namespace Omnia.RestoreRehearsal;

/// <summary>
/// Only a synthetic disposable-DB test harness. NEVER expose as HTTP API or
/// operate this program on personal/production datasets.
/// No secrets, AAD, personal IDs or connection strings are emitted.
/// </summary>
internal static class Program
{
    private const string Tenant = "omnia-restore-drill-synthetic";
    private const string Owner = "synthetic-owner-only";
    private const string Source = "manual";
    private const string External = "restore-fixture-v1";
    private const string Raw = "OMNIA_SYNTHETIC_RESTORE_FIXTURE";
    private const string Normalized = "{\"text\":\"OMNIA_SYNTHETIC_RESTORE_FIXTURE\"}";
    private const string Summary = "synthetic restore summary";
    private const string Model = "synthetic-vector-model";
    private const string PeerTenant = "omnia-restore-peer-synthetic";
    private const string PeerOwner = "unrelated-synthetic-peer";
    private const string UnwitnessedTenant = "omnia-ci-unwitnessed-pitr-synthetic";
    private const string UnwitnessedOwner = "synthetic-unwitnessed-pitr-owner";
    private const string ReceiptPoint = "omnia_ci_before_destructive_change";
    private const string ReceiptOperation = "erase-actor";

    public static async Task<int> Main(string[] args)
    {
        try
        {
            if (args.Length != 1 || args[0] is not
                ("seed" or "verify" or "missing-old-key" or "erase" or
                 "seed-peer" or "verify-peer" or "seed-unwitnessed" or
                 "erase-unwitnessed" or "verify-unwitnessed-resurrected" or
                 "sign-receipt" or
                 "append-journal" or "erase-source" or "publish-source" or
                  "witness-reconcile" or "reconcile" or "verify-erased"))
                throw new InvalidOperationException("Invalid restore rehearsal phase.");

            var dbConnection = Required("OMNIA_REHEARSAL_DB");
            var ringPath = Required("OMNIA_REHEARSAL_KEYRING_PATH");
            // CI-only fixed loopback sockets. The physical recovered
            // cluster runs on 15435 and retains the original database name.
            // Never accept non-test DBs, remote hosts or privileged logins.
            var target = new NpgsqlConnectionStringBuilder(dbConnection);
            var sourceFixture = target.Port == 15432 &&
                target.Database is ("omnia_test" or "omnia_restore_drill");
            var independentRecovery = target.Port == 15433 &&
                target.Database == "omnia_recovery";
            var physicalRecovery = target.Port == 15435 &&
                target.Database == "omnia_test";
            if (target.Host != "127.0.0.1" ||
                !(sourceFixture || independentRecovery || physicalRecovery) ||
                !string.Equals(target.Username, "omnia_app", StringComparison.Ordinal))
                throw new InvalidOperationException("Restore rehearsal only supports disposable test databases.");
            if (args[0] is "sign-receipt" or "seed-peer" or "append-journal" or
                "erase-source" or "publish-source" or
                "seed-unwitnessed" or "erase-unwitnessed" && !(
                target.Port == 15432 && target.Database == "omnia_test"))
                throw new InvalidOperationException("Source-only synthetic recovery phase.");
            if (args[0] is "reconcile" or "witness-reconcile" or "verify-erased" or
                "verify-peer" or "verify-unwitnessed-resurrected" &&
                !physicalRecovery)
                throw new InvalidOperationException("Physical recovery test phase only.");
            await using var db = NpgsqlDataSource.Create(dbConnection);
            await PostgresRuntimeGuard.VerifyAsync(db);
            using var ring = EnvelopeKeyRing.FromFile(ringPath);
            var crypto = new AesGcmEnvelopeEncryption(ring);
            switch (args[0])
            {
                case "seed":
                    await SeedAsync(db, crypto);
                    break;
                case "verify":
                    await VerifyAsync(db, crypto);
                    break;
                case "missing-old-key":
                    await VerifyMissingKeyAsync(db, crypto);
                    break;
                case "erase":
                    await EraseRestoredAsync(db, crypto);
                    break;
                case "seed-peer":
                    await SeedPeerAsync(db, crypto);
                    break;
                case "seed-unwitnessed":
                    await SeedUnwitnessedAsync(db, crypto);
                    break;
                case "erase-unwitnessed":
                    await EraseUnwitnessedAsync(db, crypto);
                    break;
                case "verify-unwitnessed-resurrected":
                    await VerifyUnwitnessedResurrectedAsync(db, crypto);
                    break;
                case "verify-peer":
                    await VerifyPeerAsync(db, crypto);
                    break;
                case "sign-receipt":
                    SignSyntheticReceipt();
                    break;
                case "append-journal":
                    RecoveryJournalLocalAppender.AppendFromPrivateFiles(
                        Required("OMNIA_RECOVERY_JOURNAL_PATH"),
                        Required("OMNIA_RECOVERY_CHECKPOINT_PATH"),
                        Required("OMNIA_RECOVERY_JOURNAL_KEY_PATH"),
                        Required("OMNIA_RECOVERY_CHECKPOINT_KEY_PATH"),
                        Tenant, Owner);
                    break;
                case "erase-source":
                    await EraseSourceAsync(db, crypto);
                    break;
                case "publish-source":
                    await PublishSourceToWitnessAsync();
                    break;
                case "witness-reconcile":
                    await ReconcileFromWitnessAsync(db, crypto);
                    break;
                case "reconcile":
                    await ReconcileSyntheticErasureAsync(db, crypto);
                    break;
                case "verify-erased":
                    await VerifyErasedAsync(db, crypto);
                    break;
            }
            Console.WriteLine("Synthetic restore rehearsal phase passed.");
            return 0;
        }
        catch
        {
            // Deliberately no exception.ToString(): the values are secret mount
            // locations, credentials, identifiers and potentially PII.
            Console.Error.WriteLine("Synthetic restore rehearsal phase failed.");
            return 1;
        }
    }

    private static string Required(string variable) =>
        Environment.GetEnvironmentVariable(variable) is { Length: > 0 } value
            ? value : throw new InvalidOperationException("Rehearsal configuration unavailable.");

    private static async Task SeedAsync(NpgsqlDataSource db,
        AesGcmEnvelopeEncryption crypto)
    {
        var now = DateTimeOffset.UtcNow;
        var grant = ConsentGrant.Issue(Tenant, Owner, Source, ConsentGrant.MemoryCapture,
            "privacy-v1", now, now.AddDays(2));
        await new PostgresConsentRepository(db).GrantAsync(grant, CancellationToken.None);

        var memory = MemoryEntry.Capture(Tenant, Owner, Source, External, "text/plain",
            Raw, Normalized, Summary, ["recovery"], now,
            Enumerable.Repeat(0.125f, 768).ToArray(), Model);
        var result = await new PostgresMemoryRepository(db, crypto).UpsertAsync(
            memory, CancellationToken.None);
        if (result.Id == Guid.Empty)
            throw new InvalidOperationException("Synthetic fixture was not persisted.");
        await VerifyAsync(db, crypto);
    }

    private sealed record Saved(
        Guid Id, string RawRedacted, string JsonRedacted, string SummaryRedacted,
        string[] TagsRedacted, byte[] Envelope, DateTimeOffset Received,
        DateTimeOffset Expires, bool HasEmbedding, string? EmbeddingModel);

    private static async Task<Saved?> FetchAsync(NpgsqlDataSource db)
    {
        await using var actor = await PostgresActorScope.OpenAsync(
            db, Tenant, Owner, CancellationToken.None);
        await using var cmd = new NpgsqlCommand("""
            SELECT id, raw_content, normalized_json::text, summary, tags,
                   sealed_payload, received_at, expires_at,
                   embedding IS NOT NULL, embedding_model
            FROM public.memory_entries
            WHERE tenant_id = @tenant AND owner_sub = @owner
              AND source = @source AND external_id = @external
            """, actor.Connection, actor.Transaction);
        cmd.Parameters.AddWithValue("tenant", Tenant);
        cmd.Parameters.AddWithValue("owner", Owner);
        cmd.Parameters.AddWithValue("source", Source);
        cmd.Parameters.AddWithValue("external", External);
        await using var reader = await cmd.ExecuteReaderAsync();
        if (!await reader.ReadAsync()) return null;
        var entry = new Saved(
            reader.GetGuid(0), reader.GetString(1), reader.GetString(2),
            reader.GetString(3), reader.GetFieldValue<string[]>(4),
            reader.GetFieldValue<byte[]>(5),
            reader.GetFieldValue<DateTimeOffset>(6),
            reader.GetFieldValue<DateTimeOffset>(7), reader.GetBoolean(8),
            reader.IsDBNull(9) ? null : reader.GetString(9));
        if (await reader.ReadAsync())
            throw new InvalidOperationException("Duplicate synthetic fixture.");
        return entry;
    }

    private static async Task VerifyAsync(NpgsqlDataSource db,
        AesGcmEnvelopeEncryption crypto)
    {
        // In a recovered cluster, the runtime login must still have no
        // unscoped visibility into either personal table. This catches a
        // restore that dropped RLS policies/grants despite keeping FORCE RLS.
        await using (var unscoped = db.CreateCommand("""
            SELECT (SELECT count(*) FROM public.memory_entries)::bigint,
                   (SELECT count(*) FROM public.consent_grants)::bigint
            """))
        await using (var reader = await unscoped.ExecuteReaderAsync())
        {
            if (!await reader.ReadAsync() ||
                reader.GetInt64(0) != 0 || reader.GetInt64(1) != 0)
                throw new InvalidOperationException("Unscoped restored RLS was not enforced.");
        }

        var saved = await FetchAsync(db)
            ?? throw new InvalidOperationException("Restored fixture missing.");
        if (saved.Id == Guid.Empty || saved.RawRedacted != "[sealed]" ||
            saved.JsonRedacted.Trim() != "{}" ||
            saved.SummaryRedacted != "[sealed]" ||
            saved.TagsRedacted.Length != 0 ||
            saved.Envelope.Length < 96 ||
            saved.Expires != saved.Received.AddDays(30) ||
            !saved.HasEmbedding || saved.EmbeddingModel != Model)
            throw new InvalidOperationException("Restored row integrity mismatch.");

        // Checks that the restored ciphertext remained bound to the original
        // canonical UUID and actor, not just that a backup can be parsed.
        var content = SealedMemoryPayload.Decrypt(
            crypto, saved.Envelope, Tenant, Owner, saved.Id);
        if (content.RawContent != Raw || content.NormalizedJson != Normalized ||
            content.Summary != Summary ||
            content.Tags.Length != 1 || content.Tags[0] != "recovery")
            throw new InvalidOperationException("Restored content integrity mismatch.");
        try
        {
            _ = SealedMemoryPayload.Decrypt(
                crypto, saved.Envelope, Tenant, "another-owner", saved.Id);
            throw new InvalidOperationException("AEAD actor binding was bypassed.");
        }
        catch (CryptographicException) { /* expected */ }

        var repo = new PostgresMemoryRepository(db, crypto);
        var timeline = await repo.RecentAsync(Tenant, Owner, 10, default);
        if (!timeline.Any(x => x.Id == saved.Id && x.Summary == Summary &&
            x.Tags.Contains("recovery")) ||
            (await repo.RecentAsync(Tenant, "another-owner", 10, default))
                .Any(x => x.Id == saved.Id))
            throw new InvalidOperationException("Restored timeline/RLS mismatch.");

        await using var output = new MemoryStream();
        await new PostgresPersonalDataService(db, crypto).WriteExportAsync(
            Tenant, Owner, output, default);
        using var json = JsonDocument.Parse(output.ToArray());
        var memories = json.RootElement.GetProperty("memoryEntries");
        if (memories.GetArrayLength() != 1)
            throw new InvalidOperationException("Restored export count mismatch.");
        var item = memories[0];
        if (item.GetProperty("id").GetGuid() != saved.Id ||
            item.GetProperty("storageProtection").GetString() != "sealed-v1" ||
            item.GetProperty("rawContent").GetString() != Raw ||
            item.GetProperty("normalized").GetProperty("text").GetString() != Raw ||
            item.GetProperty("summary").GetString() != Summary ||
            item.GetProperty("embedding").GetArrayLength() != 768 ||
            item.GetProperty("embeddingModel").GetString() != Model)
            throw new InvalidOperationException("Restored export integrity mismatch.");
    }

    private static async Task VerifyMissingKeyAsync(NpgsqlDataSource db,
        AesGcmEnvelopeEncryption wrongCrypto)
    {
        var saved = await FetchAsync(db)
            ?? throw new InvalidOperationException("Encrypted fixture missing.");
        try
        {
            _ = SealedMemoryPayload.Decrypt(
                wrongCrypto, saved.Envelope, Tenant, Owner, saved.Id);
        }
        catch (CryptographicException)
        {
            return; // fail-closed on absent historical KEK
        }
        throw new InvalidOperationException("Old encrypted data opened without its KEK.");
    }


    /// <summary>
    /// Additional unrelated synthetic actor to prove replay does not delete
    /// data from another tenant. Uses normal consent and RLS write paths.
    /// </summary>
    private static async Task SeedPeerAsync(NpgsqlDataSource db,
        AesGcmEnvelopeEncryption crypto)
    {
        var now = DateTimeOffset.UtcNow;
        await new PostgresConsentRepository(db).GrantAsync(
            ConsentGrant.Issue(PeerTenant, PeerOwner, Source,
                ConsentGrant.MemoryCapture, "privacy-v1", now, now.AddDays(2)),
            CancellationToken.None);
        var note = MemoryEntry.Capture(PeerTenant, PeerOwner, Source,
            "synthetic-peer-source-v1", "text/plain",
            "SYNTHETIC_UNRELATED_DATA", "{\"text\":\"SYNTHETIC_UNRELATED_DATA\"}",
            "peer synthetic note", ["separate"], now);
        await new PostgresMemoryRepository(db, crypto).UpsertAsync(note,
            CancellationToken.None);
    }

    // A distinct synthetic actor whose erasure is intentionally committed
    // after the PITR point but NEVER published to the independent witness.
    // This is negative DR evidence only, not a production erasure path.
    private static async Task SeedUnwitnessedAsync(NpgsqlDataSource db,
        AesGcmEnvelopeEncryption crypto)
    {
        var now = DateTimeOffset.UtcNow;
        await new PostgresConsentRepository(db).GrantAsync(
            ConsentGrant.Issue(UnwitnessedTenant, UnwitnessedOwner, Source,
                ConsentGrant.MemoryCapture, "privacy-v1", now,
                now.AddDays(2)), default);
        var note = MemoryEntry.Capture(UnwitnessedTenant,
            UnwitnessedOwner, Source, "unwitnessed-before-backup-v1",
            "text/plain", "SYNTHETIC_UNWITNESSED_PITR",
            "{\"test\":\"unwitnessed-pitr\"}",
            "synthetic unwitnessed pitr note", ["rollback"], now);
        await new PostgresMemoryRepository(db, crypto).UpsertAsync(
            note, default);
        if ((await new PostgresMemoryRepository(db, crypto)
                .RecentAsync(UnwitnessedTenant, UnwitnessedOwner, 10, default))
                .Count != 1)
            throw new InvalidOperationException("Unwitnessed fixture not seeded.");
    }

    private static async Task EraseUnwitnessedAsync(NpgsqlDataSource db,
        AesGcmEnvelopeEncryption crypto)
    {
        // CI-only correlated transaction: use the SAME synthetic request
        // UUID authenticated via the independent witness intent reader as
        // source outbox ID. This is not a proof of completed deletion after
        // PITR and never authorizes replay from an orphaned intent.
        var privatePath = Required("OMNIA_PREERASE_INTENT_ID_PATH");
        var root = Path.GetFullPath(Required("RUNNER_TEMP"))
            .TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!Path.IsPathFullyQualified(privatePath) ||
            !Path.GetFullPath(privatePath).StartsWith(root,
                StringComparison.Ordinal))
            throw new InvalidDataException("Unsafe synthetic intent ID path.");
        var raw = System.Text.Encoding.UTF8.GetString(
            ReadPrivate(privatePath, expectedMax: 37, exactSize: 37)).Trim();
        if (!Guid.TryParseExact(raw, "D", out var requestId) ||
            requestId == Guid.Empty || raw != requestId.ToString("D"))
            throw new InvalidDataException("Invalid synthetic intent UUID.");

        var readerConnection = Required("OMNIA_TEST_INTENT_READER_DB");
        var target = new NpgsqlConnectionStringBuilder(readerConnection);
        if (target.Host != "127.0.0.1" || target.Port != 15436 ||
            target.Database != "omnia_witness" ||
            target.Username != "omnia_witness_intent_reader")
            throw new InvalidOperationException("Untrusted remote intent role.");
        await using var remote = NpgsqlDataSource.Create(readerConnection);
        var anchorConnection = Required("OMNIA_PREERASE_PITR_ANCHOR_READER_DB");
        var anchor = new NpgsqlConnectionStringBuilder(anchorConnection);
        if (anchor.Host != "127.0.0.1" || anchor.Port != 15438 ||
            anchor.Database != "omnia_pitr_preflight_anchor" ||
            anchor.Username != "omnia_anchor_reader")
            throw new InvalidOperationException("Untrusted independent intent anchor.");
        var keyPath = Required("OMNIA_PREERASE_PITR_HMAC_KEY_PATH");
        if (!Path.IsPathFullyQualified(keyPath) ||
            !Path.GetFullPath(keyPath).StartsWith(root,
                StringComparison.Ordinal))
            throw new InvalidDataException("Unsafe intent signing key path.");
        var signingKey = ReadPrivate(keyPath, expectedMax: 32, exactSize: 32);
        try
        {
            await using var anchorReader = NpgsqlDataSource.Create(anchorConnection);
            var result = await new PostgresPersonalDataService(db, crypto)
                .EraseWithAnchoredIntentAsync(requestId,
                    UnwitnessedTenant, UnwitnessedOwner,
                    remote, anchorReader, signingKey, default);
            if (result.DeletedMemories != 1 || result.DeletedConsents != 1 ||
                (await new PostgresMemoryRepository(db, crypto)
                    .RecentAsync(UnwitnessedTenant, UnwitnessedOwner, 10, default))
                    .Count != 0)
                throw new InvalidOperationException(
                    "Unwitnessed erasure did not atomically commit.");
        }
        finally
        {
            System.Security.Cryptography.CryptographicOperations
                .ZeroMemory(signingKey);
        }

    }

    private static async Task VerifyUnwitnessedResurrectedAsync(
        NpgsqlDataSource db, AesGcmEnvelopeEncryption crypto)
    {
        // The recovered primary must contain the pre-backup ciphertext
        // again, with its original actor-scoped consent. Do NOT erase
        // this actor or authorize traffic in the negative rehearsal.
        var notes = await new PostgresMemoryRepository(db, crypto)
            .RecentAsync(UnwitnessedTenant, UnwitnessedOwner, 10, default);
        if (notes.Count != 1 ||
            notes[0].Summary != "synthetic unwitnessed pitr note")
            throw new InvalidOperationException(
                "Physical PITR did not resurrect the unwitnessed fixture.");
        await using var export = new MemoryStream();
        await new PostgresPersonalDataService(db, crypto).WriteExportAsync(
            UnwitnessedTenant, UnwitnessedOwner, export, default);
        using var document = JsonDocument.Parse(export.ToArray());
        if (document.RootElement.GetProperty("memoryEntries").GetArrayLength() != 1 ||
            document.RootElement.GetProperty("consentGrants").GetArrayLength() != 1)
            throw new InvalidOperationException(
                "Unwitnessed restored actor export/consent mismatch.");
    }

    private static async Task VerifyPeerAsync(NpgsqlDataSource db,
        AesGcmEnvelopeEncryption crypto)
    {
        var repo = new PostgresMemoryRepository(db, crypto);
        var peer = await repo.RecentAsync(PeerTenant, PeerOwner, 20, default);
        if (peer.Count != 1 || peer[0].Summary != "peer synthetic note")
            throw new InvalidOperationException("Unrelated actor was affected by recovery.");
        var other = await repo.RecentAsync(Tenant, Owner, 20, default);
        if (other.Count != 0)
            throw new InvalidOperationException("Recovered erased actor is still visible.");
        await using var scope = await PostgresActorScope.OpenAsync(
            db, PeerTenant, PeerOwner, default);
        await using var query = new NpgsqlCommand("""
            SELECT
                (SELECT count(*) FROM public.memory_entries
                    WHERE tenant_id = @tenant AND owner_sub = @owner),
                (SELECT count(*) FROM public.consent_grants
                    WHERE tenant_id = @tenant AND owner_sub = @owner)
            """, scope.Connection, scope.Transaction);
        query.Parameters.AddWithValue("tenant", PeerTenant);
        query.Parameters.AddWithValue("owner", PeerOwner);
        await using var rows = await query.ExecuteReaderAsync();
        if (!await rows.ReadAsync() || rows.GetInt64(0) != 1 ||
            rows.GetInt64(1) != 1)
            throw new InvalidOperationException("Peer synthetic data was lost.");
    }

    private static string ReceiptEnv(string name)
    {
        var path = Required(name);
        if (!Path.IsPathFullyQualified(path))
            throw new InvalidDataException("Private synthetic receipt file required.");
        return path;
    }

    // The only supported receipt is the known synthetic actor and recovery
    // target. The test harness has NO ability to erase arbitrary tenants.
    private static byte[] ReadPrivate(string path, int expectedMax,
        int? exactSize = null)
    {
        var file = new FileInfo(path);
        if (!file.Exists || file.Length is <= 0 or > 4096 ||
            file.Length > expectedMax ||
            (exactSize.HasValue && file.Length != exactSize.Value) ||
            file.Attributes.HasFlag(FileAttributes.ReparsePoint))
            throw new InvalidDataException("Invalid private recovery receipt.");
        if (!OperatingSystem.IsWindows())
        {
            var mode = File.GetUnixFileMode(path);
            var unsafePermissions = UnixFileMode.GroupRead | UnixFileMode.GroupWrite |
                UnixFileMode.GroupExecute | UnixFileMode.OtherRead |
                UnixFileMode.OtherWrite | UnixFileMode.OtherExecute;
            if ((mode & unsafePermissions) != 0)
                throw new InvalidDataException("Recovery receipt is not private.");
        }
        var result = File.ReadAllBytes(path);
        if (result.Length > expectedMax ||
            (exactSize.HasValue && result.Length != exactSize.Value))
            throw new InvalidDataException("Invalid private recovery receipt.");
        return result;
    }

    private static void ValidateReceipt(ReadOnlySpan<byte> bytes)
    {
        using var document = JsonDocument.Parse(bytes.ToArray(),
            new JsonDocumentOptions { MaxDepth = 4, AllowTrailingCommas = false });
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException("Invalid synthetic recovery receipt.");
        string[] expected =
        [
            "schemaVersion", "operation", "restorePoint", "receiptId",
            "tenantId", "ownerSubject"
        ];
        var properties = root.EnumerateObject().Select(p => p.Name).ToArray();
        if (properties.Length != expected.Length ||
            !properties.ToHashSet(StringComparer.Ordinal)
                .SetEquals(expected) ||
            root.GetProperty("schemaVersion").GetInt32() != 1 ||
            root.GetProperty("operation").GetString() != ReceiptOperation ||
            root.GetProperty("restorePoint").GetString() != ReceiptPoint ||
            root.GetProperty("tenantId").GetString() != Tenant ||
            root.GetProperty("ownerSubject").GetString() != Owner ||
            !Guid.TryParseExact(root.GetProperty("receiptId").GetString(),
                "N", out _))
            throw new InvalidDataException("Invalid synthetic recovery receipt.");
        // Reject duplicate JSON properties even if they repeat the correct
        // values: never permit ambiguous authorization input.
        if (properties.Distinct(StringComparer.Ordinal).Count() !=
            expected.Length)
            throw new InvalidDataException("Duplicated receipt property.");
    }

    private static void SignSyntheticReceipt()
    {
        var path = ReceiptEnv("OMNIA_REHEARSAL_RECEIPT_PATH");
        var macPath = ReceiptEnv("OMNIA_REHEARSAL_RECEIPT_MAC_PATH");
        var keyPath = ReceiptEnv("OMNIA_REHEARSAL_RECEIPT_KEY_PATH");
        var raw = ReadPrivate(path, 2048);
        var key = ReadPrivate(keyPath, 32, 32);
        try
        {
            ValidateReceipt(raw);
            var mac = System.Security.Cryptography.HMACSHA256.HashData(key, raw);
            try
            {
                File.WriteAllText(macPath, Convert.ToBase64String(mac));
                if (!OperatingSystem.IsWindows())
                    File.SetUnixFileMode(macPath, UnixFileMode.UserRead |
                        UnixFileMode.UserWrite);
            }
            finally { CryptographicOperations.ZeroMemory(mac); }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
            CryptographicOperations.ZeroMemory(raw);
        }
    }

    private static void VerifyReceipt()
    {
        var path = ReceiptEnv("OMNIA_REHEARSAL_RECEIPT_PATH");
        var keyPath = ReceiptEnv("OMNIA_REHEARSAL_RECEIPT_KEY_PATH");
        var macPath = ReceiptEnv("OMNIA_REHEARSAL_RECEIPT_MAC_PATH");
        var raw = ReadPrivate(path, 2048);
        var key = ReadPrivate(keyPath, 32, 32);
        try
        {
            var signatureBytes = ReadPrivate(macPath, 128);
            var signed = Convert.FromBase64String(
                System.Text.Encoding.ASCII.GetString(signatureBytes));
            var computed = System.Security.Cryptography.HMACSHA256.HashData(key, raw);
            try
            {
                if (signed.Length != 32 ||
                    !CryptographicOperations.FixedTimeEquals(computed, signed))
                    throw new InvalidDataException("Unauthorized recovery receipt.");
                ValidateReceipt(raw);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(computed);
                CryptographicOperations.ZeroMemory(signed);
                CryptographicOperations.ZeroMemory(signatureBytes);
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
            CryptographicOperations.ZeroMemory(raw);
        }
    }

    /// <summary>
    /// Only known CI fixture: this is a REAL service erasure on the
    /// disposable source after the named PITR point, not a raw SQL DELETE.
    /// It creates the transactional source outbox in the same commit.
    /// </summary>
    private static async Task EraseSourceAsync(NpgsqlDataSource db,
        AesGcmEnvelopeEncryption crypto)
    {
        await VerifyAsync(db, crypto);
        var result = await new PostgresPersonalDataService(db, crypto)
            .EraseAsync(Tenant, Owner, default);
        if (result.DeletedMemories != 1 || result.DeletedConsents != 1 ||
            await FetchAsync(db) is not null)
            throw new InvalidOperationException(
                "Synthetic source erasure was not atomic.");
    }

    private static NpgsqlDataSource OpenWitnessConnection(
        string variable, string expectedRole)
    {
        var connection = Required(variable);
        var options = new NpgsqlConnectionStringBuilder(connection);
        if (options.Host != "127.0.0.1" || options.Port != 15436 ||
            options.Database != "omnia_witness" ||
            !string.Equals(options.Username, expectedRole,
                StringComparison.Ordinal))
            throw new InvalidOperationException(
                "Only loopback disposable witness database is supported.");
        return NpgsqlDataSource.Create(connection);
    }

    private static NpgsqlDataSource OpenDispatcher()
    {
        var connection = Required("OMNIA_REHEARSAL_DISPATCH_DB");
        var options = new NpgsqlConnectionStringBuilder(connection);
        if (options.Host != "127.0.0.1" || options.Port != 15432 ||
            options.Database != "omnia_test" ||
            options.Username != "omnia_dispatcher")
            throw new InvalidOperationException(
                "Only disposable source dispatcher is supported.");
        return NpgsqlDataSource.Create(connection);
    }

    private static async Task RequireExactlyOneSyntheticWitnessAsync(
        NpgsqlDataSource witness)
    {
        await PostgresErasureWitnessReader.VerifyReaderAsync(witness);
        var reader = new PostgresErasureWitnessReader(witness);
        long previousSequence = 0;
        var found = 0;
        // The witness may include unrelated synthetic events from previous
        // CI tests. Never interpret an incomplete scan as authorization.
        for (var page = 0; page < 100; page++)
        {
            var batch = await reader.ReadBatchAsync(
                previousSequence, 100, CancellationToken.None);
            if (batch.Count == 0)
            {
                if (found != 1)
                    throw new InvalidOperationException(
                        "Expected synthetic committed erasure witness missing.");
                return;
            }
            foreach (var item in batch)
            {
                if (item.Sequence <= previousSequence ||
                    item.EventId == Guid.Empty ||
                    item.EventType != "erase-actor")
                    throw new InvalidOperationException(
                        "Witness event sequence or schema invalid.");
                previousSequence = item.Sequence;
                if (item.TenantId == Tenant && item.OwnerSubject == Owner)
                    found++;
            }
        }
        throw new InvalidOperationException(
            "Witness scan bound exceeded before reconciliation.");
    }

    private static async Task PublishSourceToWitnessAsync()
    {
        await using var dispatcher = OpenDispatcher();
        await using var writer = OpenWitnessConnection(
            "OMNIA_REHEARSAL_WITNESS_WRITER_DB", "omnia_witness_writer");
        await using var reader = OpenWitnessConnection(
            "OMNIA_REHEARSAL_WITNESS_READER_DB", "omnia_witness_reader");
        await new PostgresErasureOutboxWitnessPublisher(dispatcher, writer)
            .PublishBatchAsync(1000, CancellationToken.None);
        await RequireExactlyOneSyntheticWitnessAsync(reader);
    }

    private static async Task ReconcileFromWitnessAsync(NpgsqlDataSource db,
        AesGcmEnvelopeEncryption crypto)
    {
        // Validate the INDEPENDENT witness and entire bounded sequence
        // BEFORE any mutation on the recovered primary. Fixed actor only:
        // no tenant selector from the remote event is accepted here.
        await using var witness = OpenWitnessConnection(
            "OMNIA_REHEARSAL_WITNESS_READER_DB", "omnia_witness_reader");
        await RequireExactlyOneSyntheticWitnessAsync(witness);
        await new PostgresPersonalDataService(db, crypto).EraseAsync(
            Tenant, Owner, CancellationToken.None);
        await VerifyErasedAsync(db, crypto);
    }

    private static async Task ReconcileSyntheticErasureAsync(NpgsqlDataSource db,
        AesGcmEnvelopeEncryption crypto)
    {
        // Authentication precedes any read or deletion; fail-closed on
        // missing, tampered, non-private or unauthorized receipts.
        VerifyReceipt();
        // Uses the same actor lock / RLS as the real first-party erasure.
        // No arbitrary actor selectors are accepted in this CI utility.
        await new PostgresPersonalDataService(db, crypto).EraseAsync(
            Tenant, Owner, CancellationToken.None);
        await VerifyErasedAsync(db, crypto);
    }

    private static async Task VerifyErasedAsync(NpgsqlDataSource db,
        AesGcmEnvelopeEncryption crypto)
    {
        if (await FetchAsync(db) is not null)
            throw new InvalidOperationException("A deleted actor was resurrected.");
        var timeline = await new PostgresMemoryRepository(db, crypto)
            .RecentAsync(Tenant, Owner, 20, default);
        if (timeline.Count != 0)
            throw new InvalidOperationException("Recovered actor still visible.");
        await using var export = new MemoryStream();
        await new PostgresPersonalDataService(db, crypto).WriteExportAsync(
            Tenant, Owner, export, default);
        using var doc = JsonDocument.Parse(export.ToArray());
        if (doc.RootElement.GetProperty("memoryEntries").GetArrayLength() != 0 ||
            doc.RootElement.GetProperty("consentGrants").GetArrayLength() != 0)
            throw new InvalidOperationException("Recovered deletion export was not empty.");
    }

    private static async Task EraseRestoredAsync(NpgsqlDataSource db,
        AesGcmEnvelopeEncryption crypto)
    {
        await VerifyAsync(db, crypto);
        var result = await new PostgresPersonalDataService(db, crypto).EraseAsync(
            Tenant, Owner, default);
        if (result.DeletedMemories != 1 || result.DeletedConsents != 1 ||
            await FetchAsync(db) is not null)
            throw new InvalidOperationException("Restored primary erasure failed.");
    }
}
