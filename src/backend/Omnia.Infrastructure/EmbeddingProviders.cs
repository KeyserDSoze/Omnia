using System.Net.Http.Json;
using System.Text.Json;
using Omnia.Application;

namespace Omnia.Infrastructure;

public sealed class NoEmbeddingProvider : IEmbeddingProvider
{
    public Task<EmbeddingVector?> EmbedAsync(string text, CancellationToken ct) =>
        Task.FromResult<EmbeddingVector?>(null);
}

public sealed class OllamaEmbeddingProvider(HttpClient client, IConfigurationAdapter settings)
    : IEmbeddingProvider
{
    public async Task<EmbeddingVector?> EmbedAsync(string text, CancellationToken ct)
    {
        var model = settings.Model;
        using var response = await client.PostAsJsonAsync("api/embed", new { model, input = text }, ct);
        response.EnsureSuccessStatusCode();
        using var json = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
        var values = json.RootElement.GetProperty("embeddings")[0].EnumerateArray()
            .Select(x => x.GetSingle()).ToArray();
        return new EmbeddingVector(values, model);
    }
}

public sealed class AzureOpenAiEmbeddingProvider(HttpClient client, IConfigurationAdapter settings)
    : IEmbeddingProvider
{
    public async Task<EmbeddingVector?> EmbedAsync(string text, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(settings.ApiKey))
            throw new InvalidOperationException("Embeddings:ApiKey must be provided through a secret store.");
        using var request = new HttpRequestMessage(HttpMethod.Post,
            $"openai/deployments/{Uri.EscapeDataString(settings.Model)}/embeddings?api-version=2024-10-21");
        request.Headers.Add("api-key", settings.ApiKey);
        request.Content = JsonContent.Create(new { input = text, dimensions = 768 });
        using var response = await client.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();
        using var json = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
        var values = json.RootElement.GetProperty("data")[0].GetProperty("embedding")
            .EnumerateArray().Select(x => x.GetSingle()).ToArray();
        return new EmbeddingVector(values, settings.Model);
    }
}

public interface IConfigurationAdapter
{
    string Model { get; }
    string? ApiKey { get; }
}

public sealed record EmbeddingSettings(string Model, string? ApiKey) : IConfigurationAdapter;
