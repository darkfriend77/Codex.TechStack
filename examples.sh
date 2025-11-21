# Example: Fetching BTC to CHF rates for 2024

# Basic scraping mode example (short date range for testing)
dotnet run --project OandaHistoryFetcher.Cli \
  --from-currency BTC \
  --to-currency CHF \
  --start-date 2024-01-01 \
  --end-date 2024-01-10 \
  --amount 1 \
  --mode scrape \
  --delay-ms 1500 \
  --output btc_chf_jan_2024.csv \
  --acknowledge-scraping

# Full year with API mode (if you have an API key)
# export OANDA_API_KEY=your_key_here
# dotnet run --project OandaHistoryFetcher.Cli \
#   --from-currency BTC \
#   --to-currency CHF \
#   --start-date 2024-01-01 \
#   --end-date 2024-12-31 \
#   --amount 1 \
#   --mode api \
#   --output btc_chf_2024.csv

# Auto mode - uses API if OANDA_API_KEY is set, otherwise scraping
# dotnet run --project OandaHistoryFetcher.Cli \
#   --from-currency EUR \
#   --to-currency USD \
#   --start-date 2024-06-01 \
#   --end-date 2024-06-30 \
#   --mode auto \
#   --output eur_usd_june_2024.csv

# With debug logging
# dotnet run --project OandaHistoryFetcher.Cli \
#   --from-currency BTC \
#   --to-currency CHF \
#   --start-date 2024-01-01 \
#   --end-date 2024-01-03 \
#   --mode scrape \
#   --log-level debug \
#   --acknowledge-scraping
