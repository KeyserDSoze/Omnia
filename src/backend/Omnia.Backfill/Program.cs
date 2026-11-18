using Npgsql;
using Omnia.Infrastructure;

// Strictly OFFLINE: config comes from private environment/secret mounts.
// An explicit, operator-approved inventory is distinct from DB discovery.
static string Required(string name) => Environment.GetEnvironmentVariable(name) is { Length: > 0 } value
    ? value : throw new InvalidOperationException("Required backfill configuration is missing.");

try
{
    var dbConnection = Required("OMNIA_BACKFILL_DB");
    var keyRingPath = Required("OMNIA_BACKFILL_KEYRING_PATH");
    var inventoryPath = Environment.GetEnvironmentVariable("OMNIA_BACKFILL_INVENTORY_PATH");
    var singleTenant = Environment.GetEnvironmentVariable("OMNIA_BACKFILL_TENANT");
    var singleSubject = Environment.GetEnvironmentVariable("OMNIA_BACKFILL_SUBJECT");
    var batch = int.TryParse(Environment.GetEnvironmentVariable("OMNIA_BACKFILL_BATCH_SIZE"), out var configured)
        ? configured : 100;
    if (batch is < 1 or > 500)
        throw new ArgumentOutOfRangeException(nameof(batch));

    IReadOnlyList<LegacyBackfillActor> actors;
    if (!string.IsNullOrWhiteSpace(inventoryPath))
    {
        // Never silently combine a bulk inventory with a single-actor selector.
        if (!string.IsNullOrEmpty(singleTenant) || !string.IsNullOrEmpty(singleSubject))
            throw new InvalidOperationException("Ambiguous backfill selectors.");
        actors = LegacyBackfillInventory.Load(inventoryPath);
    }
    else
    {
        actors = [new LegacyBackfillActor(Required("OMNIA_BACKFILL_TENANT"),
            Required("OMNIA_BACKFILL_SUBJECT"))];
    }

    await using var db = NpgsqlDataSource.Create(dbConnection);
    await PostgresBackfillRoleGuard.VerifyAsync(db);
    var crypto = new AesGcmEnvelopeEncryption(EnvelopeKeyRing.FromFile(keyRingPath));
    var worker = new PostgresLegacyMemoryBackfill(db, crypto);
    var result = await LegacyBackfillRunner.RunAsync(worker, actors,
        batchSize: batch, maxBatchesPerActor: 100, CancellationToken.None);
    Console.WriteLine($"Batches committed; sealed rows: {result.SealedRows}; " +
        $"actors with remaining legacy rows: {result.IncompleteActors}.");
    if (result.IncompleteActors > 0) Environment.ExitCode = 2;
}
catch
{
    // No identifiers, key IDs, content, filenames, or database errors in output.
    Console.Error.WriteLine("Backfill failed; current batch rolled back. Review restricted diagnostics.");
    Environment.ExitCode = 1;
}
