using Npgsql;
using Omnia.Infrastructure;

// Offline, CI-only proof of a separately held witness prefix checkpoint.
// NEVER signs release state, mutates erasures, or calls a public API.
// Do not log identifiers, credentials, key bytes or SQL exception details.
try
{
    if (args.Length != 1 || args[0] is not ("--issue" or "--verify") ||
        Environment.GetEnvironmentVariable("CI") != "true" ||
        Environment.GetEnvironmentVariable("OMNIA_WITNESS_AUDIT_CI_ONLY") != "1")
        throw new InvalidOperationException("Witness audit is CI-only.");

    static string Required(string name) =>
        Environment.GetEnvironmentVariable(name) is { Length: > 0 } value
            ? value : throw new InvalidOperationException("Witness configuration unavailable.");

    var configuration = new NpgsqlConnectionStringBuilder(
        Required("OMNIA_TEST_WITNESS_READER_DB"));
    if (configuration.Host != "127.0.0.1" ||
        configuration.Port != 15436 ||
        configuration.Database != "omnia_witness" ||
        configuration.Username != "omnia_witness_reader")
        throw new InvalidOperationException("Unsafe witness audit connection.");

    await using var witness = NpgsqlDataSource.Create(
        configuration.ConnectionString);
    var checkpointPath = Required("OMNIA_WITNESS_CHECKPOINT_PATH");
    var keyPath = Required("OMNIA_WITNESS_CHECKPOINT_KEY_PATH");
    if (args[0] == "--issue")
    {
        await WitnessCheckpointFileAudit.IssueNewAsync(
            witness, checkpointPath, keyPath, CancellationToken.None);
        Console.WriteLine("WITNESS_CHECKPOINT_ISSUED");
    }
    else
    {
        await WitnessCheckpointFileAudit.VerifyAsync(
            witness, checkpointPath, keyPath, CancellationToken.None);
        Console.WriteLine("WITNESS_CHECKPOINT_VERIFIED");
    }
    return 0;
}
catch
{
    Console.Error.WriteLine("WITNESS_CHECKPOINT_DENIED");
    return 2;
}
