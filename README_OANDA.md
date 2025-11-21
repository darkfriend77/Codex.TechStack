# OANDA Historical Currency Rate Exporter

A C# console application that retrieves historical currency exchange rates from OANDA for a specified date range and exports them to CSV format. Supports both API-based retrieval and browser automation via Playwright.

## Features

- ✅ Fetch historical exchange rates for any currency pair
- ✅ Date range queries with per-day granularity
- ✅ Two modes of operation:
  - **API Mode**: Uses OANDA's official API (requires API key)
  - **Scraping Mode**: Uses Playwright browser automation (no API key required)
  - **Auto Mode**: Automatically selects API if key is available, otherwise uses scraping
- ✅ CSV export with comprehensive error tracking
- ✅ Configurable retry logic and rate limiting
- ✅ Detailed logging (info, debug, error levels)
- ✅ Graceful error handling (continues on per-date failures)

## Prerequisites

- .NET 9.0 SDK or higher
- For scraping mode: Playwright browsers (installed automatically via NuGet)

## Installation

1. Clone the repository:
```bash
git clone <repository-url>
cd Codex.TechStack
```

2. Restore dependencies:
```bash
dotnet restore
```

3. Install Playwright browsers (required for scraping mode):
```bash
cd OandaHistoryFetcher.Cli
pwsh bin/Debug/net9.0/playwright.ps1 install
# Or on Linux/Mac:
# ./bin/Debug/net9.0/playwright.sh install
```

4. Build the solution:
```bash
dotnet build
```

## Usage

### Basic Examples

**Scraping Mode (BTC → CHF, 2024-01-01 to 2024-01-10):**
```bash
dotnet run --project OandaHistoryFetcher.Cli \
  --from-currency BTC \
  --to-currency CHF \
  --start-date 2024-01-01 \
  --end-date 2024-01-10 \
  --mode scrape \
  --acknowledge-scraping
```

**API Mode (with environment variable):**
```bash
export OANDA_API_KEY=your_api_key_here

dotnet run --project OandaHistoryFetcher.Cli \
  --from-currency EUR \
  --to-currency USD \
  --start-date 2024-01-01 \
  --end-date 2024-12-31 \
  --mode api \
  --output eur_usd_2024.csv
```

**Auto Mode (prefers API if available):**
```bash
dotnet run --project OandaHistoryFetcher.Cli \
  --from-currency GBP \
  --to-currency JPY \
  --start-date 2024-06-01 \
  --end-date 2024-06-30 \
  --mode auto
```

### Command-Line Options

| Option | Description | Required | Default |
|--------|-------------|----------|---------|
| `--from-currency` | Source currency code (e.g., BTC, EUR) | Yes | - |
| `--to-currency` | Target currency code (e.g., CHF, USD) | Yes | - |
| `--start-date` | Start date (YYYY-MM-DD) | Yes | - |
| `--end-date` | End date (YYYY-MM-DD, inclusive) | Yes | - |
| `--amount` | Amount to convert | No | 1.0 |
| `--output` | Output CSV file path | No | ./rates.csv |
| `--mode` | Mode: `api`, `scrape`, or `auto` | No | auto |
| `--oanda-api-key` | OANDA API key | No | (from env) |
| `--delay-ms` | Delay between requests (ms) | No | 1000 |
| `--max-retries` | Max retries per request | No | 3 |
| `--log-level` | Log level: `info`, `debug`, `error` | No | info |
| `--acknowledge-scraping` | Acknowledge scraping mode usage | No | false |

### Environment Variables

- `OANDA_API_KEY`: Set your OANDA API key to avoid passing it via command line

## CSV Output Format

The output CSV file contains the following columns:

| Column | Description |
|--------|-------------|
| `date` | Date in YYYY-MM-DD format |
| `from_currency` | Source currency code |
| `to_currency` | Target currency code |
| `amount` | Amount converted |
| `rate` | Exchange rate (empty on error) |
| `source` | Data source: `api` or `scrape` |
| `status` | Status: `ok` or `error` |
| `error_message` | Error description (empty on success) |

