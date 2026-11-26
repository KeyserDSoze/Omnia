using System.Security.Cryptography;
using System.Text;
using Tao.Application;
using Xunit;

namespace Tao.Domain.Tests;

public sealed class WhatsAppWebhookTests
{
    private const string Secret = "app-secret";

    private static string Sign(string body, string secret = Secret) =>
        "sha256=" + Convert.ToHexString(HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), Encoding.UTF8.GetBytes(body))).ToLowerInvariant();

    [Fact]
    public void AValidSignatureIsAccepted()
    {
        const string body = "{\"object\":\"whatsapp_business_account\"}";
        Assert.True(WhatsAppWebhook.SignatureIsValid(Encoding.UTF8.GetBytes(body), Sign(body), Secret));
        Assert.True(WhatsAppWebhook.SignatureIsValid(Encoding.UTF8.GetBytes(body), Sign(body).ToUpperInvariant().Replace("SHA256=", "sha256="), Secret));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("sha256=")]
    [InlineData("sha256=zz")]
    [InlineData("sha1=abcd")]
    [InlineData("abcd")]
    public void AMissingOrMalformedSignatureIsRefused(string? header) =>
        Assert.False(WhatsAppWebhook.SignatureIsValid("{}"u8, header, Secret));

    [Fact]
    public void ABodyChangedAfterSigningOrSignedWithAnotherSecretIsRefused()
    {
        var signature = Sign("{\"a\":1}");
        Assert.False(WhatsAppWebhook.SignatureIsValid("{\"a\":2}"u8, signature, Secret));
        Assert.False(WhatsAppWebhook.SignatureIsValid("{\"a\":1}"u8, Sign("{\"a\":1}", "other"), Secret));
    }

    [Fact]
    public void WithoutAConfiguredSecretNothingIsAccepted() =>
        Assert.False(WhatsAppWebhook.SignatureIsValid("{}"u8, Sign("{}", ""), ""));

    [Fact]
    public void TheSubscriptionChallengeIsEchoedOnlyForTheRightToken()
    {
        Assert.Equal("1158201444", WhatsAppWebhook.Challenge("subscribe", "verify-me", "1158201444", "verify-me"));
        Assert.Null(WhatsAppWebhook.Challenge("subscribe", "wrong", "1158201444", "verify-me"));
        Assert.Null(WhatsAppWebhook.Challenge("unsubscribe", "verify-me", "1158201444", "verify-me"));
        Assert.Null(WhatsAppWebhook.Challenge("subscribe", "verify-me", null, "verify-me"));
        Assert.Null(WhatsAppWebhook.Challenge("subscribe", null, "1158201444", "verify-me"));
        Assert.Null(WhatsAppWebhook.Challenge("subscribe", "", "1158201444", ""));
    }

    [Fact]
    public void TextMessagesAreExtractedAndStatusUpdatesAreIgnored()
    {
        const string payload = """
            {"object":"whatsapp_business_account","entry":[{"id":"1","changes":[
              {"field":"messages","value":{"messaging_product":"whatsapp",
                "contacts":[{"profile":{"name":"Alice"},"wa_id":"393331112222"}],
                "messages":[
                  {"from":"393331112222","id":"wamid.A","timestamp":"1732600000","type":"text","text":{"body":"  cosa devo fare oggi?  "}},
                  {"from":"393331112222","id":"wamid.B","timestamp":"1732600001","type":"image","image":{"id":"m1"}},
                  {"from":"393331112222","id":"wamid.C","timestamp":"1732600002","type":"text","text":{"body":"   "}}
                ]}},
              {"field":"messages","value":{"statuses":[{"id":"wamid.X","status":"delivered","recipient_id":"393331112222"}]}}
            ]}]}
            """;

        var message = Assert.Single(WhatsAppWebhook.Messages(Encoding.UTF8.GetBytes(payload)));
        Assert.Equal(("393331112222", "wamid.A", "cosa devo fare oggi?", DateTimeOffset.FromUnixTimeSeconds(1732600000)),
            (message.From, message.MessageId, message.Text, message.At));
    }

    [Theory]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("[]")]
    [InlineData("{\"entry\":\"x\"}")]
    [InlineData("{\"entry\":[{\"changes\":[{\"value\":{\"messages\":\"x\"}}]}]}")]
    [InlineData("{\"entry\":[{\"changes\":[{\"value\":{\"messages\":[{\"from\":\"abc\",\"id\":\"1\",\"type\":\"text\",\"text\":{\"body\":\"hi\"}}]}}]}]}")]
    [InlineData("{\"entry\":[{\"changes\":[{\"value\":{\"messages\":[{\"from\":\"393331112222\",\"type\":\"text\",\"text\":{\"body\":\"hi\"}}]}}]}]}")]
    public void MalformedPayloadsYieldNoMessages(string payload) =>
        Assert.Empty(WhatsAppWebhook.Messages(Encoding.UTF8.GetBytes(payload)));

    [Fact]
    public void LongTextIsCutAtTheLimit()
    {
        var payload = "{\"entry\":[{\"changes\":[{\"value\":{\"messages\":[{\"from\":\"393331112222\",\"id\":\"w1\",\"timestamp\":\"1\",\"type\":\"text\",\"text\":{\"body\":\""
            + new string('a', 10_000) + "\"}}]}}]}]}";
        Assert.Equal(WhatsAppWebhook.MaxTextLength, Assert.Single(WhatsAppWebhook.Messages(Encoding.UTF8.GetBytes(payload))).Text.Length);
    }

    [Fact]
    public void PairingCodesUseAnUnambiguousAlphabetAndAreUnique()
    {
        var codes = Enumerable.Range(0, 2000).Select(_ => PairingCode.Generate()).ToArray();
        Assert.All(codes, code =>
        {
            Assert.Equal(PairingCode.Length, code.Length);
            Assert.All(code, c => Assert.Contains(c, PairingCode.Alphabet));
        });
        Assert.Equal(codes.Length, codes.Distinct().Count());
        Assert.DoesNotContain(PairingCode.Alphabet, c => "0O1I".Contains(c));
    }

    [Theory]
    [InlineData("abcd2345", "ABCD2345")]
    [InlineData("  abcd-2345 ", "ABCD2345")]
    [InlineData("ab cd 23 45", "ABCD2345")]
    public void AnEnteredCodeIsNormalized(string typed, string expected) => Assert.Equal(expected, PairingCode.Normalize(typed));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("ABC")]
    [InlineData("ABCD23456")]
    [InlineData("ABCD234O")]
    [InlineData("ABCD2341")]
    [InlineData("ABCD23!5")]
    public void AnInvalidCodeIsRefused(string? typed) => Assert.Null(PairingCode.Normalize(typed));

    [Fact]
    public void ACodeIsStoredOnlyAsAScopedHash()
    {
        var hash = PairingCode.Hash("ABCD2345", "phone-1");
        Assert.Equal(hash, PairingCode.Hash("ABCD2345", "phone-1"));
        Assert.NotEqual(hash, PairingCode.Hash("ABCD2345", "phone-2"));
        Assert.NotEqual(hash, PairingCode.Hash("ABCD2346", "phone-1"));
        Assert.DoesNotContain("ABCD2345", hash);
        Assert.Equal(64, hash.Length);
    }

    [Fact]
    public void AnUnknownSenderMayHaveAtMostThreeOpenRequestsAndOneHour()
    {
        Assert.Equal((3, TimeSpan.FromHours(1)), (PairingCode.MaxPending, PairingCode.Lifetime));
    }
}
