# Oanda History Fetcher

A console utility for exporting historical currency conversion rates from OANDA either via the official API or by scraping the public converter with Playwright. The app outputs a CSV row for every calendar day in a requested date range and records both successes and failures.

> **Compliance reminder:** review OANDA's Terms of Use and robots.txt before running in scraping mode. Enable scraping explicitly with `--acknowledge-scraping`.

## Projects

- `OandaHistoryFetcher.Core` – shared models, utilities, and rate providers for API and Playwright scraping.
- `OandaHistoryFetcher.Cli` – command-line entry point that parses arguments, selects a provider, and writes CSV output.

## Usage

The environment in this workspace may not include the .NET SDK. If installed, restore and run with:

```bash
dotnet build OandaHistoryFetcher.sln
dotnet run --project OandaHistoryFetcher.Cli -- \
  --from-currency BTC --to-currency CHF --start-date 2024-01-01 --end-date 2024-01-10 \
  --amount 1 --mode scrape --acknowledge-scraping
```

API mode prefers the `OANDA_API_KEY` environment variable:

```bash
export OANDA_API_KEY=YOUR_KEY

dotnet run --project OandaHistoryFetcher.Cli -- \
  --from-currency BTC --to-currency CHF --start-date 2024-01-01 --end-date 2024-01-10 \
  --amount 1 --mode auto
```

### Key options

- `--from-currency`, `--to-currency` (required)
- `--start-date`, `--end-date` in `YYYY-MM-DD` format (required)
- `--amount` (default `1`)
- `--output` CSV path (default `./rates.csv`)
- `--mode` `api|scrape|auto` (default `auto`)
- `--oanda-api-key` or `OANDA_API_KEY` environment variable
- `--delay-ms` politeness delay between requests (default `1000`)
- `--max-retries` retry attempts per day (default `3`)
- `--log-level` `info|debug|error` (default `info`)
- `--acknowledge-scraping` required when scraping is used
