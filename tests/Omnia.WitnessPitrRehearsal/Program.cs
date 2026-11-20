using System.Security.Cryptography;
using Npgsql;
using Omnia.Infrastructure;

// Synthetic CI-only test harness. Read-only on the recovered witness.
// Never print actor identifiers, signing keys, SQL errors or connection strings.
// Not an API recovery authorization or a general-purpose backup utility.
try
{
    if (args.Length != 1 ||
        args[0] is not ("--bootstrap" or "--verify-source" or
                       "--append-and-anchor" or "--verify-recovered") ||
        Environment.GetEnvironmentVariable("CI") != "true" ||
        Environment.GetEnvironmentVariable("GITHUB_ACTIONS") != "true" ||
        Environment.GetEnvironmentVariable("OMNIA_WITNESS_PITR_CI_ONLY") != "1")
        throw new InvalidOperationException("Unsupported synthetic test mode.");

    static string Required(string name) =>
        Environment.GetEnvironmentVariable(name) is { Length: > 0 } value
            ? value : throw new InvalidOperationException("Missing synthetic configuration.");

    static string RestrictedConnection(string key, int port,
        string database, string username)
    {
        var value = Required(key);
        var parsed = new NpgsqlConnectionStringBuilder(value);
        if (parsed.Host != "127.0.0.1" || parsed.Port != port ||
            parsed.Database != database || parsed.Username != username)
            throw new InvalidOperationException("Unsafe database target.");
        return value;
    }

    static byte[] ReadPrivateKey(string path)
    {
        if (!Path.IsPathFullyQualified(path)) throw new InvalidDataException();
        var root = Path.GetFullPath(Required("RUNNER_TEMP"))
            .TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!Path.GetFullPath(path).StartsWith(root, StringComparison.Ordinal))
            throw new InvalidDataException();
        var file = new FileInfo(path);
        if (!file.Exists || file.Length != 32 ||
            file.Attributes.HasFlag(FileAttributes.ReparsePoint))
            throw new InvalidDataException();
        if (!OperatingSystem.IsWindows())
        {
            var mode = File.GetUnixFileMode(path);
            if ((mode & (UnixFileMode.GroupRead | UnixFileMode.GroupWrite |
                         UnixFileMode.GroupExecute | UnixFileMode.OtherRead |
                         UnixFileMode.OtherWrite | UnixFileMode.OtherExecute)) != 0 ||
                (mode & UnixFileMode.UserRead) == 0)
                throw new InvalidDataException();
        }
        var bytes = File.ReadAllBytes(path);
        if (bytes.Length != 32)
        {
            CryptographicOperations.ZeroMemory(bytes);
            throw new InvalidDataException();
        }
        return bytes;
    }

    var recovered = args[0] == "--verify-recovered";
    var witnessReaderConnection = RestrictedConnection(
        recovered ? "OMNIA_WITNESS_PITR_RECOVERED_READER_DB" :
                    "OMNIA_TEST_WITNESS_READER_DB",
        recovered ? 15439 : 15436,
        "omnia_witness", "omnia_witness_reader");
    var anchorReaderConnection = RestrictedConnection(
        "OMNIA_WITNESS_PITR_ANCHOR_READER_DB",
        15438, "omnia_witness_pitr_anchor", "omnia_anchor_reader");
    var key = ReadPrivateKey(Required("OMNIA_WITNESS_PITR_HMAC_KEY_PATH"));
    try
    {
        await using var witnessReader = NpgsqlDataSource.Create(witnessReaderConnection);
        await using var anchorReader = NpgsqlDataSource.Create(anchorReaderConnection);
        var verifier = new PostgresWitnessAnchorVerifier(witnessReader, anchorReader);
        if (args[0] == "--verify-recovered")
        {
            // The CI script has already confirmed that the recovered PGDATA
            // lacks the post-point erasure, while the source/anchor verify.
            // Only a real mismatch in the signed prefix is expected here.
            try
            {
                await verifier.VerifyAsync(key, CancellationToken.None);
            }
            catch (InvalidDataException)
            {
                Console.WriteLine("WITNESS_PITR_ROLLBACK_REJECTED");
                return 0;
            }
            throw new InvalidOperationException("Witness rollback was accepted.");
        }

        if (args[0] == "--verify-source")
        {
            await verifier.VerifyAsync(key, CancellationToken.None);
            Console.WriteLine("WITNESS_PITR_SOURCE_ANCHOR_VERIFIED");
            return 0;
        }

        var anchorWriterConnection = RestrictedConnection(
            "OMNIA_WITNESS_PITR_ANCHOR_WRITER_DB", 15438,
            "omnia_witness_pitr_anchor", "omnia_anchor_writer");
        await using var anchorWriter = NpgsqlDataSource.Create(anchorWriterConnection);
        var issuer = new PostgresWitnessAnchorIssuer(
            witnessReader, anchorWriter, anchorReader);

        if (args[0] == "--bootstrap")
        {
            await issuer.BootstrapAsync(key, operatorApproved: true, CancellationToken.None);
            await verifier.VerifyAsync(key, CancellationToken.None);
            Console.WriteLine("WITNESS_PITR_ANCHOR_BOOTSTRAPPED");
            return 0;
        }

        // The only mutation accepted by this test CLI is a new SYNTHETIC
        // event written to the LIVE witness after a named physical WAL point.
        var writerConnection = RestrictedConnection(
            "OMNIA_TEST_WITNESS_WRITER_DB", 15436,
            "omnia_witness", "omnia_witness_writer");
        await using var writer = NpgsqlDataSource.Create(writerConnection);
        await new PostgresErasureWitness(writer).AppendAsync(
            new ErasureDispatchEvent(Guid.NewGuid(),
                "omnia-ci-witness-pitr-synthetic",
                "synthetic-witness-pitr-owner",
                "erase-actor", DateTimeOffset.UtcNow),
            CancellationToken.None);
        await issuer.IssueAsync(key, CancellationToken.None);
        await verifier.VerifyAsync(key, CancellationToken.None);
        Console.WriteLine("WITNESS_PITR_POST_POINT_ANCHORED");
        return 0;
    }
    finally
    {
        CryptographicOperations.ZeroMemory(key);
    }
}
catch
{
    Console.Error.WriteLine("WITNESS_PITR_PHASE_DENIED");
    return 2;
}
