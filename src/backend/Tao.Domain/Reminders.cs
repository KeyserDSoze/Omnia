using System.Globalization;

namespace Tao.Domain;

public enum ReminderRepeat { Once, Daily, Weekdays }

/// <summary>A validated reminder as the user asked for it; the time is local to <see cref="TimeZone"/>.</summary>
public sealed record ReminderDraft(string Text, ReminderRepeat Repeat, TimeOnly At, DateOnly? Date, string TimeZone);

/// <summary>Rules and schedule of the reminders that become push notifications (ADR 0113).</summary>
public static class ReminderRules
{
    public const int MaxTextLength = 200;
    public const int MaxPerActor = 50;
    public const int MaxHorizonDays = 366;

    /// <summary>Throws <see cref="ArgumentException"/> with a stable <c>invalid_*</c> code.</summary>
    public static ReminderDraft Validate(string? text, string? repeat, string? at, string? date, string? timeZone,
        DateTimeOffset now)
    {
        var body = text?.Trim() ?? "";
        if (body.Length is 0 or > MaxTextLength || body.Any(char.IsControl)) throw new ArgumentException("invalid_text");
        var kind = repeat switch
        {
            "once" => ReminderRepeat.Once, "daily" => ReminderRepeat.Daily, "weekdays" => ReminderRepeat.Weekdays,
            _ => throw new ArgumentException("invalid_repeat")
        };
        if (!TimeOnly.TryParseExact(at, "HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out var time))
            throw new ArgumentException("invalid_time");
        if (!TryZone(timeZone, out var zone)) throw new ArgumentException("invalid_time_zone");
        DateOnly? day = null;
        if (kind == ReminderRepeat.Once)
        {
            if (!DateOnly.TryParseExact(date, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed))
                throw new ArgumentException("invalid_date");
            day = parsed;
        }
        var draft = new ReminderDraft(body, kind, time, day, zone.Id);
        var first = Next(draft, now);
        if (first is null || first > now.AddDays(MaxHorizonDays)) throw new ArgumentException("invalid_date");
        return draft;
    }

    public static bool TryZone(string? id, out TimeZoneInfo zone)
    {
        zone = TimeZoneInfo.Utc;
        if (string.IsNullOrEmpty(id) || id.Length > 64 ||
            !id.All(c => char.IsAsciiLetterOrDigit(c) || c is '/' or '_' or '-' or '+')) return false;
        return TimeZoneInfo.TryFindSystemTimeZoneById(id, out zone!);
    }

    public static DateTimeOffset? Next(ReminderDraft draft, DateTimeOffset after) =>
        Next(draft.Repeat, draft.At, draft.Date, TryZone(draft.TimeZone, out var zone) ? zone : TimeZoneInfo.Utc, after);

    /// <summary>The first run strictly after <paramref name="after"/>; null when a one-off reminder is over.</summary>
    public static DateTimeOffset? Next(ReminderRepeat repeat, TimeOnly at, DateOnly? date, TimeZoneInfo zone,
        DateTimeOffset after)
    {
        if (repeat == ReminderRepeat.Once)
        {
            if (date is null) return null;
            var once = ToInstant(date.Value, at, zone);
            return once > after ? once : null;
        }
        var day = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(after, zone).DateTime);
        for (var i = 0; i < 9; i++, day = day.AddDays(1))
        {
            if (repeat == ReminderRepeat.Weekdays && day.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday) continue;
            var candidate = ToInstant(day, at, zone);
            if (candidate > after) return candidate;
        }
        return null;
    }

    /// <summary>A local time that does not exist (spring forward) moves ahead by the gap; an ambiguous one is the first.</summary>
    private static DateTimeOffset ToInstant(DateOnly day, TimeOnly at, TimeZoneInfo zone)
    {
        var local = day.ToDateTime(at, DateTimeKind.Unspecified);
        if (zone.IsInvalidTime(local)) local = local.AddHours(1);
        var offset = zone.IsAmbiguousTime(local) ? zone.GetAmbiguousTimeOffsets(local).Max() : zone.GetUtcOffset(local);
        return new DateTimeOffset(local, offset).ToUniversalTime();
    }
}
