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

    public static async Task<int> Main(string[] args)
    {
        try
        {
            if (args.Length != 1 || args[0] is not
                ("seed" or "verify" or "missing-old-key" or "erase"))
                throw new InvalidOperationException("Invalid restore rehearsal phase.");

            var dbConnection = Required("OMNIA_REHEARSAL_DB");
            var ringPath = Required("OMNIA_REHEARSAL_KEYRING_PATH");
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
