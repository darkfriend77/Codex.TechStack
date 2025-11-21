using System.Globalization;

namespace OandaHistoryFetcher.Core.Utilities;

public static class RateParsing
{
    public static decimal ParseNormalizedNumber(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            throw new FormatException("Rate text was empty");
        }

        var cleaned = new string(raw.Where(ch => char.IsDigit(ch) || ch is '.' or ',' or '-').ToArray());
        var lastComma = cleaned.LastIndexOf(',');
        var lastDot = cleaned.LastIndexOf('.');
        var decimalSeparatorIndex = Math.Max(lastComma, lastDot);

        if (decimalSeparatorIndex >= 0)
        {
            var integerPart = cleaned[..decimalSeparatorIndex].Replace(".", string.Empty).Replace(",", string.Empty);
            var fractionalPart = cleaned[(decimalSeparatorIndex + 1)..];
            cleaned = string.Concat(integerPart, '.', fractionalPart);
        }
        else
        {
            cleaned = cleaned.Replace(".", string.Empty).Replace(",", string.Empty);
        }

        return decimal.Parse(cleaned, NumberStyles.AllowDecimalPoint | NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture);
    }
}
