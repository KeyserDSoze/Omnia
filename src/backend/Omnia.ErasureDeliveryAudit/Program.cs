using Npgsql;
using Omnia.Infrastructure;

// A read-only LIVE-source delivery preflight. This is CI ONLY, never an API
// recovery-ready issuer or proof of source events lost after primary PITR.
// Do not log personal identifiers, credentials or provider exception details.
try
{
    if (args.Length != 1 || args[0] != "--all" ||
        Environment.GetEnvironmentVariable("CI") != "true" ||
        Environment.GetEnvironmentVariable("GITHUB_ACTIONS") != "true" ||
        Environment.GetEnvironmentVariable("OMNIA_ERASURE_LIVE_AUDIT_CI_ONLY") != "1")
        throw new InvalidOperationException("Live delivery audit is synthetic CI only.");

    static string RestrictedConnection(string key, int port, string database,
        string user)
    {
        var text = Environment.GetEnvironmentVariable(key);
        if (string.IsNullOrWhiteSpace(text))
            throw new InvalidOperationException("Missing audit identity.");
        var connection = new NpgsqlConnectionStringBuilder(text);
        if (connection.Host != "127.0.0.1" || connection.Port != port ||
            connection.Database != database || connection.Username != user)
            throw new InvalidOperationException("Unsafe audit identity.");
        return text;
    }

    await using var source = NpgsqlDataSource.Create(RestrictedConnection(
        "OMNIA_TEST_DISPATCH_DB", 15432, "omnia_test", "omnia_dispatcher"));
    await using var witness = NpgsqlDataSource.Create(RestrictedConnection(
        "OMNIA_TEST_WITNESS_READER_DB", 15436,
        "omnia_witness", "omnia_witness_reader"));
    var count = await new PostgresErasureLiveDeliveryAudit(source, witness)
        .VerifyAllAsync(CancellationToken.None);
    if (count < 1)
        throw new InvalidOperationException("Empty source audit cannot prove delivery.");
    Console.WriteLine("ERASURE_LIVE_DELIVERY_CONFIRMED");
    return 0;
}
catch
{
    Console.Error.WriteLine("ERASURE_LIVE_DELIVERY_DENIED");
    return 2;
}
