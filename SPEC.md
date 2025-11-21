# Specification: Historical OANDA Currency Rate Exporter

## 1. Goal / Overview

Build a **C# console application** that:

* Takes a **currency pair**, **date range**, and **amount** as input.
* For **each calendar day** in the range, retrieves the **historical exchange rate** from OANDA:

  * Prefer an official OANDA API if an API key is available.
  * Otherwise, **use browser automation with Playwright .NET** to interact with the public converter page:
    `https://www.oanda.com/currency-converter/en/?from=BTC&to=CHF&amount=1`
* Writes a **CSV file** with one row per day containing the date, currencies, amount, rate, and status.

The app must:

* Be polite to OANDA’s servers (throttling, retry/backoff, user-agent).
* Handle errors gracefully, recording failures per-day rather than aborting the whole run.
* Make it easy to later replace scraping with a pure API client.

> **Compliance note:** Before using scraping in production, a human must review OANDA’s Terms of Use and robots.txt. If scraping is disallowed, the app should require an API key instead and deactivate scraping mode.

---

## 2. Technology Stack

* **Language:** C# (latest LTS; e.g., .NET 8)
* **Runtime:** .NET SDK (8.0 or higher)
* **Project type:** Console application
* **Libraries:**

  * **Playwright .NET** for browser automation and interaction with the OANDA converter page
    NuGet: `Microsoft.Playwright`
  * Optional CSV helper:

    * `CsvHelper` or plain `System.IO` with manual CSV formatting
  * Optional config/logging:

    * `Microsoft.Extensions.Hosting`
    * `Microsoft.Extensions.Logging`

---

## 3. Functional Requirements

### 3.1 CLI Inputs

The console app must accept the following arguments:

* `--from-currency` (string, required)
  Example: `BTC`
* `--to-currency` (string, required)
  Example: `CHF`
* `--start-date` (string, required)
  Format: `YYYY-MM-DD` (e.g., `2024-01-01`)
* `--end-date` (string, required)
  Format: `YYYY-MM-DD` (inclusive)
* `--amount` (decimal, optional, default `1.0`)
* `--output` (string, optional, default `./rates.csv`)
* `--mode` (enum: `api` | `scrape` | `auto`, default `auto`)

  * `api` – use OANDA API only (requires API key).
  * `scrape` – use Playwright scraping only.
  * `auto` – prefer API if key present, otherwise fallback to scraping.
* `--oanda-api-key` (string, optional; can also come from env `OANDA_API_KEY`)
* `--delay-ms` (int, optional, default `1000`)
  Delay between per-day requests (both modes).
* `--max-retries` (int, optional, default `3`)
* `--log-level` (enum: `info` | `debug` | `error`, default `info`)
* Optional safety flag if desired: `--acknowledge-scraping` (bool) to explicitly enable scraping mode.

### 3.2 CSV Output

* CSV file in UTF-8 with headers and comma separator.

* Columns (header row):

  1. `date` – `YYYY-MM-DD`
  2. `from_currency`
  3. `to_currency`
  4. `amount`
  5. `rate` – numeric; exchange rate for 1 unit of `from_currency` in `to_currency`
  6. `source` – `"api"` or `"scrape"`
  7. `status` – `"ok"`, `"error"`, or `"skipped"`
  8. `error_message` – human-readable error; empty on success

* Each date in the range must produce exactly one row.

### 3.3 Date Iteration

* Parse `start-date` and `end-date` to `DateOnly`.
* Validate `start <= end`.
* Iterate from `start` to `end` inclusive; for each date `d`:

  * Attempt to fetch its rate.
  * On success → `status=ok`.
  * On persistent failure after retries → `status=error`.

### 3.4 Rate Retrieval Modes

#### 3.4.1 API Mode (OandaApiRateProvider)

* If `mode=api` or `mode=auto` with API key:

  * Use OANDA’s historical rates endpoint (implementer must check official docs and wire the call).
  * For each date:

    * Request rate for `from-currency` → `to-currency`.
    * Amount is 1 unit; if API returns multipliers, compute rate accordingly.
    * Use mid-market rate or clearly document choice (`bid`, `ask`, etc.).
  * If the API does not support a specific date (e.g., weekend), follow OANDA’s documented behavior. If no rate available → treat as error.
* The API client must:

  * Set authentication header (e.g., `Authorization: Bearer <key>` or as documented).
  * Handle HTTP status codes, timeouts, 429 (rate limit).
  * Implement retries with backoff.

#### 3.4.2 Scraping Mode (Playwright .NET: OandaPlaywrightRateProvider)

* If `mode=scrape` or `mode=auto` without API key:

  * Use **Playwright .NET** with Chromium (headless by default).

