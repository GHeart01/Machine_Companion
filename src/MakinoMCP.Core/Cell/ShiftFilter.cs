using System.Globalization;

namespace MakinoMCP.Core.Cell;

/// <summary>A shift: the time of day from <see cref="StartSecond"/> to <see cref="EndSecond"/> (seconds after
/// midnight) on the weekdays in <see cref="Weekdays"/> (bit 0 Monday … bit 6 Sunday). Like the console's reports, an
/// overnight shift (start after end) counts, on each date, the time before the end and the time after the start, so a
/// date's figures stay that date's; equal start and end mean the whole day.</summary>
public sealed record ShiftFilter(int StartSecond, int EndSecond, int Weekdays)
{
    public const int AllDays = 0x7F;

    private const int Day = 24 * 60 * 60;

    private static readonly string[] DayNames = ["mon", "tue", "wed", "thu", "fri", "sat", "sun"];

    /// <summary>What the ArgumentException for a bad shift starts with: the reader's "No filter named …" wording, which
    /// the tools pass on to Claude as it is.</summary>
    public const string BadInputStart = "No filter named ";

    private const string Usage =
        "Use one of: shiftStart and shiftEnd as HH:mm, both or neither (e.g. 06:00 and 14:30; 22:00 to 06:00 for a night " +
        "shift), weekdays as day names or ranges (e.g. mon-fri, or sat,sun).";

    public bool WholeDay => StartSecond == EndSecond;

    /// <summary>The counted pieces of one date, as seconds after midnight: [From1, To1) and [From2, To2); the second
    /// is empty except for an overnight shift.</summary>
    public (int From1, int To1, int From2, int To2) Pieces =>
        WholeDay ? (0, Day, 0, 0)
        : StartSecond < EndSecond ? (StartSecond, EndSecond, 0, 0)
        : (0, EndSecond, StartSecond, Day);

    /// <summary>The filter in words, e.g. "06:00-14:30, mon-fri".</summary>
    public string Text
    {
        get
        {
            var time = WholeDay ? "whole day" : $"{Clock(StartSecond)}-{Clock(EndSecond)}";
            return Weekdays == AllDays ? time : $"{time}, {DaysText(Weekdays)}";
        }
    }

    /// <summary>True when <paramref name="time"/> falls in the shift (its date's weekday and time of day).</summary>
    public bool Covers(DateTime time)
    {
        var weekday = ((int)time.DayOfWeek + 6) % 7;
        if ((Weekdays & (1 << weekday)) == 0) return false;
        var second = (int)time.TimeOfDay.TotalSeconds;
        var (f1, t1, f2, t2) = Pieces;
        return (second >= f1 && second < t1) || (second >= f2 && second < t2);
    }

    /// <summary>The filter the tool's parameters ask for, or null when none is given. A bad value throws
    /// ArgumentException starting with "No filter named ", saying what is accepted.</summary>
    public static ShiftFilter? Parse(string? shiftStart, string? shiftEnd, string? weekdays)
    {
        var hasStart = !string.IsNullOrWhiteSpace(shiftStart);
        var hasEnd = !string.IsNullOrWhiteSpace(shiftEnd);
        var hasDays = !string.IsNullOrWhiteSpace(weekdays);
        if (!hasStart && !hasEnd && !hasDays) return null;
        if (hasStart != hasEnd) throw Bad(hasStart ? $"shiftStart {shiftStart!.Trim()} without shiftEnd" : $"shiftEnd {shiftEnd!.Trim()} without shiftStart");

        var start = hasStart ? Seconds(shiftStart!, "shiftStart") : 0;
        var end = hasEnd ? Seconds(shiftEnd!, "shiftEnd") : 0; // 24:00 is allowed as an end only
        if (start == Day) throw Bad($"shiftStart {shiftStart!.Trim()}");
        return new ShiftFilter(start, end, hasDays ? Days(weekdays!) : AllDays);
    }

    private static int Seconds(string text, string name)
    {
        var t = text.Trim();
        var parts = t.Split(':');
        if (parts.Length == 2
            && int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var h)
            && int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var m)
            && parts[1].Length == 2 && m < 60 && (h < 24 || (h == 24 && m == 0)))
        {
            return h * 3600 + m * 60;
        }
        throw Bad($"{name} {t}");
    }

    private static int Days(string text)
    {
        var mask = 0;
        foreach (var token in text.Split([',', ';', ' '], StringSplitOptions.RemoveEmptyEntries))
        {
            var range = token.Split('-', StringSplitOptions.TrimEntries);
            if (range.Length == 1)
            {
                mask |= 1 << DayIndex(range[0], text);
            }
            else if (range.Length == 2)
            {
                var from = DayIndex(range[0], text);
                var to = DayIndex(range[1], text);
                for (var d = from; ; d = (d + 1) % 7)
                {
                    mask |= 1 << d;
                    if (d == to) break;
                }
            }
            else throw Bad($"weekdays {text.Trim()}");
        }
        return mask == 0 ? throw Bad($"weekdays {text.Trim()}") : mask;
    }

    private static int DayIndex(string name, string whole)
    {
        var n = name.Trim().ToLowerInvariant();
        for (var i = 0; i < DayNames.Length; i++)
        {
            if (n.Length >= 2 && (DayNames[i].StartsWith(n, StringComparison.Ordinal) || n.StartsWith(DayNames[i], StringComparison.Ordinal)))
                return i;
        }
        throw Bad($"weekdays {whole.Trim()}");
    }

    private static ArgumentException Bad(string what) => new($"{BadInputStart}'{what}'. {Usage}");

    private static string Clock(int seconds) =>
        $"{seconds / 3600:00}:{seconds % 3600 / 60:00}";

    private static string DaysText(int mask)
    {
        // Runs of three or more days as a range (mon-fri), others listed.
        var parts = new List<string>();
        var i = 0;
        while (i < 7)
        {
            if ((mask & (1 << i)) == 0) { i++; continue; }
            var j = i;
            while (j + 1 < 7 && (mask & (1 << (j + 1))) != 0) j++;
            parts.Add(j - i >= 2 ? $"{DayNames[i]}-{DayNames[j]}" : string.Join(",", Enumerable.Range(i, j - i + 1).Select(k => DayNames[k])));
            i = j + 1;
        }
        return string.Join(",", parts);
    }
}
