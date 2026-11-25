using System.Globalization;

namespace Tao.Application;

/// <summary>
/// A language the daily plan can be written in. The code comes from the client, but the prompt only
/// ever receives <see cref="EnglishName"/> from this allow-list, never text supplied by the user.
/// </summary>
public sealed record AssistantLanguage(string Code, string EnglishName, string Culture)
{
    /// <summary>Gregorian dates in the language's own words, even where the culture defaults to another calendar.</summary>
    public CultureInfo Formatting()
    {
        var culture = (CultureInfo)CultureInfo.GetCultureInfo(Culture).Clone();
        try { culture.DateTimeFormat.Calendar = new GregorianCalendar(); }
        catch (ArgumentOutOfRangeException) { return CultureInfo.InvariantCulture; }
        return culture;
    }
}

public static class AssistantLanguages
{
    public static readonly AssistantLanguage Default = new("it", "Italian", "it-IT");

    public static IReadOnlyList<AssistantLanguage> All { get; } =
    [
        Default,
        new("en", "English", "en-US"), new("zh", "Simplified Chinese", "zh-CN"), new("hi", "Hindi", "hi-IN"),
        new("es", "Spanish", "es-ES"), new("fr", "French", "fr-FR"), new("ar", "Arabic", "ar-EG"),
        new("bn", "Bengali", "bn-BD"), new("pt", "Brazilian Portuguese", "pt-BR"), new("ru", "Russian", "ru-RU"),
        new("ur", "Urdu", "ur-PK"), new("id", "Indonesian", "id-ID"), new("de", "German", "de-DE"),
        new("ja", "Japanese", "ja-JP"), new("ko", "Korean", "ko-KR"), new("tr", "Turkish", "tr-TR"),
        new("vi", "Vietnamese", "vi-VN"), new("fa", "Persian", "fa-IR"), new("pl", "Polish", "pl-PL"),
        new("nl", "Dutch", "nl-NL"), new("th", "Thai", "th-TH")
    ];

    /// <summary>No code means the default; a code outside the allow-list is rejected (null).</summary>
    public static AssistantLanguage? Find(string? code) =>
        string.IsNullOrEmpty(code) ? Default : All.FirstOrDefault(l => l.Code == code);
}
