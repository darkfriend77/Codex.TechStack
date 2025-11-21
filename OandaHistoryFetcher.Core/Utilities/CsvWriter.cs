using System.Globalization;
using System.Text;
using OandaHistoryFetcher.Core.Models;

namespace OandaHistoryFetcher.Core.Utilities;

public static class CsvWriter
{
    private static readonly string[] Header =
    [
        "date",
        "from_currency",
        "to_currency",
        "amount",
        "rate",
        "source",
        "status",
        "error_message"
    ];

    public static Task WriteHeaderAsync(TextWriter writer) => writer.WriteLineAsync(string.Join(',', Header));

    public static Task WriteRecordAsync(TextWriter writer, RateRecord record)
    {
        var cells = new[]
        {
            record.Date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            Escape(record.FromCurrency),
            Escape(record.ToCurrency),
            record.Amount.ToString(CultureInfo.InvariantCulture),
            record.Rate?.ToString(CultureInfo.InvariantCulture) ?? string.Empty,
            Escape(record.Source),
            Escape(record.Status),
            Escape(record.ErrorMessage)
        };

        return writer.WriteLineAsync(string.Join(',', cells));
    }

    private static string Escape(string value)
    {
        if (value.IndexOfAny([',', '"', '\n', '\r']) >= 0)
        {
            var builder = new StringBuilder();
            builder.Append('"');
            foreach (var ch in value)
            {
                if (ch == '"')
                {
                    builder.Append("\"\"");
                }
                builder.Append(ch);
            }
            builder.Append('"');
            return builder.ToString();
        }

        return value;
    }
}
