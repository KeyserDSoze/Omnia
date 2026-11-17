namespace Omnia.Domain;

public sealed class MemoryEntry
{
    public Guid Id { get; }
    public string TenantId { get; }
    public string OwnerSubject { get; }
    public string Source { get; }
    public string ExternalId { get; }
    public string ContentType { get; }
    public string RawContent { get; }
    public string NormalizedJson { get; }
    public string Summary { get; }
    public IReadOnlyList<string> Tags { get; }
    public DateTimeOffset OccurredAt { get; }
    public float[]? Embedding { get; }
    public string? EmbeddingModel { get; }

    private MemoryEntry(Guid id, string tenantId, string ownerSubject, string source,
        string externalId, string contentType, string rawContent, string normalizedJson,
        string summary, IReadOnlyList<string> tags, DateTimeOffset occurredAt,
        float[]? embedding, string? embeddingModel)
    {
        Id = id; TenantId = tenantId; OwnerSubject = ownerSubject; Source = source;
        ExternalId = externalId; ContentType = contentType; RawContent = rawContent;
        NormalizedJson = normalizedJson; Summary = summary; Tags = tags;
        OccurredAt = occurredAt; Embedding = embedding; EmbeddingModel = embeddingModel;
    }

    // Used only after the database has returned the canonical id for an idempotent upsert.
    public MemoryEntry WithPersistedId(Guid id) => new(id, TenantId, OwnerSubject, Source,
        ExternalId, ContentType, RawContent, NormalizedJson, Summary, Tags,
        OccurredAt, Embedding, EmbeddingModel);

    public MemoryEntry WithEmbedding(float[] values, string model)
    {
        if (values is not { Length: 768 } || values.Any(x => !float.IsFinite(x)) ||
            string.IsNullOrWhiteSpace(model))
            throw new ArgumentException("A valid 768-dimensional vector and model are required.", nameof(values));
        return new MemoryEntry(Id, TenantId, OwnerSubject, Source, ExternalId,
            ContentType, RawContent, NormalizedJson, Summary, Tags, OccurredAt,
            values.ToArray(), model.Trim());
    }

    public static MemoryEntry Capture(string tenantId, string ownerSubject, string source,
        string externalId, string contentType, string rawContent, string normalizedJson,
        string summary, IEnumerable<string> tags, DateTimeOffset occurredAt,
        float[]? embedding = null, string? embeddingModel = null)
    {
        static string Required(string? value, int max, string name)
        {
            if (string.IsNullOrWhiteSpace(value) || value.Length > max)
                throw new ArgumentException($"{name} is required and must be at most {max} characters.", name);
            return value.Trim();
        }

        tenantId = Required(tenantId, 128, nameof(tenantId));
        ownerSubject = Required(ownerSubject, 255, nameof(ownerSubject));
        source = Required(source, 120, nameof(source)).ToLowerInvariant();
        externalId = Required(externalId, 255, nameof(externalId));
        contentType = Required(contentType, 120, nameof(contentType));
        rawContent = Required(rawContent, 65536, nameof(rawContent));
        normalizedJson = Required(normalizedJson, 131072, nameof(normalizedJson));
        summary = Required(summary, 2000, nameof(summary));
        var safeTags = tags.Select(t => Required(t, 64, nameof(tags)).ToLowerInvariant())
            .Distinct(StringComparer.Ordinal).Take(20).ToArray();
        if (embedding is { Length: not 768 })
            throw new ArgumentException("Omnia v0.1 requires a 768-dimensional embedding.", nameof(embedding));
        if (embedding is not null && (embedding.Any(x => !float.IsFinite(x)) ||
            string.IsNullOrWhiteSpace(embeddingModel)))
            throw new ArgumentException("Embeddings require finite values and a model.", nameof(embedding));
        return new MemoryEntry(Guid.NewGuid(), tenantId, ownerSubject, source,
            externalId, contentType, rawContent, normalizedJson, summary, safeTags,
            occurredAt.ToUniversalTime(), embedding, embeddingModel);
    }
}