**Example CSV:**
```csv
date,from_currency,to_currency,amount,rate,source,status,error_message
2024-01-01,BTC,CHF,1,42153.50,scrape,ok,
2024-01-02,BTC,CHF,1,43201.75,scrape,ok,
2024-01-03,BTC,CHF,1,,scrape,error,Failed to extract rate: Element not found
```

## Architecture

### Project Structure

```
OandaHistoryFetcher.sln
├── OandaHistoryFetcher.Core/        # Core library
│   ├── Interfaces/
│   │   └── IRateProvider.cs         # Rate provider interface
│   ├── Models/
│   │   ├── RateRecord.cs            # Rate data model
│   │   └── RateFetchError.cs        # Custom exception
│   ├── Providers/
│   │   ├── OandaApiRateProvider.cs  # API-based provider
│   │   └── OandaPlaywrightRateProvider.cs  # Playwright scraping provider
│   └── Utils/
│       ├── CsvUtils.cs              # CSV utilities
│       └── DateUtils.cs             # Date utilities
└── OandaHistoryFetcher.Cli/         # Console application
    ├── AppConfig.cs                 # Configuration model
    └── Program.cs                   # Main entry point
```

### Key Components

- **IRateProvider**: Interface for rate retrieval implementations
- **OandaApiRateProvider**: Uses OANDA's official API (placeholder implementation)
- **OandaPlaywrightRateProvider**: Browser automation using Playwright
- **AppConfig**: Validates and manages configuration
- **CsvUtils**: Handles CSV writing with CsvHelper
- **DateUtils**: Date range enumeration and parsing

## Scraping Compliance Notice

⚠️ **IMPORTANT**: Before using scraping mode in production:

1. Review OANDA's Terms of Use: https://www.oanda.com/legal/
2. Check their robots.txt: https://www.oanda.com/robots.txt
3. Ensure compliance with their policies

If scraping is not permitted, use API mode instead. The `--acknowledge-scraping` flag is required to use scraping mode as confirmation that you understand these requirements.

## API Mode Notes

The API implementation in this project is a **placeholder**. For production use:

1. Verify OANDA's API documentation for historical rates endpoints
2. Update the endpoint URL in `OandaApiRateProvider.cs`
3. Adjust authentication headers as needed
4. Parse the actual response format from OANDA's API
5. Handle API-specific rate limits and errors

Current API endpoint used (placeholder):
```
/v3/instruments/{from}_{to}/candles?granularity=D&from={date}T00:00:00Z&to={date}T23:59:59Z
```

## Troubleshooting

### Playwright Browser Installation

If scraping mode fails with browser errors:

```bash
cd OandaHistoryFetcher.Cli/bin/Debug/net9.0
pwsh playwright.ps1 install chromium
# Or: ./playwright.sh install chromium
```

### Rate Extraction Failures

If scraping mode cannot find rates:
- Run with `--log-level debug` to see detailed selector attempts
- OANDA may have changed their page structure
- Update selectors in `OandaPlaywrightRateProvider.cs`
- Consider using non-headless mode for debugging:
  ```csharp
  // In OandaPlaywrightRateProvider.cs constructor
  headless: false
  ```

### API Errors

- Verify your API key is correct
- Check OANDA API documentation for endpoint changes
- Review rate limits and quotas on your account

## Development

### Building

```bash
dotnet build
```

### Running Tests

```bash
# (Tests not yet implemented)
dotnet test
```

### Debug Logging

Enable debug logging to see detailed operation information:

```bash
dotnet run --project OandaHistoryFetcher.Cli \
  --from-currency BTC --to-currency CHF \
  --start-date 2024-01-01 --end-date 2024-01-02 \
  --mode scrape \
  --log-level debug \
  --acknowledge-scraping
```

## License

[Specify your license here]

## Contributing

[Specify contribution guidelines here]

## Disclaimer

This tool is provided as-is for educational and personal use. Users are responsible for:
- Complying with OANDA's Terms of Use
- Respecting rate limits and usage policies
- Obtaining appropriate API keys for API mode
- Verifying data accuracy for their use case

The authors are not responsible for any misuse or violations of third-party terms of service.
