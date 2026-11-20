using Npgsql;
using Omnia.Infrastructure;

// Synthetic security NEGATIVE test only. It shows that a live-source
// delivery audit can appear clean after primary PITR has erased BOTH
// the committed outbox event and its unacknowledged witness receipt.
// Neither mode issues "ready", modifies data, or proves production custody.
try
{
    if (args.Length != 1 ||
        args[0] is not ("--live-gap" or "--recovered-gap" or
                        "--record-intent" or "--verify-intent") ||
        Environment.GetEnvironmentVariable("CI") != "true" ||
        Environment.GetEnvironmentVariable("GITHUB_ACTIONS") != "true" ||
        Environment.GetEnvironmentVariable("OMNIA_UNWITNESSED_PITR_CI_ONLY") != "1")
        throw new InvalidOperationException("This negative test is CI-only.");

    static string Restricted(string name, int port, string db, string user)
    {
        var value = Environment.GetEnvironmentVariable(name);
        if (string.IsNullOrWhiteSpace(value))
            throw new InvalidDataException("Missing synthetic SQL identity.");
        var connection = new NpgsqlConnectionStringBuilder(value);
        if (connection.Host != "127.0.0.1" || connection.Port != port ||
            connection.Database != db || connection.Username != user)
            throw new InvalidDataException("Unsupported synthetic SQL identity.");
        return value;
    }

    static Guid ReadPrivateEventId(string variable)
    {
        var filePath = Environment.GetEnvironmentVariable(variable)
            ?? throw new InvalidDataException("Missing synthetic event evidence.");
        var runnerTemp = Environment.GetEnvironmentVariable("RUNNER_TEMP")
            ?? throw new InvalidDataException("Missing CI runner path.");
        var root = Path.GetFullPath(runnerTemp).TrimEnd(
            Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!Path.IsPathFullyQualified(filePath) ||
            !Path.GetFullPath(filePath).StartsWith(root,
                StringComparison.Ordinal))
            throw new InvalidDataException("Unsafe synthetic evidence path.");

        var info = new FileInfo(filePath);
        if (!info.Exists || info.Length is < 36 or > 80 ||
            info.Attributes.HasFlag(FileAttributes.ReparsePoint))
            throw new InvalidDataException("Synthetic evidence unavailable.");
        if (!OperatingSystem.IsWindows())
        {
            var mode = File.GetUnixFileMode(filePath);
            if ((mode & (UnixFileMode.GroupRead | UnixFileMode.GroupWrite |
                         UnixFileMode.GroupExecute | UnixFileMode.OtherRead |
                         UnixFileMode.OtherWrite | UnixFileMode.OtherExecute)) != 0 ||
                (mode & UnixFileMode.UserRead) == 0)
                throw new InvalidDataException("Synthetic evidence is not private.");
        }

        var raw = File.ReadAllText(filePath).Trim();
        if (!Guid.TryParse(raw, out var id) || id == Guid.Empty ||
            raw != id.ToString("D"))
            throw new InvalidDataException("Invalid synthetic event UUID.");
        return id;
    }

    // PRE-COMMIT intent is a request marker recorded in a distinct
    // PostgreSQL PGDATA. It cannot prove the later source DELETE committed.
    const string syntheticTenant = "omnia-ci-unwitnessed-pitr-synthetic";
    const string syntheticOwner = "synthetic-unwitnessed-pitr-owner";
    var intentId = ReadPrivateEventId("OMNIA_PREERASE_INTENT_ID_PATH");
    if (args[0] == "--record-intent")
    {
        await using var writer = NpgsqlDataSource.Create(Restricted(
            "OMNIA_TEST_INTENT_WRITER_DB", 15436,
            "omnia_witness", "omnia_witness_intent_writer"));
        var recorded = await new PostgresPreEraseIntentWriter(writer)
            .RecordAsync(intentId, syntheticTenant, syntheticOwner,
                CancellationToken.None);
        if (recorded < 1)
            throw new InvalidDataException("Pre-erase intent was not acknowledged.");
        Console.WriteLine("PREERASE_INTENT_PERSISTED");
        return 0;
    }

    // All read-only phases MUST find the exact independent intent before
    // consulting any source outbox (live or rolled back).
    await using (var reader = NpgsqlDataSource.Create(Restricted(
        "OMNIA_TEST_INTENT_READER_DB", 15436,
        "omnia_witness", "omnia_witness_intent_reader")))
    {
        var receipt = await new PostgresPreEraseIntentReader(reader)
            .FindAsync(intentId, CancellationToken.None)
            ?? throw new InvalidDataException("Pre-erase intent missing.");
        if (receipt.Sequence <= 0 || receipt.RequestId != intentId ||
            receipt.TenantId != syntheticTenant ||
            receipt.OwnerSubject != syntheticOwner ||
            receipt.IntentType != "erase-actor-intent")
            throw new InvalidDataException("Pre-erase intent scope mismatch.");
    }
    if (args[0] == "--verify-intent")
    {
        Console.WriteLine("PREERASE_INTENT_PRESENT_NOT_COMMITTED");
        return 0;
    }

    var recovered = args[0] == "--recovered-gap";
    var dispatcher = Restricted(
        recovered ? "OMNIA_UNWITNESSED_RECOVERED_DISPATCH_DB"
                  : "OMNIA_TEST_DISPATCH_DB",
        recovered ? 15435 : 15432, "omnia_test", "omnia_dispatcher");
    var witness = Restricted("OMNIA_TEST_WITNESS_READER_DB",
        15436, "omnia_witness", "omnia_witness_reader");
    var id = ReadPrivateEventId("OMNIA_UNWITNESSED_PITR_EVENT_ID_PATH");
    if (id != intentId)
        throw new InvalidDataException(
            "Source commit UUID is not correlated to the remote pre-intent.");
    await using var sourceDb = NpgsqlDataSource.Create(dispatcher);
    await using var witnessDb = NpgsqlDataSource.Create(witness);
    await PostgresErasureDispatchReader.VerifyRoleAsync(sourceDb);
    await PostgresErasureWitnessReader.VerifyReaderAsync(witnessDb);

    var sourceReader = new PostgresErasureDispatchReader(sourceDb);
    var foundInSource = false;
    DateTimeOffset? afterAt = null;
    Guid? afterId = null;
    var scanned = 0;
    while (true)
    {
        var page = await sourceReader.ReadBatchAsync(100,
            afterAt, afterId, CancellationToken.None);
        if (page.Count == 0) break;
        scanned += page.Count;
        if (scanned > PostgresErasureLiveDeliveryAudit.MaxOutboxEvents)
            throw new InvalidDataException("Live source scan exceeds bound.");
        if (page.Any(e => e.Id == id))
        {
            var item = page.Single(e => e.Id == id);
            if (item.TenantId != syntheticTenant ||
                item.OwnerSubject != syntheticOwner ||
                item.EventType != "erase-actor")
                throw new InvalidDataException("Source synthetic scope mismatch.");
            foundInSource = true;
        }
        afterAt = page[^1].OccurredAt;
        afterId = page[^1].Id;
    }

    // This UUID has NOT reached the witness before the source PITR.
    // Do not allow a successful source-only scan to conceal that fact.
    if (await new PostgresErasureWitnessReader(witnessDb).FindAsync(
            id, CancellationToken.None) is not null)
        throw new InvalidDataException("Unexpected remote erasure acknowledgement.");

    var audit = new PostgresErasureLiveDeliveryAudit(sourceDb, witnessDb);
    if (!recovered)
    {
        if (!foundInSource)
            throw new InvalidDataException("Expected unacknowledged source commit missing.");
        try
        {
            await audit.VerifyKnownEventAsync(id, CancellationToken.None);
        }
        catch (InvalidDataException)
        {
            Console.WriteLine("CORRELATED_ERASURE_SOURCE_COMMIT_PENDING");
            Console.WriteLine("UNWITNESSED_SOURCE_COMMIT_PENDING");
            return 0;
        }
        throw new InvalidDataException("Unwitnessed event was marked delivered.");
    }

    if (foundInSource)
        throw new InvalidDataException("PITR did not roll back committed source event.");
    // This positive scan is explicitly NOT proof that the pre-PITR
    // committed erasure never existed.
    if (await audit.VerifyAllAsync(CancellationToken.None) < 1)
        throw new InvalidDataException("Synthetic baseline outbox was unexpectedly empty.");
    try
    {
        await audit.VerifyKnownEventAsync(id, CancellationToken.None);
    }
    catch (InvalidDataException)
    {
        // The independently remembered event identifier proves this
        // particular run's false-green risk, but an unsigned local
        // CI file is NOT a trustworthy external source commit ledger.
        Console.WriteLine("CORRELATED_INTENT_ID_SURVIVED_PRIMARY_PITR");
        Console.WriteLine("PREERASE_INTENT_SURVIVED_PRIMARY_PITR");
        Console.WriteLine("UNWITNESSED_PITR_FALSE_GREEN_SOURCE_ONLY_DENIED");
        return 0;
    }
    throw new InvalidDataException("Historical gap was accepted.");
}
catch
{
    Console.Error.WriteLine("UNWITNESSED_PITR_EVIDENCE_DENIED");
    return 2;
}