##### Browser Setup

* On startup:

  * `Playwright.CreateAsync()`
  * `Chromium.LaunchAsync(new BrowserTypeLaunchOptions { Headless = true })`
  * `browser.NewPageAsync()`
  * Set user agent and viewport if desired.

* Build and navigate once to base URL:

  ```csharp
  var url = $"https://www.oanda.com/currency-converter/en/?from={fromCurrency}&to={toCurrency}&amount={amount}";
  await page.GotoAsync(url, new() { WaitUntil = WaitUntilState.NetworkIdle });
  ```

* Reuse the **same `IBrowser` and `IPage`** for all days to improve performance.

##### Per-day Flow

For each date `d`:

1. **Ensure base currency, quote, and amount**

   * Verify that the UI already shows correct `from` / `to` and `amount` fields from initial navigation.
   * If necessary, adjust via `page.Locator(...)` (for robustness).

2. **Set the date in the date picker**

   * Identify the date input element or date picker trigger. Possible approaches:

     * `var dateInput = page.Locator("input[aria-label='Date']");`
       (actual selectors must be inspected in the DOM).
   * Determine expected date format from the input (e.g., `02 January 2025`).

     * Implement helper method: `string FormatDateForOanda(DateOnly d)` that matches the UI format.
   * Set the date:

     ```csharp
     await dateInput.ClickAsync();
     await dateInput.FillAsync(formattedDate);
     await dateInput.PressAsync("Enter");
     ```
   * Alternatively, interact with the calendar widget directly if needed.

3. **Wait for the rate update**

   * Identify the result element that displays converted amount (e.g., numeric field on the right).
   * Use locators with robust attributes (e.g., `data-testid`, role/text, etc.).
   * Example:

     ```csharp
     var resultLocator = page.Locator("data-testid=converter-result");
     await resultLocator.WaitForAsync(new() { Timeout = 10000 });
     var text = await resultLocator.InnerTextAsync();
     ```
   * Ensure either:

     * `WaitUntilState.NetworkIdle`, or
     * Wait for a specific DOM change (e.g., innerText to change) to avoid stale data.

4. **Parse the numeric rate**

   * The displayed value is for `amount` units; the rate is:

     * `rate = parsedConvertedAmount / amount`
   * Normalize number:

     * Remove non-digit except decimal separator and minus.
     * Handle thousands separators (commas/spaces) and locale decimal (`,` vs `.`):

       * Strategy: last occurrence of `.` or `,` is decimal separator; others are grouping separators.
   * Convert to `decimal` with invariant culture.

5. **Error handling**

   * If selectors not found, date not accepted, or parsing fails:

     * Throw a custom `RateFetchError` marked retryable.
   * Retries:

     * Up to `max-retries` with exponential backoff (e.g., `delay * attemptIndex`).
   * If still failing:

     * Return `status=error` for that date.

6. **Delay for politeness**

   * After each day (success or error), `await Task.Delay(delayMs)`.

##### Browser Shutdown

* On application shutdown (or once all dates done):

  * Close `IPage`, `IBrowser`, and dispose Playwright.

---

## 4. Non-Functional Requirements

### 4.1 Performance

* Scraping mode:

  * One browser instance for the whole run.
  * Reuse page, only changing date each iteration.
  * Accept that a long range (e.g., many months) will take minutes due to intentional delays.

* API mode:

  * Optionally implement batch queries if OANDA supports multiple dates per request.
  * Otherwise, per-day calls with delays.

### 4.2 Logging

* Use `Microsoft.Extensions.Logging` or simple `Console.WriteLine` abstraction.

At minimum log:

* Startup configuration (mode, date range, currencies).
* For each date:

  * Start of fetch.
  * Success with rate (info level).
  * Error with short reason (warning/error).
* Summary at end: total days, successes, errors.

`debug` level should log:

* Full URLs (minus secrets).
* HTTP status codes and truncated responses for API mode.
* Relevant DOM selectors and raw text from rate element.

### 4.3 Error Handling

* Input validation errors: print message, exit with non-zero code, do not create CSV.
* Per-date errors:

  * Never crash; always write a row with `status=error`.
* HTTP or scraping blocking (429, captchas):

  * Log warning.
  * Backoff before retry.
  * If persistent → record error and continue.

---

## 5. Architecture & Code Structure (C#)

### 5.1 Projects

* **Solution:** `OandaHistoryFetcher.sln`
* **Projects:**

  1. `OandaHistoryFetcher.Core` (class library)
  2. `OandaHistoryFetcher.Cli` (console app, references `Core`)

### 5.2 Core Library (`OandaHistoryFetcher.Core`)

#### 5.2.1 Models

```csharp
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
```

Custom exception:

