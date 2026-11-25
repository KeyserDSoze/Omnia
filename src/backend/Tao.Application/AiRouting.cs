namespace Tao.Application;

/// <summary>Where an AI task runs. Speech runs in the user's browser today; server engines plug in here.</summary>
public enum AiEngine { Off, Browser, Server }

/// <summary>The routing table the client follows for each AI task.</summary>
public sealed record AiRoutes(AiEngine Briefing, AiEngine SpeechToText, AiEngine TextToSpeech);

/// <summary>
/// Installation-wide AI routing (Ai:SpeechToText, Ai:TextToSpeech). The briefing always runs on
/// the server model when one is configured. A server speech engine does not exist yet: asking for
/// it fails at startup instead of silently falling back to the browser.
/// </summary>
public sealed record AiRouting(AiEngine SpeechToText, AiEngine TextToSpeech)
{
    public static AiRouting Parse(string? speechToText, string? textToSpeech) =>
        new(Speech(speechToText, "Ai:SpeechToText"), Speech(textToSpeech, "Ai:TextToSpeech"));

    public AiRoutes Routes(bool briefingConfigured) =>
        new(briefingConfigured ? AiEngine.Server : AiEngine.Off, SpeechToText, TextToSpeech);

    private static AiEngine Speech(string? value, string key) => (value ?? "").Trim().ToLowerInvariant() switch
    {
        "" or "browser" => AiEngine.Browser,
        "off" => AiEngine.Off,
        "server" => throw new InvalidOperationException(
            key + "=server is not available yet: no server speech engine is installed (see ADR 0102)."),
        _ => throw new InvalidOperationException(key + " accepts browser or off.")
    };
}
