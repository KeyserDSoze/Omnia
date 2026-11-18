using Npgsql;
using Omnia.Infrastructure;

// Read-only, offline maintenance CLI. This is NOT hosted by the HTTP API.
// Do not print DB credentials, actor IDs or exception details.
try
{
    var connection = Environment.GetEnvironmentVariable("OMNIA_BACKFILL_AUDIT_DB");
    if (string.IsNullOrWhiteSpace(connection))
        throw new InvalidOperationException("Audit configuration missing.");

    await using var db = NpgsqlDataSource.Create(connection);
    var count = await new PostgresLegacyBackfillAudit(db).CountRemainingAsync(
        CancellationToken.None);
    Console.WriteLine($"Remaining legacy memory rows in primary PostgreSQL: {count}.");
    if (count > 0) Environment.ExitCode = 2;
}
catch
{
    Console.Error.WriteLine("Legacy audit failed; consult restricted operator diagnostics.");
    Environment.ExitCode = 1;
}