```csharp
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
```

#### 5.2.2 Interfaces

```csharp
public interface IRateProvider : IAsyncDisposable
{
    Task<decimal> GetRateAsync(
        string fromCurrency,
        string toCurrency,
        decimal amount,
        DateOnly date,
        CancellationToken cancellationToken = default);
}
```

#### 5.2.3 Implementations

1. `OandaApiRateProvider` (placeholder for official API usage)

   * Constructor params: `string apiKey`, `TimeSpan delay`, `int maxRetries`, `ILogger? logger`.
   * Implements `GetRateAsync` using `HttpClient`.

2. `OandaPlaywrightRateProvider`

   * Fields: `IPlaywright`, `IBrowser`, `IPage`, delay, maxRetries, logger.
   * Lifecycle:

     * Constructor: accepts delay, maxRetries, logger, and headless flag (optional).
     * `InitializeAsync()` method or lazy initialization in first `GetRateAsync`.
     * `GetRateAsync`:

       * Navigates once initially.
       * For each call, changes date and reads result.
     * Implements `IAsyncDisposable` to close browser and Playwright.

### 5.3 CLI Project (`OandaHistoryFetcher.Cli`)

Responsibilities:

* Parse CLI args (either manually or using a library like `System.CommandLine`).
* Construct configuration object.
* Select mode:

  * If `mode=api` and no API key → fail.
  * If `mode=auto` and key present → use API; else use scraping.
* Instantiate appropriate `IRateProvider`.
* Generate date range and loop.
* Write CSV via `CsvWriter` utility.

Pseudo-control-flow:

```csharp
// 1. Parse args, validate
var config = AppConfig.Parse(args);

// 2. Determine mode and create rate provider
IRateProvider rateProvider = config.Mode switch
{
    "api"   => new OandaApiRateProvider(...),
    "scrape" => new OandaPlaywrightRateProvider(...),
    "auto" => config.HasApiKey
                ? new OandaApiRateProvider(...)
                : new OandaPlaywrightRateProvider(...),
    _ => throw ...
};

// 3. Open CSV and write header
using var writer = new StreamWriter(config.OutputPath, false, Encoding.UTF8);
// or using CsvHelper

// 4. Iterate dates
foreach (var date in DateUtils.EnumerateDates(config.StartDate, config.EndDate))
{
    try
    {
        decimal rate = await GetWithRetriesAsync(rateProvider, config, date, cancellationToken);
        var record = new RateRecord(date, config.From, config.To, config.Amount, rate, config.ActiveMode, "ok", "");
        CsvUtils.WriteRecord(writer, record);
    }
    catch (RateFetchError ex)
    {
        var record = new RateRecord(date, config.From, config.To, config.Amount, null, config.ActiveMode, "error", ex.Message);
        CsvUtils.WriteRecord(writer, record);
    }

    await Task.Delay(config.Delay, cancellationToken);
}

// 5. Dispose rateProvider
await rateProvider.DisposeAsync();
```

---

## 6. Testing

* **Unit tests** (in a separate test project):

  * Date utilities (range generation, formatting for CSV and OANDA).
  * Number parsing helper for scraped rate strings.
  * CSV writing helpers.
* **Integration tests (scraping)**:

  * Small date range (3–5 days) BTC→CHF.
  * Assert non-empty rates and valid CSV structure.
* **Integration tests (API)**:

  * Only if a test API key is configured.
* **Error simulations**:

  * Force invalid selector to ensure `status=error` rows are written.
  * Simulate 429 or timeout in API provider.

---

## 7. Example CLI Usage

Scraping, BTC→CHF, 2024-01-01 to 2024-10-31:

```bash
dotnet run --project OandaHistoryFetcher.Cli \
  --from-currency BTC \
  --to-currency CHF \
  --start-date 2024-01-01 \
  --end-date 2024-10-31 \
  --amount 1 \
  --mode scrape \
  --delay-ms 1500 \
  --output btc_chf_2024.csv \
  --acknowledge-scraping
```

Using API if `OANDA_API_KEY` is available:

```bash
set OANDA_API_KEY=YOUR_KEY_HERE

dotnet run --project OandaHistoryFetcher.Cli \
  --from-currency BTC \
  --to-currency CHF \
  --start-date 2024-01-01 \
  --end-date 2024-10-31 \
  --amount 1 \
  --mode auto \
  --output btc_chf_2024.csv
```

Test execution command line:

```bash
dotnet run --project OandaHistoryFetcher.Cli --from-currency BTC --to-currency CHF --start-date 2024-01-01 --end-date 2024-01-10 --amount 1 --mode scrape e --delay-ms 1500 --output btc_chf_2024.csv --acknowledge-scraping```
