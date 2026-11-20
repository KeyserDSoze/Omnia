using Omnia.Infrastructure;

// Offline READ-ONLY recovery audit, no network/database/HTTP access.
// This is not a signer or a release command. Do not publish PII/keys.
try
{
    if (args.Length != 0)
        throw new InvalidOperationException("Unexpected audit argument.");
    static string Required(string name) =>
        Environment.GetEnvironmentVariable(name) is { Length: > 0 } value
            ? value : throw new InvalidOperationException("Audit configuration unavailable.");

    var verified = RecoveryJournalFileAudit.VerifyFiles(
        Required("OMNIA_RECOVERY_JOURNAL_PATH"),
        Required("OMNIA_RECOVERY_CHECKPOINT_PATH"),
        Required("OMNIA_RECOVERY_JOURNAL_KEY_PATH"),
        Required("OMNIA_RECOVERY_CHECKPOINT_KEY_PATH"));
    Console.WriteLine($"Recovery journal verified against external checkpoint: {verified.Count} entries.");
    return 0;
}
catch
{
    Console.Error.WriteLine("RECOVERY_AUDIT_FAILED: journal/checkpoint was not verified.");
    return 2;
}
