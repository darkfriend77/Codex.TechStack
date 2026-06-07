# OANDA History Fetcher

A command-line tool to fetch historical currency exchange rates from OANDA, designed for building auditable rate tables (e.g. for tax reporting, where you need the rate of a currency pair on a specific date).

It can fetch a single date, a list of dates, or a date range, and writes the results to a CSV file. It also supports an **incremental CSV mode** that reads a job list from a CSV, fetches only the rows that aren't done yet, and writes the full state back out — so interrupted runs can be resumed.

## How it works

The tool supports two rate sources:

- **`scrape`** — Automates a headless Chromium browser (via Playwright) to read rates from the public OANDA currency converter. This is the fully implemented and default path.
- **`api`** — Intended to use the official OANDA v3 API. **⚠️ Not yet implemented** — selecting `api` mode (or `auto` mode while `OANDA_API_KEY` is set) will currently fail every job with a "not implemented" error. See [Status](#status).

## Features

- **Flexible date selection** — a single date, a comma-separated list of dates, or an inclusive start/end range.
- **Incremental / resumable runs** — feed a CSV job list via `--input-csv`; rows already marked `ok` are kept as-is, everything else is (re)fetched. The output is the full updated table.
- **Per-row currency pairs** — when using `--input-csv`, each row can specify its own `from_currency`, `to_currency`, and `amount`, so one run can cover many different pairs.
- **CSV output** — a single, consistent schema (see [Output format](#output-format)) written incrementally and flushed after every row.
- **Resilient** — configurable retry count with exponential backoff and a configurable delay between requests.
- **Configurable logging** — `info`, `debug`, or `error` levels.

## Status

| Mode | State |
|------|-------|
| `scrape` | ✅ Implemented |
| `api` | ❌ Not implemented (stub — throws `NotImplementedException`) |
| `auto` | ⚠️ Uses `api` if `OANDA_API_KEY` is set, otherwise `scrape`. Because `api` is a stub, **set no API key if you want `auto` to work.** |

> **Note for tax use:** the scraper currently does not independently verify that the fetched rate corresponds to the requested date, and it does not apply a documented weekend/holiday policy. Spot-check the output before relying on it.

## Prerequisites

- [.NET 10.0 SDK](https://dotnet.microsoft.com/download) (10.0.5 or later; both projects target `net10.0`).
- For **scrape mode**, Playwright browsers. They are installed on first run; if needed you can install them manually after building:
  ```bash
  pwsh OandaHistoryFetcher.Cli/bin/Debug/net10.0/playwright.ps1 install
  ```

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

```bash
dotnet run --project OandaHistoryFetcher.Cli -- [OPTIONS]
```

You must provide **one** of the following job sources:

- `--dates` (a list of specific dates), **or**
- `--start-date` **and** `--end-date` (an inclusive range), **or**
- `--input-csv` (a CSV job list).

### Options

| Option | Alias | Description | Required | Default |
|--------|-------|-------------|----------|---------|
| `--from-currency` | | Source currency code (e.g. `USD`, `BTC`). | Yes, unless using `--input-csv` | - |
| `--to-currency` | | Target currency code (e.g. `CHF`). | Yes, unless using `--input-csv` | - |
| `--dates` | | Comma-separated list of dates. Accepts `yyyy-MM-dd` or `dd.MM.yyyy` (e.g. `2025-02-21, 22.02.2025`). | One source required* | - |
| `--start-date` | | Start date of an inclusive range (`yyyy-MM-dd`). | One source required* | - |
| `--end-date` | | End date of an inclusive range (`yyyy-MM-dd`). | One source required* | - |
| `--input-csv` | `-i` | Path to a CSV job list (incremental mode). See [Incremental mode](#incremental-mode). | One source required* | - |
| `--amount` | | Amount to convert. The reported `rate` is normalized to a per-1-unit rate. | No | `1.0` |
| `--output` | | Path to the output CSV file. | No | `./rates.csv` |
| `--mode` | | Fetch mode: `api`, `scrape`, or `auto`. | No | `auto` |
| `--oanda-api-key` | | OANDA API key (or set the `OANDA_API_KEY` env var). Only relevant once `api` mode is implemented. | No | - |
| `--delay-ms` | | Delay between requests, in milliseconds. Also the base unit for retry backoff. | No | `1000` |
| `--max-retries` | | Maximum retries per request. | No | `3` |
| `--log-level` | | Log verbosity: `info`, `debug`, or `error`. | No | `info` |
| `--acknowledge-scraping` | | Acknowledge the scraping terms of use. Currently advisory (a warning is logged if omitted). | Recommended for scrape mode | `false` |

\* **Job source:** provide exactly one of `--dates`, the `--start-date`/`--end-date` pair, or `--input-csv`.

### Output format

The output CSV always has this header:

```
date,from_currency,to_currency,amount,rate,source,status,error_message
```

- `date` — written as `yyyy-MM-dd`.
- `rate` — the per-1-unit rate (`total / amount`), in invariant culture (`.` decimal separator).
- `source` — `scrape` or `api`, reflecting which provider produced the row.
- `status` — `ok`, `error`, or `todo`.
- `error_message` — populated (and quoted) when `status` is `error`.

### Incremental mode

In incremental mode you supply a CSV with at least a `date` column. The recognized columns are:

```
date,from_currency,to_currency,amount,rate,source,status,error_message
```

Behavior:

- The `date` column is **required**; dates are parsed as `yyyy-MM-dd`, `dd.MM.yyyy`, or `dd/MM/yyyy`.
- Rows whose `status` is `ok` are **kept as-is** (not re-fetched).
- Any other status (`todo`, `error`, blank, …) is **fetched**.
- If `from_currency` / `to_currency` / `amount` are missing on a row, the tool falls back to the `--from-currency` / `--to-currency` / `--amount` values passed on the command line.
- Rows with an unparseable date, or with no currency pair and no CLI fallback, are skipped with a warning.

The full table (kept + newly fetched rows) is written to `--output`.

> ⚠️ The output file is opened in truncate mode. If you point `--input-csv` and `--output` at the **same file**, a crash mid-run can lose data. Prefer writing to a separate output file, or back up the input first.

## Examples

#### 1. Fetch a date range (scraping)
USD → CHF rates for January 2025.
```bash
dotnet run --project OandaHistoryFetcher.Cli -- \
  --from-currency USD \
  --to-currency CHF \
  --start-date 2025-01-01 \
  --end-date 2025-01-31 \
  --mode scrape \
  --acknowledge-scraping \
  --output usd_chf_jan_2025.csv
```

#### 2. Fetch specific dates
BTC → CHF rates for a few dates in February.
```bash
dotnet run --project OandaHistoryFetcher.Cli -- \
  --from-currency BTC \
  --to-currency CHF \
  --dates "21.02.2025, 22.02.2025, 25.02.2025" \
  --mode scrape \
  --acknowledge-scraping
```

#### 3. Incremental / resumable run from a CSV job list
Process every non-`ok` row in `input.csv` (which can mix many currency pairs and dates) and write the full result to `result.csv`.
```bash
dotnet run --project OandaHistoryFetcher.Cli -- \
  --input-csv input.csv \
  --output result.csv \
  --mode scrape \
  --acknowledge-scraping
```
Example `input.csv`:
```
date,from_currency,to_currency,amount,rate,source,status,error_message
01.01.2026,BTC,CHF,1,,scrape,todo,
01.01.2026,ETH,CHF,1,,scrape,todo,
01.01.2026,USD,CHF,1,,scrape,todo,
```
Re-running the same command with the previous `result.csv` as input will skip rows already marked `ok` and only retry the rest.

#### 4. Tuning delay, retries, and logging
```bash
dotnet run --project OandaHistoryFetcher.Cli -- \
  --from-currency EUR --to-currency CHF \
  --start-date 2025-01-01 --end-date 2025-01-05 \
  --mode scrape --acknowledge-scraping \
  --delay-ms 2000 --max-retries 5 --log-level debug
```

#### 5. API mode (not yet implemented)
```bash
export OANDA_API_KEY="your-api-key"
dotnet run --project OandaHistoryFetcher.Cli -- \
  --from-currency EUR --to-currency CHF \
  --start-date 2025-01-01 --end-date 2025-01-05 \
  --mode api
```
> This currently fails with a "not implemented" error. The OANDA v3 endpoint (e.g. `instruments/{from}_{to}/candles?granularity=D`) still needs to be wired up in `OandaApiRateProvider`.

## Supported currencies

Any currency pair available on the OANDA platform. Pairs that have been used with this tool include:

- **Fiat**: USD, EUR, CHF, GBP, JPY, …
- **Crypto / tokens**: BTC, ETH, DOT, KSM, PAXG, USDC, USDT, and others.

Availability for a given symbol and date depends on OANDA's converter.

## Disclaimer

When using `scrape` mode you are interacting with the OANDA public website; please comply with their [Terms of Use](https://www.oanda.com/). This tool is for educational and personal use only and makes no guarantee of rate accuracy — verify values before using them for any official purpose, including tax filings.
