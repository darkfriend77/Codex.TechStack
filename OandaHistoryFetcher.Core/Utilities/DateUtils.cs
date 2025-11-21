namespace OandaHistoryFetcher.Core.Utilities;

public static class DateUtils
{
    public static IEnumerable<DateOnly> EnumerateDates(DateOnly start, DateOnly end)
    {
        if (end < start)
        {
            throw new ArgumentException("End date must be on or after start date", nameof(end));
        }

        for (var date = start; date <= end; date = date.AddDays(1))
        {
            yield return date;
        }
    }

    public static DateTime ToUtcStart(this DateOnly date) => date.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc);
}
