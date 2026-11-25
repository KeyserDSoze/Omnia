using System.Text.Json;
using Tao.Domain;

namespace Tao.Application;

public sealed record CaptureMemoryCommand(string Source, string ExternalId, string ContentType,
    string Content, DateTimeOffset? OccurredAt);
public sealed record MemorySummary(Guid Id, string Source, string Summary, IReadOnlyList<string> Tags,
    DateTimeOffset OccurredAt, bool HasEmbedding);

/// <summary>
/// A live canonical capture key was reused with different source content.
/// No personal text, source key or tenant identifier may enter this message.
/// </summary>
public sealed class MemoryIdempotencyConflictException : Exception
{
    public MemoryIdempotencyConflictException()
        : base("A live memory capture key already exists with different content.") { }
}

public interface IMemoryRepository
{
    Task<MemoryEntry> UpsertAsync(MemoryEntry entry, CancellationToken cancellationToken);
    Task<IReadOnlyList<MemorySummary>> RecentAsync(string tenantId, string ownerSubject,
        int limit, CancellationToken cancellationToken);
}

public sealed record EmbeddingVector(float[] Values, string Model);
public interface IEmbeddingProvider
{
    Task<EmbeddingVector?> EmbedAsync(string text, CancellationToken cancellationToken);
}

public interface IMemoryCatalog
{
    Task<MemoryEntry> CaptureAsync(string tenantId, string subject,
        CaptureMemoryCommand command, CancellationToken cancellationToken);
    Task<IReadOnlyList<MemorySummary>> RecentAsync(string tenantId, string subject,
        int limit, CancellationToken cancellationToken);
}

public sealed class MemoryCatalog(
    IMemoryRepository repository, IEmbeddingProvider embeddings, IConsentGuard consent) : IMemoryCatalog
{
    public async Task<MemoryEntry> CaptureAsync(string tenantId, string subject,
        CaptureMemoryCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        var kind = command.ContentType?.Trim().ToLowerInvariant()
            ?? throw new ArgumentException("ContentType is required.", nameof(command));
        if (kind is not ("text/plain" or "application/json" or "application/xml" or "text/xml"))
            throw new ArgumentException("Unsupported content type.", nameof(command));
        if (string.IsNullOrWhiteSpace(command.Content))
            throw new ArgumentException("Content is required.", nameof(command));

        // Fail invalid JSON before invoking any external embedding provider.
        if (kind == "application/json") using (JsonDocument.Parse(command.Content)) { }
        var plain = command.Content.Trim();
        var excerpt = plain.Length > 180 ? plain[..180] + "…" : plain;
        var tags = Classify(plain);
        var normalized = JsonSerializer.Serialize(new
        {
            schemaVersion = 1, source = command.Source, contentType = kind,
            text = plain, tags, occurredAt = command.OccurredAt
        });
        // Validate all domain fields BEFORE transmitting any personal text to a model.
        var entry = MemoryEntry.Capture(tenantId, subject, command.Source, command.ExternalId,
            kind, command.Content, normalized, excerpt, tags,
            command.OccurredAt ?? DateTimeOffset.UtcNow);

        // Consent is checked before any optional network AI call and again under a
        // PostgreSQL row lock in UpsertAsync, so revocation prevents later writes.
        await consent.EnsureActiveAsync(entry.TenantId, entry.OwnerSubject,
            entry.Source, ConsentGrant.MemoryCapture, cancellationToken);

        var vector = await embeddings.EmbedAsync(excerpt, cancellationToken);
        if (vector is not null)
            entry = entry.WithEmbedding(vector.Values, vector.Model);

        return await repository.UpsertAsync(entry, cancellationToken);
    }

    public Task<IReadOnlyList<MemorySummary>> RecentAsync(string tenantId, string subject,
        int limit, CancellationToken cancellationToken) =>
        repository.RecentAsync(tenantId, subject, Math.Clamp(limit, 1, 100), cancellationToken);

    public static string[] Classify(string text)
    {
        var lower = text.ToLowerInvariant();
        var labels = new List<string>();
        if (new[] { "riunione", "meeting", "cliente", "progetto", "work", "lavoro" }.Any(lower.Contains))
            labels.Add("lavoro");
        if (new[] { "amico", "amicizia", "friend", "famiglia", "family" }.Any(lower.Contains))
            labels.Add("relazioni");
        if (new[] { "notizia", "news", "attualità" }.Any(lower.Contains))
            labels.Add("attualita");
        if (new[] { "salute", "palestra", "health" }.Any(lower.Contains))
            labels.Add("benessere");
        return labels.Count == 0 ? ["personale"] : labels.ToArray();
    }
}
