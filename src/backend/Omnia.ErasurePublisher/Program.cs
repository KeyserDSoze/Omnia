using Npgsql;
using Omnia.Infrastructure;

// An isolated CI-only witness publisher, NEVER an HTTP endpoint or
// recovery-release authority. Do not log credentials, actors or SQL errors.
try
{
    if (args.Length > 1 || (args.Length == 1 && args[0] != "--once") ||
        Environment.GetEnvironmentVariable("CI") != "true" ||
        Environment.GetEnvironmentVariable("OMNIA_ERASURE_PUBLISHER_CI_ONLY") != "1")
        throw new InvalidOperationException("Publisher is limited to disposable CI.");

    static string Required(string name) =>
        Environment.GetEnvironmentVariable(name) is { Length: > 0 } value
            ? value : throw new InvalidOperationException("Missing configuration.");

    static string RestrictedConnection(
        string name, int port, string database, string user)
    {
        var value = Required(name);
        var parsed = new NpgsqlConnectionStringBuilder(value);
        if (parsed.Host != "127.0.0.1" || parsed.Port != port ||
            parsed.Database != database || parsed.Username != user)
            throw new InvalidOperationException("Unsafe publisher connection.");
        return value;
    }

    var primary = RestrictedConnection(
        "OMNIA_TEST_DISPATCH_DB", 15432, "omnia_test", "omnia_dispatcher");
    var witness = RestrictedConnection(
        "OMNIA_TEST_WITNESS_WRITER_DB", 15436,
        "omnia_witness", "omnia_witness_writer");

    await using var dispatchDb = NpgsqlDataSource.Create(primary);
    await using var witnessDb = NpgsqlDataSource.Create(witness);
    await PostgresErasureDispatchReader.VerifyRoleAsync(dispatchDb);
    await PostgresErasureWitness.VerifyWriterAsync(witnessDb);

    var publisher = new PostgresErasureOutboxWitnessPublisher(dispatchDb, witnessDb);
    if (args.Length == 1)
    {
        await publisher.PublishBatchAsync(1000, CancellationToken.None);
        Console.WriteLine("ERASURE_PUBLISHER_CYCLE_CONFIRMED");
        return 0;
    }

    using var cancellation = new CancellationTokenSource();
    Console.CancelKeyPress += (_, eventArgs) =>
    {
        eventArgs.Cancel = true;
        cancellation.Cancel();
    };
    var loop = new ErasureWitnessDeliveryLoop(
        ct => publisher.PublishBatchAsync(1000, ct),
        TimeSpan.FromSeconds(5),
        successful => Console.WriteLine(successful
            ? "ERASURE_PUBLISHER_CYCLE_CONFIRMED"
            : "ERASURE_PUBLISHER_RETRY_PENDING"));
    await loop.RunAsync(cancellation.Token);
    return 0;
}
catch
{
    Console.Error.WriteLine("ERASURE_PUBLISHER_DENIED");
    return 2;
}
