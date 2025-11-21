# OANDA History Fetcher

A command-line tool to fetch historical currency exchange rates from OANDA. It supports both using the official OANDA API and scraping the OANDA currency converter website (for personal/testing use).

## Features

- **Flexible Date Selection**: Fetch rates for a date range or a specific list of dates.
- **Multiple Modes**: 
  - `api`: Uses the official OANDA V3 API (requires API key).
  - `scrape`: Automates a browser to fetch rates from the public converter (requires acknowledgment).
  - `auto`: Defaults to API if key is present, otherwise falls back to scraping.
- **CSV Output**: Saves results to a clean CSV format.
- **Resilient**: Built-in retries and configurable delays.

## Prerequisites

- [.NET 8.0 SDK](https://dotnet.microsoft.com/download) or later.
- For **Scraping Mode**:
  - Playwright browsers (installed automatically on first run or via `pwsh bin/Debug/net8.0/playwright.ps1 install`).

## Installation

1. Clone the repository.
2. Restore dependencies:
   ```bash
   dotnet restore
   ```
3. Build the project:
   ```bash
   dotnet build
   ```

## Usage

Run the tool using `dotnet run` from the `OandaHistoryFetcher.Cli` directory.

### Basic Command Structure

```bash
dotnet run --project OandaHistoryFetcher.Cli -- [OPTIONS]
```

### Options

| Option | Description | Required | Default |
|--------|-------------|----------|---------|
| `--from-currency` | Source currency code (e.g., `USD`, `BTC`). | Yes | - |
| `--to-currency` | Target currency code (e.g., `CHF`). | Yes | - |
| `--dates` | Comma-separated list of specific dates (e.g., `21.02.2025, 22.02.2025`). | No* | - |
| `--start-date` | Start date (YYYY-MM-DD). | No* | - |
| `--end-date` | End date (YYYY-MM-DD). | No* | - |
| `--amount` | Amount to convert. | No | `1.0` |
| `--output` | Path to the output CSV file. | No | `./rates.csv` |
| `--mode` | Fetch mode: `api`, `scrape`, or `auto`. | No | `auto` |
| `--oanda-api-key` | OANDA API Key (can also be set via `OANDA_API_KEY` env var). | No | - |
| `--acknowledge-scraping`| Flag to acknowledge terms when using scrape mode. | Yes (if scraping) | `false` |

\* **Note**: You must provide either `--dates` OR both `--start-date` and `--end-date`.

### Examples

#### 1. Fetch a Date Range (Scraping)
Fetch USD to CHF rates for January 2025.
```bash
dotnet run --project OandaHistoryFetcher.Cli -- \
  --from-currency USD \
  --to-currency CHF \
  --start-date 2025-01-01 \
  --end-date 2025-01-31 \
  --mode scrape \
  --acknowledge-scraping
```

#### 2. Fetch Specific Dates
Fetch BTC to CHF rates for specific dates in February.
```bash
dotnet run --project OandaHistoryFetcher.Cli -- \
  --from-currency BTC \
  --to-currency CHF \
  --dates "21.02.2025, 22.02.2025, 25.02.2025" \
  --mode scrape \
  --acknowledge-scraping
```

#### 3. Using the API
If you have an API key, you can use the API mode for faster and more reliable results.
```bash
export OANDA_API_KEY="your-api-key"
dotnet run --project OandaHistoryFetcher.Cli -- \
  --from-currency EUR \
  --to-currency CHF \
  --start-date 2025-01-01 \
  --end-date 2025-01-05 \
  --mode api
```

## Supported Currencies

The tool supports any currency pair available on the OANDA platform. Common pairs verified include:

- **Fiat**: USD, EUR, CHF, GBP, JPY, etc.
- **Crypto**: BTC, ETH, DOT, KSM, PAXG, USDC, USDT.

## Disclaimer

When using the `scrape` mode, you are interacting with the OANDA public website. Please ensure you comply with their [Terms of Use](https://www.oanda.com/). This tool is for educational and personal use only.
