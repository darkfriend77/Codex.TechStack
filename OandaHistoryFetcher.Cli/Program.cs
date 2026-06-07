using System;
using System.CommandLine;
using System.CommandLine.NamingConventionBinder;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using OandaHistoryFetcher.Core.Interfaces;
using OandaHistoryFetcher.Core.Models;
using OandaHistoryFetcher.Core.Providers;

namespace OandaHistoryFetcher.Cli;

class Program
{
    static async Task<int> Main(string[] args)
    {
        var rootCommand = new RootCommand("OANDA Historical Currency Rate Exporter");

        // Make optional to allow CSV-only usage, validation will handle requirements
        var fromOption = new Option<string?>("--from-currency", "Source currency code (e.g. BTC)");
        var toOption = new Option<string?>("--to-currency", "Target currency code (e.g. CHF)");
        var startDateOption = new Option<string?>("--start-date", "Start date (YYYY-MM-DD)");
        var endDateOption = new Option<string?>("--end-date", "End date (YYYY-MM-DD)");
        var datesOption = new Option<string?>("--dates", "Comma-separated list of dates (e.g. 21.02.2025, 22.02.2025)");
        var amountOption = new Option<decimal>("--amount", () => 1.0m, "Amount to convert");
        var outputOption = new Option<string>("--output", () => "./rates.csv", "Output CSV file path");
        var inputCsvOption = new Option<string?>("--input-csv", "Input CSV file path (incremental mode)");
        inputCsvOption.AddAlias("-i");
        var modeOption = new Option<string>("--mode", () => "auto", "Mode: api, scrape, auto");
        var apiKeyOption = new Option<string?>("--oanda-api-key", "OANDA API Key (or set OANDA_API_KEY env var)");
        var delayOption = new Option<int>("--delay-ms", () => 1000, "Delay between requests in ms");
        var maxRetriesOption = new Option<int>("--max-retries", () => 3, "Max retries per request");
        var logLevelOption = new Option<string>("--log-level", () => "info", "Log level: info, debug, error");
        var ackScrapingOption = new Option<bool>("--acknowledge-scraping", "Acknowledge scraping terms");

        rootCommand.AddOption(fromOption);
        rootCommand.AddOption(toOption);
        rootCommand.AddOption(startDateOption);
        rootCommand.AddOption(endDateOption);
        rootCommand.AddOption(datesOption);
        rootCommand.AddOption(amountOption);
        rootCommand.AddOption(outputOption);
        rootCommand.AddOption(inputCsvOption);
        rootCommand.AddOption(modeOption);
        rootCommand.AddOption(apiKeyOption);
        rootCommand.AddOption(delayOption);
        rootCommand.AddOption(maxRetriesOption);
        rootCommand.AddOption(logLevelOption);
        rootCommand.AddOption(ackScrapingOption);

        rootCommand.SetHandler(async (context) =>
        {
            var fromArg = context.ParseResult.GetValueForOption(fromOption);
            var toArg = context.ParseResult.GetValueForOption(toOption);
            var startStr = context.ParseResult.GetValueForOption(startDateOption);
            var endStr = context.ParseResult.GetValueForOption(endDateOption);
            var datesStr = context.ParseResult.GetValueForOption(datesOption);
            var amountDefault = context.ParseResult.GetValueForOption(amountOption);
            var output = context.ParseResult.GetValueForOption(outputOption)!;
            var inputCsv = context.ParseResult.GetValueForOption(inputCsvOption);
            var mode = context.ParseResult.GetValueForOption(modeOption)!;
            var apiKey = context.ParseResult.GetValueForOption(apiKeyOption) ?? Environment.GetEnvironmentVariable("OANDA_API_KEY");
            var delayMs = context.ParseResult.GetValueForOption(delayOption);
            var maxRetries = context.ParseResult.GetValueForOption(maxRetriesOption);
            var logLevelStr = context.ParseResult.GetValueForOption(logLevelOption)!;
            var ackScraping = context.ParseResult.GetValueForOption(ackScrapingOption);

            // Setup Logging
            using var loggerFactory = LoggerFactory.Create(builder =>
            {
                builder.AddSimpleConsole(options =>
                {
                    options.IncludeScopes = false;
                    options.SingleLine = true;
                    options.TimestampFormat = "HH:mm:ss ";
                });

                var level = logLevelStr.ToLower() switch
                {
                    "debug" => LogLevel.Debug,
                    "error" => LogLevel.Error,
                    _ => LogLevel.Information
                };
                builder.SetMinimumLevel(level);
            });
            var logger = loggerFactory.CreateLogger<Program>();

            // 1. Load Jobs
            List<JobItem> jobs = new();

            if (!string.IsNullOrWhiteSpace(inputCsv))
            {
                if (!File.Exists(inputCsv))
                {
                    logger.LogError("Input CSV file not found: {Path}", inputCsv);
                    context.ExitCode = 1;
                    return;
                }

                try
                {
                    logger.LogInformation("Reading input CSV: {Path}", inputCsv);
                    var lines = await File.ReadAllLinesAsync(inputCsv);
                    if (lines.Length > 0)
                    {
                        var headerLine = lines[0];
                        var headers = ParseCsvLine(headerLine).Select(h => h.Trim().ToLower()).ToList();

                        // Map headers
                        var idxDate = headers.IndexOf("date");
                        var idxFrom = headers.IndexOf("from_currency");
                        var idxTo = headers.IndexOf("to_currency");
                        var idxAmount = headers.IndexOf("amount");
                        var idxStatus = headers.IndexOf("status");
                        var idxRate = headers.IndexOf("rate");
                        var idxSource = headers.IndexOf("source");
                        var idxError = headers.IndexOf("error_message");

                        if (idxDate == -1)
                        {
                            logger.LogError("Input CSV must contain 'date' column.");
                            context.ExitCode = 1;
                            return;
                        }

                        for (int i = 1; i < lines.Length; i++)
                        {
                            var line = lines[i];
                            if (string.IsNullOrWhiteSpace(line)) continue;

                            var cols = ParseCsvLine(line);
                            // Basic safety
                            if (cols.Count <= idxDate) continue;

                            bool parsed = DateOnly.TryParseExact(cols[idxDate], new[] { "yyyy-MM-dd", "dd.MM.yyyy", "dd/MM/yyyy" }, CultureInfo.InvariantCulture, DateTimeStyles.None, out var date);
                            if (!parsed)
                            {
                                // Fallback to generic only if exact matches fail (though generic might find wrong things for ambiguous dates, so maybe valid to just rely on exact if we want strictness? User said "input is always...")
                                // But let's keep generic as fallback but AFTER exact.
                                parsed = DateOnly.TryParse(cols[idxDate], CultureInfo.InvariantCulture, DateTimeStyles.None, out date);
                            }

                            if (!parsed)
                            {
                                logger.LogWarning("Line {Line}: Invalid date {Val}, skipping.", i + 1, cols[idxDate]);
                                continue;
                            }

                            string from = (idxFrom != -1 && idxFrom < cols.Count) ? cols[idxFrom] : (fromArg ?? "");
                            string to = (idxTo != -1 && idxTo < cols.Count) ? cols[idxTo] : (toArg ?? "");
                            string amountStr = (idxAmount != -1 && idxAmount < cols.Count) ? cols[idxAmount] : "";
                            decimal amount = decimal.TryParse(amountStr, NumberStyles.Any, CultureInfo.InvariantCulture, out var amt) ? amt : amountDefault;

                            string status = (idxStatus != -1 && idxStatus < cols.Count) ? cols[idxStatus] : "todo";
                            string? rateVal = (idxRate != -1 && idxRate < cols.Count) ? cols[idxRate] : null;
                            string? sourceVal = (idxSource != -1 && idxSource < cols.Count) ? cols[idxSource] : null;
                            string? errorVal = (idxError != -1 && idxError < cols.Count) ? cols[idxError] : null;

                            // Fallback overrides if CSV missing values
                            if (string.IsNullOrWhiteSpace(from)) from = fromArg ?? "";
                            if (string.IsNullOrWhiteSpace(to)) to = toArg ?? "";

                            if (string.IsNullOrWhiteSpace(from) || string.IsNullOrWhiteSpace(to))
                            {
                                logger.LogWarning("Line {Line}: Missing From/To currency and no CLI defaults provided. Skipping.", i + 1);
                                continue;
                            }

                            // If status is empty, treat as todo
                            if (string.IsNullOrWhiteSpace(status)) status = "todo";

                            jobs.Add(new JobItem(date, from, to, amount, status, rateVal, sourceVal, errorVal));
                        }
                    }
                }
                catch (Exception ex)
                {
                    logger.LogError("Error reading input CSV: {Msg}", ex.Message);
                    context.ExitCode = 1;
                    return;
                }
            }
            else
            {
                // Standard mode validation
                if (string.IsNullOrWhiteSpace(fromArg) || string.IsNullOrWhiteSpace(toArg))
                {
                    logger.LogError("--from-currency and --to-currency are required when not using CSV input.");
                    context.ExitCode = 1;
                    return;
                }

                // Date generation logic
                var targetDates = new List<DateOnly>();
                if (!string.IsNullOrWhiteSpace(datesStr))
                {
                    var parts = datesStr.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                    foreach (var part in parts)
                    {
                        if (DateOnly.TryParseExact(part, new[] { "yyyy-MM-dd", "dd.MM.yyyy" }, CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
                            targetDates.Add(date);
                        else
                        {
                            logger.LogError("Invalid date: {Part}", part);
                            context.ExitCode = 1;
                            return;
                        }
                    }
                    targetDates.Sort();
                }
                else if (!string.IsNullOrWhiteSpace(startStr) && !string.IsNullOrWhiteSpace(endStr))
                {
                    if (DateOnly.TryParse(startStr, out var start) && DateOnly.TryParse(endStr, out var end))
                    {
                        if (start > end) { logger.LogError("Start > End"); context.ExitCode = 1; return; }
                        for (var d = start; d <= end; d = d.AddDays(1)) targetDates.Add(d);
                    }
                    else { logger.LogError("Invalid start/end dates"); context.ExitCode = 1; return; }
                }
                else
                {
                    logger.LogError("Provide --dates OR (--start-date AND --end-date) OR --input-csv");
                    context.ExitCode = 1;
                    return;
                }

                foreach (var d in targetDates)
                {
                    jobs.Add(new JobItem(d, fromArg, toArg, amountDefault, "todo", null, null, null));
                }
            }

            if (jobs.Count == 0)
            {
                logger.LogError("No valid jobs to process.");
                context.ExitCode = 1;
                return;
            }

            // Mode Selection
            IRateProvider? provider = null;
            string activeMode = mode;

            try
            {
                if (mode == "api")
                {
                    if (string.IsNullOrEmpty(apiKey)) { logger.LogError("API Key required for API mode."); context.ExitCode = 1; return; }
                    provider = new OandaApiRateProvider(apiKey, TimeSpan.FromMilliseconds(delayMs), maxRetries, logger);
                }
                else if (mode == "scrape")
                {
                    if (!ackScraping) logger.LogWarning("Scraping acknowledged?");
                    provider = new OandaPlaywrightRateProvider(TimeSpan.FromMilliseconds(delayMs), maxRetries, headless: true, logger: logger);
                }
                else
                {
                    if (!string.IsNullOrEmpty(apiKey))
                    {
                        activeMode = "api";
                        provider = new OandaApiRateProvider(apiKey, TimeSpan.FromMilliseconds(delayMs), maxRetries, logger);
                    }
                    else
                    {
                        activeMode = "scrape";
                        provider = new OandaPlaywrightRateProvider(TimeSpan.FromMilliseconds(delayMs), maxRetries, headless: true, logger: logger);
                    }
                }

                logger.LogInformation("Processing {Count} jobs using {Mode}...", jobs.Count, activeMode);

                // Open output file for writing (overwrite/truncate mode to dump full state)
                // We will stream write results as we go.
                using var writer = new StreamWriter(output, false, Encoding.UTF8);
                var csvHeader = "date,from_currency,to_currency,amount,rate,source,status,error_message";
                await writer.WriteLineAsync(csvHeader);

                int successCount = 0;
                int errorCount = 0;
                int skipCount = 0;

                for (int i = 0; i < jobs.Count; i++)
                {
                    var job = jobs[i];

                    if (job.Status == "ok")
                    {
                        // Already done, just write it
                        job.WriteCsvLine(writer); // Or write manually
                        skipCount++;
                        continue;
                    }

                    // Process
                    try
                    {
                        decimal rate = await provider.GetRateAsync(job.From, job.To, job.Amount, job.Date);
                        job.Rate = rate.ToString(CultureInfo.InvariantCulture);
                        job.Status = "ok";
                        job.Source = activeMode;
                        job.ErrorMessage = "";
                        successCount++;
                        logger.LogInformation("[{I}/{Total}] {Date} {From}/{To}: {Rate}", i + 1, jobs.Count, job.Date, job.From, job.To, rate);
                    }
                    catch (RateFetchError ex)
                    {
                        job.Status = "error";
                        job.ErrorMessage = ex.Message;
                        errorCount++;
                        logger.LogError("[{I}/{Total}] {Date} failed: {Msg}", i + 1, jobs.Count, job.Date, ex.Message);
                    }
                    catch (Exception ex)
                    {
                        job.Status = "error";
                        job.ErrorMessage = ex.Message;
                        errorCount++;
                        logger.LogError("[{I}/{Total}] {Date} critical error: {Msg}", i + 1, jobs.Count, job.Date, ex.Message);
                    }

                    job.WriteCsvLine(writer);
                    await writer.FlushAsync();

                    // Delay if actually processed
                    if (i < jobs.Count - 1)
                        await Task.Delay(delayMs);
                }

                logger.LogInformation("Done. OK: {Ok}, Errors: {Err}, Skipped: {Skip}. Saved to {Out}", successCount, errorCount, skipCount, output);

            }
            catch (Exception ex)
            {
                logger.LogCritical(ex, "Fatal error.");
                context.ExitCode = 1;
            }
            finally
            {
                if (provider != null) await provider.DisposeAsync();
            }
        });

        return await rootCommand.InvokeAsync(args);
    }

    static List<string> ParseCsvLine(string line)
    {
        var result = new List<string>();
        bool inQuotes = false;
        var current = new StringBuilder();
        for (int i = 0; i < line.Length; i++)
        {
            char c = line[i];
            if (c == '"')
            {
                if (inQuotes && i + 1 < line.Length && line[i + 1] == '"')
                {
                    current.Append('"');
                    i++;
                }
                else
                {
                    inQuotes = !inQuotes;
                }
            }
            else if (c == ',' && !inQuotes)
            {
                result.Add(current.ToString());
                current.Clear();
            }
            else
            {
                current.Append(c);
            }
        }
        result.Add(current.ToString());
        return result;
    }
}

class JobItem
{
    public DateOnly Date { get; set; }
    public string From { get; set; }
    public string To { get; set; }
    public decimal Amount { get; set; }
    public string Status { get; set; }
    public string? Rate { get; set; }
    public string? Source { get; set; }
    public string? ErrorMessage { get; set; }

    public JobItem(DateOnly date, string from, string to, decimal amount, string status, string? rate, string? source, string? errorMessage)
    {
        Date = date; From = from; To = to; Amount = amount; Status = status; Rate = rate; Source = source; ErrorMessage = errorMessage;
    }

    public void WriteCsvLine(StreamWriter writer)
    {
        var line = $"{Date:yyyy-MM-dd},{From},{To},{Amount},{Rate},{Source},{Status},\"{(ErrorMessage ?? "").Replace("\"", "\"\"")}\"";
        writer.WriteLine(line);
    }
}
