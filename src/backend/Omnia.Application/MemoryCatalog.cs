using System.Text.Json;
using Omnia.Domain;

namespace Omnia.Application;

public sealed record CaptureMemoryCommand(string Source, string ExternalId, string ContentType,
    string Content, DateTimeOffset? OccurredAt);
public sealed record MemorySummary(Guid Id, string Source, string Summary, IReadOnlyList<string> Tags,
    DateTimeOffset OccurredAt, bool HasEmbedding);

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

public sealed class MemoryCatalog(IMemoryRepository repository, IEmbeddingProvider embeddings) : IMemoryCatalog
{
    public async Task<MemoryEntry> CaptureAsync(string tenantId, string subject,
        CaptureMemoryCommand command, CancellationToken cancellationToken)
    {
        if (command is null) throw new ArgumentNullException(nameof(command));
        var kind = command.ContentType.Trim().ToLowerInvariant();
        if (kind is not ("text/plain" or "application/json" or "application/xml" or "text/xml"))
            throw new ArgumentException("Unsupported content type.", nameof(command));
        // Invalid JSON is rejected rather than silently assigned a different format.
        if (kind == "application/json") using (JsonDocument.Parse(command.Content)) { }
        var plain = command.Content.Trim();
        var excerpt = plain.Length > 180 ? plain[..180] + "…" : plain;
        var tags = Classify(plain);
        var normalized = JsonSerializer.Serialize(new
        {
            schemaVersion = 1, source = command.Source, contentType = kind,
            text = plain, tags, occurredAt = command.OccurredAt
        });
        // Network-dependent enrichment is optional. Missing embeddings are explicit and searchable
        // only by metadata until an asynchronous retry pipeline is implemented.
        var vector = await embeddings.EmbedAsync(excerpt, cancellationToken);
        var entry = MemoryEntry.Capture(tenantId, subject, command.Source, command.ExternalId,
            kind, command.Content, normalized, excerpt, tags,
            command.OccurredAt ?? DateTimeOffset.UtcNow, vector?.Values, vector?.Model);
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
