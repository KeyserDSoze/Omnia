using Tao.Domain;
using Xunit;

namespace Tao.Domain.Tests;

public sealed class WorkItemTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 25, 9, 0, 0, TimeSpan.Zero);
    private static readonly Guid Account = Guid.Parse("6f9619ff-8b86-d011-b42d-00c04fc964ff");

    [Fact]
    public void KeyRoundTripsProviderAccountKindAndExternalId()
    {
        var item = new WorkItem(ConnectorProviders.Microsoft, Account, WorkItemKind.Task,
            "list-1|task:42", "Prepara offerta");
        Assert.Equal("microsoft:6f9619ff8b86d011b42d00c04fc964ff:task:list-1|task:42", item.Key);
        Assert.True(WorkItemKey.TryParse(item.Key, out var provider, out var account,
            out var kind, out var externalId));
        Assert.Equal((ConnectorProviders.Microsoft, Account, WorkItemKind.Task, "list-1|task:42"),
            (provider, account, kind, externalId));
    }

    [Theory]
    [InlineData("")]
    [InlineData("dropbox:6f9619ff8b86d011b42d00c04fc964ff:task:1")]
    [InlineData("google:not-a-guid:task:1")]
    [InlineData("google:6f9619ff8b86d011b42d00c04fc964ff:fax:1")]
    [InlineData("google:6f9619ff8b86d011b42d00c04fc964ff:task:")]
    public void MalformedOrForeignKeysAreRejected(string key) =>
        Assert.False(WorkItemKey.TryParse(key, out _, out _, out _, out _));

    [Fact]
    public void OversizedKeysAreRejected() =>
        Assert.False(WorkItemKey.TryParse(
            "google:6f9619ff8b86d011b42d00c04fc964ff:mail:" + new string('x', 600),
            out _, out _, out _, out _));

    [Fact]
    public void PriorityPutsOverdueTasksFirstAndFinishedEventsLast()
    {
        WorkItem Make(WorkItemKind kind, DateTimeOffset? due = null, DateTimeOffset? start = null,
            DateTimeOffset? end = null, bool important = false, bool unread = false) =>
            new(ConnectorProviders.Google, Account, kind, Guid.NewGuid().ToString("N"), "x",
                StartsAt: start, EndsAt: end, DueAt: due, Important: important, Unread: unread);

        var overdue = Make(WorkItemKind.Task, due: Now.AddDays(-1));
        var dueToday = Make(WorkItemKind.Task, due: Now.AddHours(6));
        var soonMeeting = Make(WorkItemKind.Event, start: Now.AddMinutes(30), end: Now.AddMinutes(90));
        var importantMail = Make(WorkItemKind.Mail, important: true);
        var chat = Make(WorkItemKind.Chat, unread: true);
        var unreadMail = Make(WorkItemKind.Mail, unread: true);
        var laterMeeting = Make(WorkItemKind.Event, start: Now.AddHours(6), end: Now.AddHours(7));
        var readMail = Make(WorkItemKind.Mail);
        var finished = Make(WorkItemKind.Event, start: Now.AddHours(-3), end: Now.AddHours(-2));

        var ordered = new[] { finished, readMail, laterMeeting, unreadMail, chat, importantMail,
                soonMeeting, dueToday, overdue }
            .OrderByDescending(x => WorkItemPriority.Score(x, Now)).ToArray();
        Assert.Equal(new[] { overdue, dueToday, soonMeeting, importantMail, chat, unreadMail,
            laterMeeting, readMail, finished }, ordered);
    }

    [Theory]
    [InlineData("done", TriageDecision.Done)]
    [InlineData("not_done", TriageDecision.NotDone)]
    [InlineData("later", TriageDecision.Later)]
    [InlineData("dismissed", TriageDecision.Dismissed)]
    public void TriageDecisionsUseStableWireNames(string wire, TriageDecision expected)
    {
        Assert.True(TriageDecisions.TryParse(wire, out var parsed));
        Assert.Equal(expected, parsed);
        Assert.Equal(wire, TriageDecisions.ToWire(parsed));
    }

    [Theory]
    [InlineData("DONE")]
    [InlineData("delete")]
    [InlineData(null)]
    public void UnknownTriageDecisionsAreRejected(string? wire) =>
        Assert.False(TriageDecisions.TryParse(wire, out _));
}
