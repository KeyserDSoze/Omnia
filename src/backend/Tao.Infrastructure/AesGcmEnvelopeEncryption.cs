using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using Tao.Application;

namespace Tao.Infrastructure;

/// <summary>
/// Binary envelope v1: 'TAO1' | keyIdLength(1) | ASCII keyId | wrapNonce(12)
/// | wrapTag(16) | wrappedDEK(32) | dataNonce(12) | dataTag(16) | ciphertext.
/// Each payload has a random 256-bit DEK, independent of its KEK version.
/// AEAD associated data is a canonical length-prefixed (tenant, user, purpose,
/// record id) tuple. KEK wrapping authenticates the key ID as well.
/// </summary>
public sealed class AesGcmEnvelopeEncryption(EnvelopeKeyRing keyRing) : IEnvelopeEncryption
{
    private static readonly byte[] Magic = [(byte)'T', (byte)'A', (byte)'O', (byte)'1'];
    private const int NonceLength = 12;
    private const int TagLength = 16;
    private const int DekLength = 32;
    // No streaming support in v1. Large media requires a separate design.
    private const int MaxPayloadLength = 1024 * 1024;

    public byte[] Seal(ReadOnlySpan<byte> plaintext, EncryptionScope scope)
    {
        if (plaintext.Length > MaxPayloadLength)
            throw new ArgumentOutOfRangeException(nameof(plaintext));
        var aad = ContextBytes(scope);
        var id = keyRing.ActiveKeyId;
        var wrappedAad = AeadDomain(aad, 0x02, id);
        var dataAad = AeadDomain(aad, 0x01, null);

        var dek = RandomNumberGenerator.GetBytes(DekLength);
        var wrapNonce = RandomNumberGenerator.GetBytes(NonceLength);
        var wrapTag = new byte[TagLength];
        var wrappedDek = new byte[DekLength];
        var dataNonce = RandomNumberGenerator.GetBytes(NonceLength);
        var dataTag = new byte[TagLength];
        var ciphertext = new byte[plaintext.Length];
        var kek = keyRing.CopyKey(id);
        try
        {
            using (var wrapping = new AesGcm(kek, TagLength))
                wrapping.Encrypt(wrapNonce, dek, wrappedDek, wrapTag, wrappedAad);
            using (var data = new AesGcm(dek, TagLength))
                data.Encrypt(dataNonce, plaintext, ciphertext, dataTag, dataAad);
            return Assemble(id, wrapNonce, wrapTag, wrappedDek,
                dataNonce, dataTag, ciphertext);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(dek);
            CryptographicOperations.ZeroMemory(kek);
        }
    }

    public byte[] Open(ReadOnlySpan<byte> envelope, EncryptionScope scope)
    {
        var parsed = Parse(envelope);
        var aad = ContextBytes(scope);
        var dataAad = AeadDomain(aad, 0x01, null);
        var dek = Unwrap(parsed, aad);
        var plaintext = new byte[parsed.Ciphertext.Length];
        try
        {
            using var aes = new AesGcm(dek, TagLength);
            aes.Decrypt(parsed.DataNonce, parsed.Ciphertext, parsed.DataTag,
                plaintext, dataAad);
            return plaintext;
        }
        catch
        {
            CryptographicOperations.ZeroMemory(plaintext);
            throw;
        }
        finally { CryptographicOperations.ZeroMemory(dek); }
    }

    public byte[] Rewrap(ReadOnlySpan<byte> envelope, EncryptionScope scope)
    {
        var parsed = Parse(envelope);
        var aad = ContextBytes(scope);
        var dek = Unwrap(parsed, aad);
        try
        {
            // Authenticate the existing payload before returning or repackaging it.
            var verified = new byte[parsed.Ciphertext.Length];
            try
            {
                using var aes = new AesGcm(dek, TagLength);
                aes.Decrypt(parsed.DataNonce, parsed.Ciphertext, parsed.DataTag,
                    verified, AeadDomain(aad, 0x01, null));
            }
            finally { CryptographicOperations.ZeroMemory(verified); }

            if (parsed.KeyId == keyRing.ActiveKeyId) return envelope.ToArray();

            var nextId = keyRing.ActiveKeyId;
            var nonce = RandomNumberGenerator.GetBytes(NonceLength);
            var tag = new byte[TagLength];
            var newWrappedDek = new byte[DekLength];
            var kek = keyRing.CopyKey(nextId);
            try
            {
                using var wrap = new AesGcm(kek, TagLength);
                wrap.Encrypt(nonce, dek, newWrappedDek, tag,
                    AeadDomain(aad, 0x02, nextId));
                return Assemble(nextId, nonce, tag, newWrappedDek,
                    parsed.DataNonce, parsed.DataTag, parsed.Ciphertext);
            }
            finally { CryptographicOperations.ZeroMemory(kek); }
        }
        finally { CryptographicOperations.ZeroMemory(dek); }
    }

