using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Tao.Application;

namespace Tao.Infrastructure.Assistant;

public sealed class LlmOptions
{
    /// <summary>Base URL of an OpenAI-compatible API, e.g. https://api.openai.com/v1,
    /// https://{resource}.openai.azure.com/openai/v1 or http://ollama:11434/v1.</summary>
    public string? Endpoint { get; set; }
    public string? ApiKey { get; set; }
    public string? Model { get; set; }
    /// <summary>"bearer" (OpenAI, Ollama) or "api-key" (Azure OpenAI key authentication).</summary>
    public string AuthHeader { get; set; } = "bearer";
    /// <summary>"max_tokens" (OpenAI-compatible servers) or "max_completion_tokens" (GPT-5 and later, o-series).</summary>
    public string TokenParameter { get; set; } = "max_tokens";
    /// <summary>Reasoning models accept only their default temperature.</summary>
    public bool OmitTemperature { get; set; }

    /// <summary>HTTPS, or HTTP only for loopback and single-label hosts on the private network.</summary>
    public static Uri? ValidatedEndpoint(string? endpoint)
    {
        if (string.IsNullOrWhiteSpace(endpoint)) return null;
        if (!Uri.TryCreate(endpoint.TrimEnd('/') + "/", UriKind.Absolute, out var uri) ||
            !(uri.Scheme == Uri.UriSchemeHttps ||
              (uri.Scheme == Uri.UriSchemeHttp && (uri.IsLoopback || !uri.Host.Contains('.')))) ||
            uri.UserInfo.Length > 0 || uri.Query.Length > 0 || uri.Fragment.Length > 0)
            throw new InvalidOperationException(
                "Llm:Endpoint must be HTTPS (HTTP only for loopback or a private single-label host).");
        return uri;
    }
}

/// <summary>Chat completions over HTTP (no SDK). Model errors and bodies are never surfaced or logged.</summary>
public sealed class OpenAiCompatibleChatModel(HttpClient http, LlmOptions options) : IChatModel
{
    private readonly Uri? _endpoint = LlmOptions.ValidatedEndpoint(options.Endpoint);

    public bool IsConfigured => _endpoint is not null && !string.IsNullOrWhiteSpace(options.Model);
    public string Model => options.Model ?? "";

    public async Task<ChatCompletion> CompleteJsonAsync(IReadOnlyList<ChatMessage> messages, int maxOutputTokens,
        CancellationToken ct)
    {
        if (!IsConfigured) throw new AssistantNotConfiguredException();
        var payload = new Dictionary<string, object?>
        {
            ["model"] = options.Model,
            ["messages"] = messages.Select(m => new { role = m.Role, content = m.Content }).ToArray(),
            [options.TokenParameter is "max_completion_tokens" ? "max_completion_tokens" : "max_tokens"] = maxOutputTokens,
            ["response_format"] = new { type = "json_object" }
        };
        if (!options.OmitTemperature) payload["temperature"] = 0.2;
        var body = JsonSerializer.Serialize(payload);
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(_endpoint!, "chat/completions"))
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json")
        };
        if (!string.IsNullOrWhiteSpace(options.ApiKey))
        {
            if (string.Equals(options.AuthHeader, "api-key", StringComparison.OrdinalIgnoreCase))
                request.Headers.TryAddWithoutValidation("api-key", options.ApiKey);
            else
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", options.ApiKey);
        }
        HttpResponseMessage response;
        try { response = await http.SendAsync(request, ct); }
        catch (HttpRequestException e) { throw new AssistantUnavailableException("model_unreachable", e); }
        using (response)
        {
            if (!response.IsSuccessStatusCode)
                throw new AssistantUnavailableException(response.StatusCode == System.Net.HttpStatusCode.TooManyRequests
                    ? "model_rate_limited" : "model_error");
            try
            {
                using var json = JsonDocument.Parse(await response.Content.ReadAsByteArrayAsync(ct));
                var content = json.RootElement.GetProperty("choices")[0].GetProperty("message").GetProperty("content");
                return new ChatCompletion(
                    content.GetString() ?? throw new AssistantUnavailableException("invalid_model_output"),
                    Tokens(json.RootElement, "prompt_tokens"), Tokens(json.RootElement, "completion_tokens"));
            }
            catch (Exception e) when (e is JsonException or KeyNotFoundException or InvalidOperationException
                or IndexOutOfRangeException)
            {
                throw new AssistantUnavailableException("invalid_model_output", e);
            }
        }
    }

    /// <summary>Usage as reported by the provider; absent or odd values mean "unknown".</summary>
    private static int? Tokens(JsonElement root, string name) =>
        root.TryGetProperty("usage", out var usage) && usage.ValueKind == JsonValueKind.Object &&
        usage.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number &&
        value.TryGetInt32(out var tokens) && tokens >= 0 ? tokens : null;
}
