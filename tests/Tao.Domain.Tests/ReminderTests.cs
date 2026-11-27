using Tao.Domain;
using Xunit;

namespace Tao.Domain.Tests;

public sealed class ReminderTests
{
    private static readonly TimeZoneInfo Rome = TimeZoneInfo.FindSystemTimeZoneById("Europe/Rome");
    private static DateTimeOffset Utc(int y, int m, int d, int h = 0, int min = 0) => new(y, m, d, h, min, 0, TimeSpan.Zero);

    [Fact]
    public void ADailyReminderIsTheNextLocalOccurrenceAndNeverTheInstantItself()
    {
        var at = new TimeOnly(9, 0);
        // 07:30 UTC = 08:30 in Rome (CET): today at 09:00 local is 08:00 UTC.
        Assert.Equal(Utc(2026, 11, 27, 8), ReminderRules.Next(ReminderRepeat.Daily, at, null, Rome, Utc(2026, 11, 27, 7, 30)));
        // Exactly at the time it moves to the next day.
        Assert.Equal(Utc(2026, 11, 28, 8), ReminderRules.Next(ReminderRepeat.Daily, at, null, Rome, Utc(2026, 11, 27, 8)));
    }

    [Fact]
    public void WeekdaysSkipTheWeekend()
    {
        var at = new TimeOnly(9, 0);
        // Friday 2026-11-27 after 09:00 local: the next one is Monday the 30th.
        Assert.Equal(Utc(2026, 11, 30, 8), ReminderRules.Next(ReminderRepeat.Weekdays, at, null, Rome, Utc(2026, 11, 27, 12)));
    }

    [Fact]
    public void AOneOffReminderRunsOnceAndThenIsOver()
    {
        var date = new DateOnly(2026, 12, 1);
        Assert.Equal(Utc(2026, 12, 1, 14, 30), ReminderRules.Next(ReminderRepeat.Once, new TimeOnly(15, 30), date, Rome, Utc(2026, 11, 27)));
        Assert.Null(ReminderRules.Next(ReminderRepeat.Once, new TimeOnly(15, 30), date, Rome, Utc(2026, 12, 1, 14, 30)));
        Assert.Null(ReminderRules.Next(ReminderRepeat.Once, new TimeOnly(15, 30), null, Rome, Utc(2026, 11, 27)));
    }

    [Fact]
    public void LocalTimeFollowsDaylightSavingTime()
    {
        var at = new TimeOnly(9, 0);
        // Before the change (CEST, +2): 09:00 local is 07:00 UTC; after it (CET, +1) it is 08:00 UTC.
        Assert.Equal(Utc(2026, 10, 24, 7), ReminderRules.Next(ReminderRepeat.Daily, at, null, Rome, Utc(2026, 10, 24, 0)));
        Assert.Equal(Utc(2026, 10, 26, 8), ReminderRules.Next(ReminderRepeat.Daily, at, null, Rome, Utc(2026, 10, 25, 12)));
    }

    [Fact]
    public void ATimeThatDoesNotExistMovesAheadAndAnAmbiguousOneUsesTheFirst()
    {
        // 2026-03-29 02:30 does not exist in Rome (02:00 jumps to 03:00): it becomes 03:30 CEST = 01:30 UTC.
        Assert.Equal(Utc(2026, 3, 29, 1, 30),
            ReminderRules.Next(ReminderRepeat.Once, new TimeOnly(2, 30), new DateOnly(2026, 3, 29), Rome, Utc(2026, 3, 28)));
        // 2026-10-25 02:30 happens twice: the first (CEST, +2) is 00:30 UTC.
        Assert.Equal(Utc(2026, 10, 25, 0, 30),
            ReminderRules.Next(ReminderRepeat.Once, new TimeOnly(2, 30), new DateOnly(2026, 10, 25), Rome, Utc(2026, 10, 24)));
    }

    [Fact]
    public void AValidRequestBecomesADraftInTheUsersZone()
    {
        var draft = ReminderRules.Validate("  Call Marco  ", "daily", "09:30", null, "Europe/Rome", Utc(2026, 11, 27));
        Assert.Equal(new ReminderDraft("Call Marco", ReminderRepeat.Daily, new TimeOnly(9, 30), null, "Europe/Rome"), draft);
        var once = ReminderRules.Validate("Dentist", "once", "15:00", "2026-12-03", "Europe/Rome", Utc(2026, 11, 27));
        Assert.Equal(new DateOnly(2026, 12, 3), once.Date);
    }

