using System.Text.Json;

namespace Omnia.Infrastructure;

/// <summary>
/// Operator-provided, sensitive, explicitly approved actor manifest. Not a
/// database discovery mechanism and not part of any HTTP request contract.
/// </summary>
public sealed record LegacyBackfillActor(string TenantId, string OwnerSubject);

public static class LegacyBackfillInventory
{
    private const int MaxBytes = 256 * 1024;
    private const int MaxActors = 1_000;
    private static readonly StringComparer Comparer = StringComparer.Ordinal;

    public static IReadOnlyList<LegacyBackfillActor> Load(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path))
            throw Invalid();
        var file = new FileInfo(path);
        if (!file.Exists || file.Length is <= 0 or > MaxBytes ||
            file.Attributes.HasFlag(FileAttributes.ReparsePoint))
            throw Invalid();

        // Actor identifiers are confidential metadata. Only a private,
        // operator-controlled file is accepted: no world/group access.
        if (!OperatingSystem.IsWindows())
        {
            var mode = File.GetUnixFileMode(path);
            var notPrivate = UnixFileMode.GroupRead | UnixFileMode.GroupWrite |
                UnixFileMode.GroupExecute | UnixFileMode.OtherRead |
                UnixFileMode.OtherWrite | UnixFileMode.OtherExecute;
            if ((mode & notPrivate) != 0) throw Invalid();
        }

        var bytes = File.ReadAllBytes(path);
        if (bytes.Length is <= 0 or > MaxBytes) throw Invalid();
        try
        {
            using var document = JsonDocument.Parse(bytes, new JsonDocumentOptions
            {
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow,
                MaxDepth = 8
            });
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object ||
                root.EnumerateObject().Count() != 3 ||
                !HasExactly(root, "schemaVersion", "operatorApproved", "actors") ||
                root.GetProperty("schemaVersion").ValueKind != JsonValueKind.Number ||
                !root.GetProperty("schemaVersion").TryGetInt32(out var version) ||
                version != 1 ||
                root.GetProperty("operatorApproved").ValueKind != JsonValueKind.True)
                throw Invalid();

            var items = root.GetProperty("actors");
            if (items.ValueKind != JsonValueKind.Array ||
                items.GetArrayLength() is < 1 or > MaxActors) throw Invalid();

            var actors = new List<LegacyBackfillActor>(items.GetArrayLength());
            var unique = new HashSet<(string Tenant, string Owner)>();
            foreach (var item in items.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object ||
                    item.EnumerateObject().Count() != 2 ||
                    !HasExactly(item, "tenantId", "ownerSubject")) throw Invalid();
                var tenantField = item.GetProperty("tenantId");
                var ownerField = item.GetProperty("ownerSubject");
                if (tenantField.ValueKind != JsonValueKind.String ||
                    ownerField.ValueKind != JsonValueKind.String) throw Invalid();
                var tenant = tenantField.GetString()!;
                var owner = ownerField.GetString()!;
                if (!Valid(tenant, 128) || !Valid(owner, 255) ||
                    !unique.Add((tenant, owner))) throw Invalid();
                actors.Add(new LegacyBackfillActor(tenant, owner));
            }
            return actors.AsReadOnly();
        }
        catch (JsonException)
        {
            throw Invalid();
        }
        finally
        {
            System.Security.Cryptography.CryptographicOperations.ZeroMemory(bytes);
        }
    }

    private static bool Valid(string value, int max) =>
        value.Length > 0 && value.Length <= max &&
        value == value.Trim() && !value.Any(char.IsControl);

    private static bool HasExactly(JsonElement obj, params string[] names) =>
        obj.EnumerateObject().All(p => names.Contains(p.Name, Comparer)) &&
        names.All(name => obj.TryGetProperty(name, out _));

    private static InvalidDataException Invalid() =>
        new("Invalid or unauthorized legacy backfill inventory.");
}

/// <summary>
/// Bounded re-entrant migration, per authenticated operator-approved actor.
/// A partial result is reported as incomplete, never as fully processed.
/// </summary>
public sealed record LegacyBackfillRunResult(long SealedRows, int IncompleteActors);

public static class LegacyBackfillRunner
{
    public static async Task<LegacyBackfillRunResult> RunAsync(
        PostgresLegacyMemoryBackfill worker, IReadOnlyList<LegacyBackfillActor> actors,
        int batchSize, int maxBatchesPerActor, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(worker);
        ArgumentNullException.ThrowIfNull(actors);
        if (actors.Count is < 1 or > 1_000 ||
            batchSize is < 1 or > 500 ||
            maxBatchesPerActor is < 1 or > 100)
            throw new ArgumentOutOfRangeException(nameof(batchSize),
                "Invalid bounded backfill configuration.");
        var unique = new HashSet<(string, string)>();
        foreach (var actor in actors)
        {
            if (actor is null ||
                string.IsNullOrWhiteSpace(actor.TenantId) ||
                string.IsNullOrWhiteSpace(actor.OwnerSubject) ||
                !unique.Add((actor.TenantId, actor.OwnerSubject)))
                throw new ArgumentException("Invalid or duplicate backfill actor list.");
        }

        long sealedRows = 0;
        var incomplete = 0;
        foreach (var actor in actors)
        {
            for (var i = 0; i < maxBatchesPerActor; i++)
            {
                ct.ThrowIfCancellationRequested();
                var count = await worker.BackfillBatchAsync(
                    actor.TenantId, actor.OwnerSubject, batchSize, ct);
                sealedRows += count;
                if (count == 0) break;
            }
            if (await worker.CountLegacyAsync(actor.TenantId,
                    actor.OwnerSubject, ct) != 0)
                incomplete++;
        }
        return new LegacyBackfillRunResult(sealedRows, incomplete);
    }
}
