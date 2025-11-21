namespace OandaHistoryFetcher.Core.Models;

/// <summary>
/// Exception thrown when rate fetching fails.
/// </summary>
public class RateFetchError : Exception
{
    public DateOnly Date { get; }
    public string SourceMode { get; }
    public bool IsRetryable { get; }

    public RateFetchError(
        DateOnly date, 
        string sourceMode, 
        string message, 
        bool isRetryable, 
        Exception? inner = null)
        : base(message, inner)
    {
        Date = date;
        SourceMode = sourceMode;
        IsRetryable = isRetryable;
    }
}
