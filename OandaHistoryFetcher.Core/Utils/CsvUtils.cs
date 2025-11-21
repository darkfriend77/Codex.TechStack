using System.Globalization;
using System.Text;
using CsvHelper;
using CsvHelper.Configuration;
using OandaHistoryFetcher.Core.Models;

namespace OandaHistoryFetcher.Core.Utils;

/// <summary>
/// Utility methods for CSV operations.
/// </summary>
public static class CsvUtils
{
    /// <summary>
    /// Creates a CSV writer configured for rate records.
    /// </summary>
    public static CsvWriter CreateCsvWriter(StreamWriter streamWriter)
    {
        var config = new CsvConfiguration(CultureInfo.InvariantCulture)
        {
            HasHeaderRecord = true,
            Encoding = Encoding.UTF8
        };

        return new CsvWriter(streamWriter, config);
    }

    /// <summary>
    /// Writes a rate record to the CSV writer.
    /// </summary>
    public static void WriteRecord(CsvWriter writer, RateRecord record)
    {
        writer.WriteField(record.Date.ToString("yyyy-MM-dd"));
        writer.WriteField(record.FromCurrency);
        writer.WriteField(record.ToCurrency);
        writer.WriteField(record.Amount);
        writer.WriteField(record.Rate?.ToString(CultureInfo.InvariantCulture) ?? "");
        writer.WriteField(record.Source);
        writer.WriteField(record.Status);
        writer.WriteField(record.ErrorMessage);
        writer.NextRecord();
    }

    /// <summary>
    /// Writes the CSV header.
    /// </summary>
    public static void WriteHeader(CsvWriter writer)
    {
        writer.WriteField("date");
        writer.WriteField("from_currency");
        writer.WriteField("to_currency");
        writer.WriteField("amount");
        writer.WriteField("rate");
        writer.WriteField("source");
        writer.WriteField("status");
        writer.WriteField("error_message");
        writer.NextRecord();
    }
}
