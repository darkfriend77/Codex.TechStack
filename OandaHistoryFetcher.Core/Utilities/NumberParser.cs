using System.Globalization;

namespace OandaHistoryFetcher.Core.Utilities;

public static class NumberParser
{
    /// <summary>
    /// Parses numeric text that may contain grouping separators or mixed locale punctuation.
    /// Strategy: assume the last '.' or ',' is the decimal separator; other punctuations are removed.
    /// </summary>
    public static decimal ParseDecimalFlexible(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            throw new FormatException("Cannot parse an empty number string.");
        }

        var trimmed = text.Trim();
        var lastDot = trimmed.LastIndexOf('.');
        var lastComma = trimmed.LastIndexOf(',');
        var decimalSeparatorIndex = Math.Max(lastDot, lastComma);

        var sanitized = new Span<char>(new char[trimmed.Length]);
        var spanIndex = 0;

        for (var i = 0; i < trimmed.Length; i++)
        {
            var ch = trimmed[i];
            if (char.IsDigit(ch) || ch == '-' || ch == '+')
            {
                sanitized[spanIndex++] = ch;
                continue;
            }

            if (i == decimalSeparatorIndex && (ch == '.' || ch == ','))
            {
                sanitized[spanIndex++] = '.';
            }
        }

        var normalized = sanitized[..spanIndex].ToString();
        return decimal.Parse(normalized, NumberStyles.Number | NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture);
    }
}
