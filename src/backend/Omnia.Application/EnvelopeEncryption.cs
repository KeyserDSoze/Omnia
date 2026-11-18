namespace Omnia.Application;

/// <summary>
/// Untrusted caller-supplied identity is never sufficient authorization. Before
/// passing a scope to encryption/storage, the application must obtain tenant
/// and subject from an authenticated JWT (the same trust boundary as RLS).
/// Purpose and record id bind a ciphertext to its precise intended use.
/// </summary>
public sealed record EncryptionScope(
    string TenantId, string OwnerSubject, string Purpose, string RecordId);

/// <summary>
/// Versioned authenticated envelope: per-record random DEK, wrapped with a
/// versioned KEK. No provider-specific types enter the Application layer.
/// This is a codec only; existing PostgreSQL tables are not encrypted by it.
/// </summary>
public interface IEnvelopeEncryption
{
    byte[] Seal(ReadOnlySpan<byte> plaintext, EncryptionScope scope);
    byte[] Open(ReadOnlySpan<byte> envelope, EncryptionScope scope);
    byte[] Rewrap(ReadOnlySpan<byte> envelope, EncryptionScope scope);
}
