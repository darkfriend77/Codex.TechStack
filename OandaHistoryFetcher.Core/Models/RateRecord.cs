namespace OandaHistoryFetcher.Core.Models;

/// <summary>
/// Represents a single dated exchange rate entry for CSV output.
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
