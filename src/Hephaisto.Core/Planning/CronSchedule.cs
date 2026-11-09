using System.Globalization;

namespace Hephaisto.Core.Planning;

/// <summary>
/// A five-field cron expression - minute, hour, day of month, month, day of week - read in a time
/// zone (#312, the weekly review's schedule). Numbers, <c>*</c>, lists, ranges and steps; no
/// names, no seconds, no <c>L</c>/<c>W</c>/<c>#</c>. Pure: the caller passes the moment and the zone.
/// </summary>
/// <remarks>
/// <para>
/// As in Vixie cron, a day matches when the day of month OR the day of week does if both are
/// restricted, and the restricted one when only one is. 7 is Sunday as well as 0.
/// </para>
/// <para>
/// <b>Daylight saving.</b> A local time the clock jumps over (02:30 on the spring night) does not
/// happen and is skipped; a local time that happens twice (02:30 on the autumn night) is the
/// first of the two. A Monday at 06:00 in Europe/Berlin is neither.
/// </para>
/// </remarks>
public sealed class CronSchedule
{
    private readonly bool[] minutes = new bool[60];
    private readonly bool[] hours = new bool[24];
    private readonly bool[] days = new bool[32];
    private readonly bool[] months = new bool[13];
    private readonly bool[] weekdays = new bool[7];
    private bool anyDay;
    private bool anyWeekday;

    private CronSchedule(string expression) => Expression = expression;

    public string Expression { get; }

    public static CronSchedule Parse(string expression) =>
        TryParse(expression, out var schedule, out var why) ? schedule! : throw new FormatException(why);

    public static bool TryParse(string? expression, out CronSchedule? schedule, out string? why)
    {
        schedule = null;
        var fields = (expression ?? string.Empty).Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        if (fields.Length != 5)
        {
            why = $"it has {fields.Length} fields, and a schedule has five (minute hour day-of-month month day-of-week)";
            return false;
        }

        var s = new CronSchedule(string.Join(' ', fields));

        if (!Field(fields[0], 0, 59, s.minutes, "minute", out why)
            || !Field(fields[1], 0, 23, s.hours, "hour", out why)
            || !Field(fields[2], 1, 31, s.days, "day of month", out why)
            || !Field(fields[3], 1, 12, s.months, "month", out why))
        {
            return false;
        }

        var weekdays = new bool[8];

        if (!Field(fields[4], 0, 7, weekdays, "day of week", out why))
        {
            return false;
        }

        for (var d = 0; d < 7; d++)
        {
            s.weekdays[d] = weekdays[d] || (d == 0 && weekdays[7]);
        }

        s.anyDay = fields[2] == "*";
        s.anyWeekday = fields[4] == "*";
        schedule = s;
        why = null;
        return true;
    }

    /// <summary>The first moment strictly after <paramref name="after"/> the schedule names, or null when none comes within five years.</summary>
    public DateTimeOffset? Next(DateTimeOffset after, TimeZoneInfo zone)
    {
        ArgumentNullException.ThrowIfNull(zone);

        var local = TimeZoneInfo.ConvertTime(after, zone).DateTime;
        var t = new DateTime(local.Year, local.Month, local.Day, local.Hour, local.Minute, 0, DateTimeKind.Unspecified).AddMinutes(1);
        var end = t.AddYears(5);

        while (t < end)
        {
            if (!months[t.Month])
            {
                t = new DateTime(t.Year, t.Month, 1, 0, 0, 0, DateTimeKind.Unspecified).AddMonths(1);
                continue;
            }

            if (!DayMatches(t))
            {
                t = t.Date.AddDays(1);
                continue;
            }

            if (!hours[t.Hour])
            {
                t = t.Date.AddHours(t.Hour + 1);
                continue;
            }

            if (!minutes[t.Minute])
            {
                t = t.AddMinutes(1);
                continue;
            }

            if (zone.IsInvalidTime(t))
            {
                t = t.AddMinutes(1);
                continue;
            }

            var offset = zone.IsAmbiguousTime(t) ? zone.GetAmbiguousTimeOffsets(t).Max() : zone.GetUtcOffset(t);
            var at = new DateTimeOffset(t, offset);

            if (at > after)
            {
                return at.ToUniversalTime();
            }

            t = t.AddMinutes(1);
        }

        return null;
    }

    /// <summary>
    /// The newest moment the schedule named in <c>(at - within, at]</c>, or null. What a scheduler
    /// that wakes up late - or starts after a restart - still owes.
    /// </summary>
    public DateTimeOffset? Latest(DateTimeOffset at, TimeSpan within, TimeZoneInfo zone)
    {
        DateTimeOffset? latest = null;
        var from = at - within;

        // Forward from the start of the window: a schedule names at most a minute's worth per minute.
        for (var next = Next(from, zone); next is { } n && n <= at; next = Next(n, zone))
        {
            latest = n;
        }

        return latest;
    }

    public override string ToString() => Expression;

    private bool DayMatches(DateTime t)
    {
        var dom = days[t.Day];
        var dow = weekdays[(int)t.DayOfWeek];

        return (anyDay, anyWeekday) switch
        {
            (true, true) => true,
            (true, false) => dow,
            (false, true) => dom,
            _ => dom || dow,
        };
    }

    private static bool Field(string text, int min, int max, bool[] set, string name, out string? why)
    {
        foreach (var part in text.Split(','))
        {
            var (range, step) = part.Split('/') switch
            {
                [var r] => (r, 1),
                [var r, var s] when int.TryParse(s, NumberStyles.None, CultureInfo.InvariantCulture, out var n) && n > 0 => (r, n),
                _ => (null, 0),
            };

            if (range is null)
            {
                why = $"the {name} field's '{part}' has a step that is not a positive number";
                return false;
            }

            int from, to;

            if (range == "*")
            {
                (from, to) = (min, max);
            }
            else if (range.Split('-') is [var a, var b]
                && int.TryParse(a, NumberStyles.None, CultureInfo.InvariantCulture, out from)
                && int.TryParse(b, NumberStyles.None, CultureInfo.InvariantCulture, out to))
            {
                // Read above.
            }
            else if (int.TryParse(range, NumberStyles.None, CultureInfo.InvariantCulture, out from))
            {
                // A single value with a step runs to the end of the field, as cron reads "5/15".
                to = step > 1 ? max : from;
            }
            else
            {
                why = $"the {name} field's '{part}' is not a number, a range or *";
                return false;
            }

            if (from < min || to > max || from > to)
            {
                why = $"the {name} field's '{part}' is outside {min}-{max}";
                return false;
            }

            for (var v = from; v <= to; v += step)
            {
                set[v] = true;
            }
        }

        why = null;
        return true;
    }
}
