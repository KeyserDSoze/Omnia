using Npgsql;
using Omnia.Infrastructure;

// Offline executable: configuration exclusively in environment/secret mounts.
static string Required(string name) => Environment.GetEnvironmentVariable(name) is { Length: > 0 } value
    ? value : throw new InvalidOperationException("Required backfill configuration is missing.");

try
{
    var dbConnection = Required("OMNIA_BACKFILL_DB");
    var keyRingPath = Required("OMNIA_BACKFILL_KEYRING_PATH");
    var tenant = Required("OMNIA_BACKFILL_TENANT");
    var owner = Required("OMNIA_BACKFILL_SUBJECT");
    var batch = int.TryParse(Environment.GetEnvironmentVariable("OMNIA_BACKFILL_BATCH_SIZE"), out var configured)
        ? configured : 100;
    if (batch is < 1 or > 500)
        throw new ArgumentOutOfRangeException(nameof(batch));

    await using var db = NpgsqlDataSource.Create(dbConnection);
    await PostgresRuntimeGuard.VerifyAsync(db);
    var crypto = new AesGcmEnvelopeEncryption(EnvelopeKeyRing.FromFile(keyRingPath));
    var worker = new PostgresLegacyMemoryBackfill(db, crypto);
    var written = 0;
    for (var i = 0; i < 100; i++)
    {
        var count = await worker.BackfillBatchAsync(tenant, owner, batch, CancellationToken.None);
        written += count;
        if (count == 0) break;
    }
    var remaining = await worker.CountLegacyAsync(tenant, owner, CancellationToken.None);
    Console.WriteLine($"Batches committed; sealed rows: {written}; remaining actor legacy rows: {remaining}.");
    if (remaining != 0) Environment.ExitCode = 2;
}
catch
{
    // Deliberately suppress exception details and all personally scoped inputs.
    Console.Error.WriteLine("Backfill failed; transaction rolled back. Review restricted DB diagnostics.");
    Environment.ExitCode = 1;
}