    private byte[] Unwrap(ParsedEnvelope entry, byte[] aad)
    {
        var kek = keyRing.CopyKey(entry.KeyId);
        var dek = new byte[DekLength];
        try
        {
            using var aes = new AesGcm(kek, TagLength);
            aes.Decrypt(entry.WrapNonce, entry.WrappedDek, entry.WrapTag, dek,
                AeadDomain(aad, 0x02, entry.KeyId));
            return dek;
        }
        catch
        {
            CryptographicOperations.ZeroMemory(dek);
            throw;
        }
        finally { CryptographicOperations.ZeroMemory(kek); }
    }

    private static byte[] ContextBytes(EncryptionScope scope)
    {
        ArgumentNullException.ThrowIfNull(scope);
        string[] values = [scope.TenantId, scope.OwnerSubject, scope.Purpose, scope.RecordId];
        int[] limits = [128, 255, 120, 255];
        var chunks = new byte[values.Length][];
        var length = 0;
        for (var i = 0; i < values.Length; i++)
        {
            if (string.IsNullOrWhiteSpace(values[i]) || values[i].Length > limits[i])
                throw new ArgumentException("Encryption requires a valid scoped identity and record.");
            chunks[i] = Encoding.UTF8.GetBytes(values[i]);
            length += 4 + chunks[i].Length;
        }
        var result = new byte[length];
        var position = 0;
        foreach (var part in chunks)
        {
            BinaryPrimitives.WriteInt32BigEndian(result.AsSpan(position, 4), part.Length);
            position += 4;
            part.CopyTo(result, position);
            position += part.Length;
        }
        return result;
    }

    private static byte[] AeadDomain(byte[] scopeAad, byte purpose, string? id)
    {
        byte[] idBytes = id is null ? [] : Encoding.ASCII.GetBytes(id);
        var aad = new byte[5 + scopeAad.Length + idBytes.Length];
        Magic.CopyTo(aad, 0);
        aad[4] = purpose;
        scopeAad.CopyTo(aad, 5);
        idBytes.CopyTo(aad, 5 + scopeAad.Length);
        return aad;
    }

    private static byte[] Assemble(string id, byte[] wrapNonce, byte[] wrapTag,
        byte[] wrappedDek, byte[] dataNonce, byte[] dataTag, byte[] ciphertext)
    {
        var idBytes = Encoding.ASCII.GetBytes(id);
        var output = new byte[5 + idBytes.Length + NonceLength + TagLength +
            DekLength + NonceLength + TagLength + ciphertext.Length];
        Magic.CopyTo(output, 0);
        output[4] = checked((byte)idBytes.Length);
        var pos = 5;
        foreach (var part in new[] { idBytes, wrapNonce, wrapTag,
            wrappedDek, dataNonce, dataTag, ciphertext })
        {
            part.CopyTo(output, pos);
            pos += part.Length;
        }
        return output;
    }

    private static ParsedEnvelope Parse(ReadOnlySpan<byte> envelope)
    {
        if (envelope.Length < 5 || !envelope[..4].SequenceEqual(Magic))
            throw new CryptographicException("Unsupported or malformed encryption envelope.");
        var idLength = envelope[4];
        if (idLength is < 1 or > 32 || envelope.Length < 5 + idLength +
            NonceLength + TagLength + DekLength + NonceLength + TagLength ||
            envelope.Length > 5 + 32 + NonceLength + TagLength +
                DekLength + NonceLength + TagLength + MaxPayloadLength)
            throw new CryptographicException("Unsupported or malformed encryption envelope.");

        var id = Encoding.ASCII.GetString(envelope.Slice(5, idLength));
        if (!EnvelopeKeyRing.ValidKeyId(id))
            throw new CryptographicException("Unsupported encryption key ID.");
        var position = 5 + idLength;
        return new ParsedEnvelope(id, Take(envelope, ref position, NonceLength),
            Take(envelope, ref position, TagLength),
            Take(envelope, ref position, DekLength),
            Take(envelope, ref position, NonceLength),
            Take(envelope, ref position, TagLength),
            envelope[position..].ToArray());
    }

    private static byte[] Take(ReadOnlySpan<byte> buffer, ref int position, int count)
    {
        var value = buffer.Slice(position, count).ToArray();
        position += count;
        return value;
    }

    private sealed record ParsedEnvelope(string KeyId, byte[] WrapNonce, byte[] WrapTag,
        byte[] WrappedDek, byte[] DataNonce, byte[] DataTag, byte[] Ciphertext);
}
