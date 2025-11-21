namespace OandaHistoryFetcher.Core.Utilities;

public static class DateUtils
{
    public static IEnumerable<DateOnly> Enumerate(DateOnly start, DateOnly endInclusive)
    {
        if (endInclusive < start)
        {
            throw new ArgumentException("End date must be greater than or equal to start date.");
        }

        for (var date = start; date <= endInclusive; date = date.AddDays(1))
        {
            yield return date;
        }
    }
}
