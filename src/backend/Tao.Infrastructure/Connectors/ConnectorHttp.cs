using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Tao.Application;

namespace Tao.Infrastructure.Connectors;

/// <summary>Shared OAuth/REST plumbing. Provider error bodies are never logged or surfaced.</summary>
internal static partial class ConnectorHttp
{
    public const int MaxTitleLength = 200;
    public const int MaxDetailLength = 280;

    public static async Task<OAuthTokens> RequestTokensAsync(HttpClient http, string endpoint,
        IEnumerable<KeyValuePair<string, string>> form, TimeProvider time, IReadOnlyList<string> requestedScopes,
        CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, endpoint)
        {
            Content = new FormUrlEncodedContent(form)
        };
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        using var response = await http.SendAsync(request, ct);
        if (!response.IsSuccessStatusCode)
            throw new ConnectorUnavailableException(response.StatusCode is HttpStatusCode.BadRequest
                or HttpStatusCode.Unauthorized ? "reconnect_required" : "provider_error");
        using var json = JsonDocument.Parse(await response.Content.ReadAsByteArrayAsync(ct));
        var root = json.RootElement;
        var access = String(root, "access_token");
        if (string.IsNullOrEmpty(access)) throw new ConnectorUnavailableException("provider_error");
        var expiresIn = root.TryGetProperty("expires_in", out var e) && e.TryGetInt32(out var seconds)
            ? seconds : 3600;
        var scopes = String(root, "scope") is { Length: > 0 } granted
            ? granted.Split(' ', StringSplitOptions.RemoveEmptyEntries)
            : requestedScopes.ToArray();
        return new OAuthTokens(access, String(root, "refresh_token"),
            time.GetUtcNow().AddSeconds(Math.Clamp(expiresIn, 60, 86_400)), scopes);
    }

    public static async Task<JsonDocument> GetJsonAsync(HttpClient http, string url, string accessToken,
        CancellationToken ct, string? prefer = null)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        if (prefer is not null) request.Headers.TryAddWithoutValidation("Prefer", prefer);
        using var response = await http.SendAsync(request, ct);
        EnsureSuccess(response);
        return JsonDocument.Parse(await response.Content.ReadAsByteArrayAsync(ct));
    }

    public static async Task PatchJsonAsync(HttpClient http, string url, string accessToken, string json,
        CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Patch, url)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        using var response = await http.SendAsync(request, ct);
        EnsureSuccess(response);
    }

    public static void EnsureSuccess(HttpResponseMessage response)
    {
        if (response.IsSuccessStatusCode) return;
        throw new ConnectorUnavailableException(response.StatusCode switch
        {
            HttpStatusCode.Unauthorized => "reconnect_required",
            HttpStatusCode.Forbidden => "permission_missing",
            HttpStatusCode.NotFound => "not_found",
            HttpStatusCode.TooManyRequests => "rate_limited",
            _ => "provider_error"
        });
    }

    /// <summary>A missing optional permission hides one category instead of the whole account.</summary>
    public static async Task<IReadOnlyList<T>> OptionalAsync<T>(Func<Task<IReadOnlyList<T>>> load)
    {
        try { return await load(); }
        catch (ConnectorUnavailableException e) when (e.Code is "permission_missing" or "not_found")
        {
            return [];
        }
    }

    public static string? String(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) &&
        value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    public static JsonElement Child(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value)
            ? value : default;

    public static IEnumerable<JsonElement> Items(JsonDocument document, string name) =>
        document.RootElement.TryGetProperty(name, out var array) && array.ValueKind == JsonValueKind.Array
            ? array.EnumerateArray() : [];

    public static bool Bool(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) &&
        value.ValueKind == JsonValueKind.True;

    public static DateTimeOffset? Utc(string? value) =>
        DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var parsed)
            ? parsed : null;

    public static string Clip(string? text, int max)
    {
        if (string.IsNullOrWhiteSpace(text)) return "";
        var clean = Whitespace().Replace(text, " ").Trim();
        return clean.Length <= max ? clean : clean[..(max - 1)].TrimEnd() + "…";
    }

    public static string? PlainText(string? html, int max)
    {
        if (string.IsNullOrWhiteSpace(html)) return null;
        var text = WebUtility.HtmlDecode(Tags().Replace(html, " "));
        var clipped = Clip(text, max);
        return clipped.Length == 0 ? null : clipped;
    }

    public static string Query(params (string Name, string Value)[] values) =>
        string.Join('&', values.Select(v => Uri.EscapeDataString(v.Name) + "=" + Uri.EscapeDataString(v.Value)));

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();

    [GeneratedRegex("<[^>]*>")]
    private static partial Regex Tags();
}
