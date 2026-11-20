using System.Security.Cryptography;
using Npgsql;
using Omnia.Infrastructure;

// CI-only third-PGDATA physical WAL PITR negative drill. This never touches
// the live API, authenticates a real user, certifies a source COMMIT, or
// authorizes recovery-ready. Synthetic actor details are fixed and public.
try
{
    if (args.Length != 1 ||
        args[0] is not ("--claim" or "--verify-live" or "--verify-recovered" or
            "--claim-fourth" or "--verify-fourth-live" or "--verify-fourth-recovered") ||
        Environment.GetEnvironmentVariable("CI") != "true" ||
        Environment.GetEnvironmentVariable("GITHUB_ACTIONS") != "true" ||
        Environment.GetEnvironmentVariable("OMNIA_CLAIM_PITR_CI_ONLY") != "1")
        throw new InvalidOperationException("Claim PITR drill is synthetic CI only.");

    const string tenant = "omnia-ci-physical-third-claim-pitr";
    const string actor = "synthetic-third-claim-pitr-owner";
    var root = Environment.GetEnvironmentVariable("RUNNER_TEMP") ??
        throw new InvalidDataException("Missing runner temp.");
    root = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) +
        Path.DirectorySeparatorChar;

    static string Restricted(string variable, string db, int port,
        string username)
    {
        var raw = Environment.GetEnvironmentVariable(variable) ??
            throw new InvalidDataException("Missing restricted CI SQL identity.");
        var options = new NpgsqlConnectionStringBuilder(raw);
        if (options.Host != "127.0.0.1" || options.Port != port ||
            options.Database != db || options.Username != username)
            throw new InvalidDataException("Invalid restricted CI SQL identity.");
        return raw;
    }

    string PathUnderRunner(string variable)
    {
        var path = Environment.GetEnvironmentVariable(variable) ??
            throw new InvalidDataException("Missing synthetic private input.");
        if (!Path.IsPathFullyQualified(path) ||
            !Path.GetFullPath(path).StartsWith(root, StringComparison.Ordinal) ||
            new FileInfo(path).Attributes.HasFlag(FileAttributes.ReparsePoint))
            throw new InvalidDataException("Untrusted synthetic private input.");
        if (!OperatingSystem.IsWindows())
        {
            var mode = File.GetUnixFileMode(path);
            if ((mode & (UnixFileMode.GroupRead | UnixFileMode.GroupWrite |
                         UnixFileMode.GroupExecute | UnixFileMode.OtherRead |
                         UnixFileMode.OtherWrite | UnixFileMode.OtherExecute)) != 0 ||
                (mode & UnixFileMode.UserRead) == 0)
                throw new InvalidDataException("Unprotected synthetic private input.");
        }
        return path;
    }

    var uuidPath = PathUnderRunner("OMNIA_CLAIM_PITR_REQUEST_PATH");
    var rawId = File.ReadAllText(uuidPath).Trim();
    if (!Guid.TryParseExact(rawId, "D", out var requestId) ||
        requestId == Guid.Empty || rawId != requestId.ToString("D"))
        throw new InvalidDataException("Invalid synthetic UUID.");
    var keyPath = PathUnderRunner("OMNIA_CLAIM_PITR_KEY_PATH");
    var key = File.ReadAllBytes(keyPath);
    if (key.Length != 32) throw new InvalidDataException("Synthetic HMAC key size invalid.");
    try
    {
        // ADR 0041: complementary physical PITR of the FOURTH PGDATA.
        // Everything here is a synthetic CI-only admission test. A surviving
        // THIRD attempt can reject a replay when FOURTH loses its claim;
        // neither claim constitutes a primary DELETE commit attestation.
        if (args[0] is "--claim-fourth" or "--verify-fourth-live" or
            "--verify-fourth-recovered")
        {
            const string fourthTenant = "omnia-ci-physical-fourth-claim-pitr";
            const string fourthActor = "synthetic-fourth-claim-pitr-owner";
            var recovered = args[0] == "--verify-fourth-recovered";
            const string fourthDb = "omnia_ci_fourth_claim_pitr";
            await using var thirdLiveReader = NpgsqlDataSource.Create(Restricted(
                "OMNIA_TEST_EXECUTION_CLAIM_READER_DB",
                "omnia_execution_claims", 15438, "omnia_claim_reader"));
            await using var fourthScopedReader = NpgsqlDataSource.Create(Restricted(
                recovered ? "OMNIA_CLAIM_PITR_RECOVERED_FOURTH_READER_DB"
                          : "OMNIA_CLAIM_PITR_FOURTH_READER_DB",
                fourthDb, recovered ? 15442 : 15440, "omnia_claim_reader"));
            var thirdVerification =
                new PostgresPreEraseExecutionClaimReader(thirdLiveReader);
            var fourthVerification =
                new PostgresPreEraseExecutionClaimReader(fourthScopedReader);
            if (args[0] == "--claim-fourth")
            {
                await using var fourthScopedWriter = NpgsqlDataSource.Create(
                    Restricted("OMNIA_CLAIM_PITR_FOURTH_WRITER_DB",
                        fourthDb, 15440, "omnia_claim_writer"));
                await using var thirdLiveWriter = NpgsqlDataSource.Create(
                    Restricted("OMNIA_TEST_EXECUTION_CLAIM_WRITER_DB",
                        "omnia_execution_claims", 15438, "omnia_claim_writer"));
                await new PostgresPreEraseExecutionClaimWriter(fourthScopedWriter)
                    .ClaimOnceAsync(requestId, fourthTenant, fourthActor, key, default);
                await new PostgresPreEraseExecutionClaimWriter(thirdLiveWriter)
                    .ClaimOnceAsync(requestId, fourthTenant, fourthActor, key, default);
                Console.WriteLine("FOURTH_THEN_THIRD_CLAIM_COMMITTED");
                return 0;
            }
            if (!recovered)
            {
                await fourthVerification.VerifyClaimAsync(
                    requestId, fourthTenant, fourthActor, key, default);
                await thirdVerification.VerifyClaimAsync(
                    requestId, fourthTenant, fourthActor, key, default);
                Console.WriteLine("FOURTH_AND_THIRD_LIVE_CLAIM_VERIFIED");
                return 0;
            }

            // Restore to the pre-claim target. The independent third PGDATA
            // still has a scope-authenticated claim. Never touch source data.
            await thirdVerification.VerifyClaimAsync(
                requestId, fourthTenant, fourthActor, key, default);
            if (await fourthVerification.HasClaimAsync(requestId, default))
                throw new InvalidDataException(
                    "Physical fourth PITR did not remove the old attempt.");
            await using var fourthRestoredWriter = NpgsqlDataSource.Create(
                Restricted("OMNIA_CLAIM_PITR_RECOVERED_FOURTH_WRITER_DB",
                    fourthDb, 15442, "omnia_claim_writer"));
            await using var thirdLiveReplayWriter = NpgsqlDataSource.Create(
                Restricted("OMNIA_TEST_EXECUTION_CLAIM_WRITER_DB",
                    "omnia_execution_claims", 15438, "omnia_claim_writer"));

            // Fourth was rolled back, so it accepts a duplicated UUID;
            // THIRD must fail with UNIQUE 23505 before source DELETE.
            await new PostgresPreEraseExecutionClaimWriter(fourthRestoredWriter)
                .ClaimOnceAsync(requestId, fourthTenant, fourthActor, key, default);
            try
            {
                await new PostgresPreEraseExecutionClaimWriter(thirdLiveReplayWriter)
                    .ClaimOnceAsync(requestId, fourthTenant, fourthActor, key, default);
            }
            catch (PostgresException ex) when (ex.SqlState == "23505")
            {
                await fourthVerification.VerifyClaimAsync(
                    requestId, fourthTenant, fourthActor, key, default);
                await thirdVerification.VerifyClaimAsync(
                    requestId, fourthTenant, fourthActor, key, default);
                Console.WriteLine("PHYSICAL_FOURTH_PITR_REPLAY_DENIED_BY_THIRD");
                return 0;
            }
            throw new InvalidDataException(
                "Surviving third cluster incorrectly admitted replay.");
        }

        // Fourth has a distinct PGDATA and its own restricted credentials.
        await using var fourthReader = NpgsqlDataSource.Create(Restricted(
            "OMNIA_TEST_FOURTH_CLAIM_READER_DB", "omnia_fourth_claims", 15440,
            "omnia_claim_reader"));
        await using var fourthWriter = NpgsqlDataSource.Create(Restricted(
            "OMNIA_TEST_FOURTH_CLAIM_WRITER_DB", "omnia_fourth_claims", 15440,
            "omnia_claim_writer"));
        if (args[0] == "--claim")
        {
            await using var thirdWriter = NpgsqlDataSource.Create(Restricted(
                "OMNIA_CLAIM_PITR_THIRD_WRITER_DB", "omnia_ci_claim_pitr",
                15438, "omnia_claim_writer"));
            await new PostgresPreEraseExecutionClaimWriter(fourthWriter)
                .ClaimOnceAsync(requestId, tenant, actor, key, default);
            await new PostgresPreEraseExecutionClaimWriter(thirdWriter)
                .ClaimOnceAsync(requestId, tenant, actor, key, default);
            Console.WriteLine("FOURTH_ATTEMPT_DURABLY_CLAIMED_BEFORE_THIRD");
            return 0;
        }

        await new PostgresPreEraseExecutionClaimReader(fourthReader)
            .VerifyClaimAsync(requestId, tenant, actor, key, default);
        await using var thirdReader = NpgsqlDataSource.Create(
            args[0] == "--verify-recovered"
                ? Restricted("OMNIA_CLAIM_PITR_RECOVERED_THIRD_READER_DB",
                    "omnia_ci_claim_pitr", 15441, "omnia_claim_reader")
                : Restricted("OMNIA_CLAIM_PITR_THIRD_READER_DB",
                    "omnia_ci_claim_pitr", 15438, "omnia_claim_reader"));
        var third = new PostgresPreEraseExecutionClaimReader(thirdReader);
        if (args[0] == "--verify-live")
        {
            await third.VerifyClaimAsync(requestId, tenant, actor, key, default);
            Console.WriteLine("LIVE_THIRD_AND_FOURTH_ATTEMPT_VERIFIED");
            return 0;
        }

        // The restored THIRD PGDATA has LOST the claim. The fourth has not.
        if (await third.HasClaimAsync(requestId, default))
            throw new InvalidDataException("Physical third PITR did not erase claim.");
        // Prove the only remaining admission path refuses replay BEFORE a
        // new third claim or source DELETE can be attempted.
        try
        {
            await new PostgresPreEraseExecutionClaimWriter(fourthWriter)
                .ClaimOnceAsync(requestId, tenant, actor, key, default);
        }
        catch (PostgresException ex) when (ex.SqlState == "23505")
        {
            Console.WriteLine("PHYSICAL_THIRD_PITR_REPLAY_DENIED_BY_FOURTH");
            return 0;
        }
        throw new InvalidDataException("Fourth fence incorrectly permitted replay.");
    }
    finally
    {
        CryptographicOperations.ZeroMemory(key);
    }
}
catch
{
    Console.Error.WriteLine("CLAIM_PITR_REHEARSAL_DENIED");
    return 2;
}
