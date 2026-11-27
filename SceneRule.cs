// A rule is checked on display changes and at its next clock boundary, not on every UI tick.
internal sealed class SceneRule
{
    public int Version { get; set; } = 1;
    public string SceneId { get; set; } = "";
    public bool Enabled { get; set; } = true;
    public int Priority { get; set; }
    public int MinMonitorCount { get; set; } = 1;
    public int? MaxMonitorCount { get; set; }
    public TimeOnly? StartLocalTime { get; set; }
    public TimeOnly? EndLocalTime { get; set; }
    public int[]? DaysOfWeek { get; set; } // 0 = Sunday, 1 = Monday, ..., 6 = Saturday

    public bool Matches(DateTime localNow, int monitorCount)
    {
        if (!Enabled || Version != 1 || string.IsNullOrWhiteSpace(SceneId)) return false;
        if (monitorCount < Math.Max(1, MinMonitorCount) || monitorCount > (MaxMonitorCount ?? int.MaxValue)) return false;
        if (StartLocalTime is not { } start || EndLocalTime is not { } end)
            return IncludesDay(localNow.DayOfWeek);
        var time = TimeOnly.FromDateTime(localNow);
        if (start == end) return IncludesDay(localNow.DayOfWeek); // selected day, all day
        if (start < end) return IncludesDay(localNow.DayOfWeek) && time >= start && time < end;
        return (IncludesDay(localNow.DayOfWeek) && time >= start) ||
               (IncludesDay(localNow.AddDays(-1).DayOfWeek) && time < end);
    }

    bool IncludesDay(DayOfWeek day) => DaysOfWeek == null || DaysOfWeek.Length == 0 || DaysOfWeek.Contains((int)day);
}

internal static class SceneRuleEvaluator
{
    public static SceneRule? Select(IEnumerable<SceneRule> rules, DateTime localNow, int monitorCount) =>
        rules.Where(rule => rule.Matches(localNow, monitorCount))
            .OrderByDescending(rule => rule.Priority)
            .ThenByDescending(rule => rule.MinMonitorCount)
            .FirstOrDefault();

    public static DateTime? NextTimeBoundary(IEnumerable<SceneRule> rules, DateTime localNow)
    {
        DateTime? next = null;
        foreach (var rule in rules.Where(rule => rule.Enabled && rule.Version == 1 &&
                     rule.StartLocalTime.HasValue && rule.EndLocalTime.HasValue))
        {
            var start = rule.StartLocalTime!.Value;
            var end = rule.EndLocalTime!.Value;
            for (int offset = -1; offset <= 8; offset++)
            {
                var day = localNow.Date.AddDays(offset);
                if (rule.DaysOfWeek is { Length: > 0 } && !rule.DaysOfWeek.Contains((int)day.DayOfWeek)) continue;
                var begin = day.Add(start.ToTimeSpan());
                var finish = day.Add(end.ToTimeSpan());
                if (end <= start) finish = finish.AddDays(1);
                if (begin > localNow && (next == null || begin < next)) next = begin;
                if (finish > localNow && (next == null || finish < next)) next = finish;
            }
        }
        return next;
    }
}