    [Theory]
    [InlineData("", "daily", "09:00", null, "Europe/Rome", "invalid_text")]
    [InlineData("a\nb", "daily", "09:00", null, "Europe/Rome", "invalid_text")]
    [InlineData("ok", "hourly", "09:00", null, "Europe/Rome", "invalid_repeat")]
    [InlineData("ok", "daily", "9", null, "Europe/Rome", "invalid_time")]
    [InlineData("ok", "daily", "25:00", null, "Europe/Rome", "invalid_time")]
    [InlineData("ok", "daily", "09:00", null, "../../etc", "invalid_time_zone")]
    [InlineData("ok", "daily", "09:00", null, "Mars/Olympus", "invalid_time_zone")]
    [InlineData("ok", "once", "09:00", null, "Europe/Rome", "invalid_date")]
    [InlineData("ok", "once", "09:00", "2026-11-01", "Europe/Rome", "invalid_date")]
    [InlineData("ok", "once", "09:00", "2031-01-01", "Europe/Rome", "invalid_date")]
    public void ABadRequestIsRefusedWithAStableCode(string text, string repeat, string at, string? date, string zone, string code)
    {
        var error = Assert.Throws<ArgumentException>(() =>
            ReminderRules.Validate(text, repeat, at, date, zone, Utc(2026, 11, 27)));
        Assert.Equal(code, error.Message);
    }

    [Fact]
    public void TheTextHasALimit()
    {
        Assert.Throws<ArgumentException>(() => ReminderRules.Validate(new string('x', 201), "daily", "09:00", null, "UTC", Utc(2026, 11, 27)));
        ReminderRules.Validate(new string('x', 200), "daily", "09:00", null, "UTC", Utc(2026, 11, 27));
    }
}

public sealed class PushSubscriptionRulesTests
{
    private static string Key(int length, byte first = 0x04)
    {
        var bytes = new byte[length];
        bytes[0] = first;
        return PushSubscriptionRules.Encode(bytes);
    }

    [Theory]
    [InlineData("https://fcm.googleapis.com/fcm/send/abc")]
    [InlineData("https://updates.push.services.mozilla.com/wpush/v2/abc")]
    [InlineData("https://wns2-par02p.notify.windows.com/w/?token=abc")]
    [InlineData("https://web.push.apple.com/abc")]
    public void TheBrowsersPushServicesAreAccepted(string endpoint) =>
        Assert.Equal(new Uri(endpoint).AbsoluteUri, PushSubscriptionRules.Validate(endpoint, Key(65), Key(16)).Endpoint);

    [Theory]
    [InlineData("http://fcm.googleapis.com/fcm/send/abc")]
    [InlineData("https://evil.example/fcm.googleapis.com")]
    [InlineData("https://fcm.googleapis.com.evil.example/x")]
    [InlineData("https://user:pw@fcm.googleapis.com/x")]
    [InlineData("https://fcm.googleapis.com:8443/x")]
    [InlineData("https://169.254.169.254/latest")]
    [InlineData("https://localhost/x")]
    [InlineData("")]
    [InlineData("not a url")]
    public void AnythingElseIsRefusedSoTheServerCannotBeSentElsewhere(string endpoint) =>
        Assert.Equal("invalid_subscription", Assert.Throws<ArgumentException>(() =>
            PushSubscriptionRules.Validate(endpoint, Key(65), Key(16))).Message);

    [Fact]
    public void TheKeysMustHaveTheShapeOfRfc8291()
    {
        const string endpoint = "https://fcm.googleapis.com/fcm/send/abc";
        Assert.Throws<ArgumentException>(() => PushSubscriptionRules.Validate(endpoint, Key(64), Key(16)));
        Assert.Throws<ArgumentException>(() => PushSubscriptionRules.Validate(endpoint, Key(65, 0x03), Key(16)));
        Assert.Throws<ArgumentException>(() => PushSubscriptionRules.Validate(endpoint, Key(65), Key(15)));
        Assert.Throws<ArgumentException>(() => PushSubscriptionRules.Validate(endpoint, "***", Key(16)));
        Assert.Throws<ArgumentException>(() => PushSubscriptionRules.Validate(endpoint, null, null));
    }

    [Fact]
    public void Base64UrlRoundTrips()
    {
        var bytes = Enumerable.Range(0, 70).Select(i => (byte)(i * 7)).ToArray();
        Assert.Equal(bytes, PushSubscriptionRules.Decode(PushSubscriptionRules.Encode(bytes)));
        Assert.DoesNotContain('=', PushSubscriptionRules.Encode(bytes));
    }
}
