namespace OandaHistoryFetcher.Core;

/// <summary>
/// Represents a failure while fetching a rate for a specific date.
/// </summary>
public class RateFetchError : Exception
{
    public DateOnly Date { get; }
    public string SourceMode { get; }
    public bool IsRetryable { get; }

    public RateFetchError(DateOnly date, string sourceMode, string message, bool isRetryable, Exception? inner = null)
        : base(message, inner)
    {
        Date = date;
        SourceMode = sourceMode;
        IsRetryable = isRetryable;
    }
}
