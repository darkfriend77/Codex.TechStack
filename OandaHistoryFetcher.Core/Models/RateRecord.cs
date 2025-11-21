namespace OandaHistoryFetcher.Core.Models;

/// <summary>
/// Represents a single rate record for a specific date.
/// </summary>
public sealed record RateRecord(
    DateOnly Date,
    string FromCurrency,
    string ToCurrency,
    decimal Amount,
    decimal? Rate,
    string Source,
    string Status,
    string ErrorMessage
);
