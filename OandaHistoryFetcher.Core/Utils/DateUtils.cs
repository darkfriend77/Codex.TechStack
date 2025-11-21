namespace OandaHistoryFetcher.Core.Utils;

/// <summary>
/// Utility methods for date operations.
/// </summary>
public static class DateUtils
{
    /// <summary>
    /// Enumerates all dates in the inclusive range from start to end.
    /// </summary>
    public static IEnumerable<DateOnly> EnumerateDates(DateOnly start, DateOnly end)
    {
        if (start > end)
        {
            throw new ArgumentException("Start date must be less than or equal to end date");
        }

        for (var date = start; date <= end; date = date.AddDays(1))
        {
            yield return date;
        }
    }

    /// <summary>
    /// Validates and parses a date string in YYYY-MM-DD format.
    /// </summary>
    public static DateOnly ParseDateString(string dateString)
    {
        if (!DateOnly.TryParseExact(
            dateString, 
            "yyyy-MM-dd", 
            System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.None, 
            out var result))
        {
            throw new FormatException($"Invalid date format: {dateString}. Expected format: YYYY-MM-DD");
        }

        return result;
    }
}
