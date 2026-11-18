using System.Security.Cryptography;
using System.Text.Json;
using Omnia.Application;
using Omnia.Domain;

namespace Omnia.Infrastructure;

/// <summary>
/// A sealed document contains original raw, normalization, summary and tags.
/// Tenant, subject and canonical database GUID are AEAD associated data.
/// This only protects THESE fields: source/externalId, time, vector, model and
/// other metadata remain visible in the primary PostgreSQL table.
/// </summary>
public static class SealedMemoryPayload
{
    public sealed record Content(string RawContent, string NormalizedJson,
        string Summary, string[] Tags);

    private static EncryptionScope Scope(string tenant, string subject, Guid id) =>
        new(tenant, subject, "memory.payload", id.ToString("N"));

    public static byte[] Encrypt(IEnvelopeEncryption crypto, MemoryEntry memory, Guid canonicalId)
    {
        ArgumentNullException.ThrowIfNull(crypto);
        var encoded = JsonSerializer.SerializeToUtf8Bytes(new Content(
            memory.RawContent, memory.NormalizedJson, memory.Summary,
            memory.Tags.ToArray()));
        try
        {
            return crypto.Seal(encoded, Scope(memory.TenantId, memory.OwnerSubject, canonicalId));
        }
        finally { CryptographicOperations.ZeroMemory(encoded); }
    }

    public static Content Decrypt(IEnvelopeEncryption crypto, byte[] envelope,
        string tenant, string subject, Guid id)
    {
        ArgumentNullException.ThrowIfNull(crypto);
        var bytes = crypto.Open(envelope, Scope(tenant, subject, id));
        try
        {
            Content? content;
            try { content = JsonSerializer.Deserialize<Content>(bytes); }
            catch (JsonException) { throw new CryptographicException("Invalid sealed note format."); }
            if (content is null || content.RawContent is null ||
                content.NormalizedJson is null || content.Summary is null ||
                content.Tags is null || content.Tags.Any(x => x is null))
                throw new CryptographicException("Invalid sealed note content.");
            return content;
        }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }
}
