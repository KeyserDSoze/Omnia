using Tao.Application;
using Xunit;

namespace Tao.Domain.Tests;

public sealed class AiRoutingTests
{
    [Fact]
    public void SpeechRunsInTheBrowserByDefaultAndBriefingFollowsTheModel()
    {
        var routing = AiRouting.Parse(null, null);
        Assert.Equal(new AiRoutes(AiEngine.Off, AiEngine.Browser, AiEngine.Browser), routing.Routes(briefingConfigured: false));
        Assert.Equal(AiEngine.Server, routing.Routes(briefingConfigured: true).Briefing);
    }

    [Theory]
    [InlineData("off", "browser", AiEngine.Off, AiEngine.Browser)]
    [InlineData(" Browser ", "OFF", AiEngine.Browser, AiEngine.Off)]
    [InlineData("", "", AiEngine.Browser, AiEngine.Browser)]
    public void EachSpeechTaskCanBeTurnedOff(string stt, string tts, AiEngine expectedStt, AiEngine expectedTts)
    {
        var routes = AiRouting.Parse(stt, tts).Routes(briefingConfigured: false);
        Assert.Equal(expectedStt, routes.SpeechToText);
        Assert.Equal(expectedTts, routes.TextToSpeech);
    }

    [Theory]
    [InlineData("server", null)]
    [InlineData(null, "server")]
    [InlineData("whisper", null)]
    [InlineData(null, "cloud")]
    public void UnknownOrNotYetAvailableEnginesFailFast(string? stt, string? tts) =>
        Assert.Throws<InvalidOperationException>(() => AiRouting.Parse(stt, tts));
}
