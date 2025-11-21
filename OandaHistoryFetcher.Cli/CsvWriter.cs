using System.Globalization;
using System.Text;
using OandaHistoryFetcher.Core.Models;

namespace OandaHistoryFetcher.Cli;

internal static class CsvWriter
{
    public static void WriteHeader(TextWriter writer)
    {
        writer.WriteLine("date,from_currency,to_currency,amount,rate,source,status,error_message");
    }

    public static void WriteRecord(TextWriter writer, RateRecord record)
    {
        var fields = new[]
        {
            record.Date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            record.FromCurrency,
            record.ToCurrency,
            record.Amount.ToString(CultureInfo.InvariantCulture),
            record.Rate?.ToString(CultureInfo.InvariantCulture) ?? string.Empty,
            record.Source,
            record.Status,
            record.ErrorMessage ?? string.Empty
        };

        writer.WriteLine(string.Join(',', fields.Select(Escape)));
    }

    private static string Escape(string value)
    {
        if (value.IndexOfAny([',', '"', '\n', '\r']) >= 0)
        {
            var escaped = value.Replace("\"", "\"\"");
            return $"\"{escaped}\"";
        }

        return value;
    }

    public static StreamWriter CreateWriter(string path)
    {
        var fullPath = Path.GetFullPath(path);
        var directory = Path.GetDirectoryName(fullPath);
        if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
        {
            Directory.CreateDirectory(directory);
        }

        return new StreamWriter(File.Open(fullPath, FileMode.Create, FileAccess.Write, FileShare.Read), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
    }
}
