# market-analysis-engine

A cloud-first **market data ingestion + lightweight analysis API** built around:

- **Azure Functions (.NET, timer + HTTP)**
- **Financial Modeling Prep (FMP)** for market data
- **Supabase (Postgres + REST/RPC)** as the data store and API surface for downstream analytics

> Current status: the core Azure Functions app deploys via **Terraform + Azure DevOps YAML pipeline**, and the functions are running in Azure.

---

## What this repo does today

### 1) Ingest “Top Traded / Most Active” symbols (FMP → Supabase)
A scheduled function calls FMP’s “most actives / top traded” feed and writes raw rows into Supabase:

- **Destination table:** `top_traded_raw`
- **Provider tag:** `fmp_most_actives`
- **Payload stored:** `raw_payload` (full JSON element)
- **Important columns written:** `symbol, name, price, change, change_percentage, exchange, volume, high, low, last_trade(null), provider, as_of, rank, raw_payload`

### 2) Ingest quarterly income-statement fundamentals (FMP → Supabase)
Two scheduled paths exist:

- **From an allowlist table** (for staying inside “free tier”/rate limits)
  - **Source table:** `fmp_free_fundamentals_allowed`
  - **Destination table:** `fundamentals_raw`

- **From recent Top Traded symbols**
  - Reads `top_traded_raw` and fetches fundamentals for the most recent symbols
  - **Destination table:** `fundamentals_raw`

Stored fundamentals are inserted as raw JSON rows (idempotent via `on_conflict` in the TopTraded-driven function):

- **Destination table:** `fundamentals_raw`
- **Provider tag:** `fmp_income_statement`
- **statement_type:** `income_statement`
- **period:** `quarter`
- **Important columns written:** `symbol, provider, statement_type, period, as_of, raw_payload`

### 3) Ingest latest prices in batches (Supabase → FMP → Supabase)
A high-frequency timer job:

1. Pulls eligible symbols from `fundamentals_raw` that haven’t been checked recently  
2. Calls FMP batch quote endpoint
3. Writes rows into `stocks_raw`
4. Marks `fundamentals_raw.last_price_checked_at` for the symbols processed

- **Destination table:** `stocks_raw`
- **Provider tag:** `fmp_batch_quote`
- **Market-hours gating:** runs every 2 minutes but skips outside **US market hours (America/New_York 9:30–16:00, Mon–Fri)** unless overridden.

### 4) HTTP “analysis” endpoint (Supabase RPC → computed features → response)
A single HTTP endpoint returns:

- recent quotes (via Supabase RPC `api_get_latest_quotes`)
- latest predictions (via Supabase RPC `api_get_latest_predictions`)
- computed features on the quote series (moving average, volatility, percent change)

**Route**
- `GET /api/analysis/{symbol}` (Function authorization level)

**Query params**
- `points` (default `200`): number of quote points to pull
- `window` (default `20`): rolling window for features
- `predictions` (default `25`): number of prediction rows
- `features` (default `ma,vol,pct_change`): comma-separated list

**Response shape (high level)**
```json
{
  "symbol": "AAPL",
  "quotes": [ /* enriched quote points */ ],
  "predictions": [ /* latest prediction rows */ ],
  "meta": { "points": 200, "window": 20, "features": ["ma","vol","pct_change"], "predLimit": 25 }
}
```

---

## Azure Functions in this codebase

| Function | Trigger | Schedule (cron) | Purpose |
|---|---|---:|---|
| `IngestTopTraded` | Timer | `0 45 14-20 * * 1-5` | Ingest Top Traded during market hours (UTC) |
| `IngestTopTradedClose` | Timer | `0 0 21 * * 1-5` | Ingest Top Traded at/after close (UTC) |
| `IngestFundamentalsFromTopTraded` | Timer | `0 15 14-22 * * 1-5` | Fetch quarterly income statements for recent Top Traded symbols |
| `IngestFundamentalsFromFMP` | Timer | `0 15 14-22/2 * * 1-5` | Fetch quarterly income statements for allowlisted symbols |
| `fmp_get_price` | Timer | `0 */2 * * * *` | Batch quotes → `stocks_raw`, market-hours gated |
| `Analysis` | HTTP GET | — | `/api/analysis/{symbol}` returns quotes + predictions + features |

> Notes on schedules: Azure Functions cron is evaluated in UTC. The `fmp_get_price` job additionally checks US market hours in **America/New_York** unless bypassed.

---

## Supabase dependencies

### Tables used
- `top_traded_raw`
- `fundamentals_raw`
- `stocks_raw`
- `fmp_free_fundamentals_allowed`

### RPC functions used by the HTTP endpoint
- `api_get_latest_quotes` (expects `{ symbol, limit }`)
- `api_get_latest_predictions` (expects `{ symbol, limit }`)

---

## Configuration (environment variables)

### Required for Supabase REST calls (most functions)
- `SUPABASE_API_URL` (e.g. `https://xxxx.supabase.co`)
- `SUPABASE_SERVICE_ROLE_KEY` (service role key; required for inserts/RPC)

### Required for Supabase direct DB connection (price ingestion)
- `SUPABASE_DB_URL` (Postgres connection string)

### Required for FMP
- `FMP_API_KEY`
- `FMP_BASE_URL` (optional override; defaults are handled in code)

### Optional tuning
- `FMP_BATCH_SIZE` (batch size for quotes)
- `FMP_MAX_SYMBOLS_PER_RUN` (cap per run)
- `BYPASS_MARKET_HOURS` (`true` to bypass market-hours gating)

---

## Running locally

1. Install prerequisites
   - .NET SDK matching the project (Functions isolated)
   - Azure Functions Core Tools

2. Set local settings  
   Create `local.settings.json` (do **not** commit it):
```json
{
  "IsEncrypted": false,
  "Values": {
    "AzureWebJobsStorage": "UseDevelopmentStorage=true",
    "FUNCTIONS_WORKER_RUNTIME": "dotnet-isolated",
    "SUPABASE_API_URL": "https://xxxx.supabase.co",
    "SUPABASE_SERVICE_ROLE_KEY": "your-service-role-key",
    "SUPABASE_DB_URL": "Host=...;Username=...;Password=...;Database=...;",
    "FMP_API_KEY": "your-fmp-key",
    "FMP_BASE_URL": "https://financialmodelingprep.com"
  }
}
```

3. Run the Functions app
```bash
func start
```

4. Call the HTTP endpoint (local)
```bash
curl "http://localhost:7071/api/analysis/AAPL?points=200&window=20&features=ma,vol,pct_change"
```

---

## Deployment

This project is deployed via:
- **Terraform** (infrastructure)
- **Azure DevOps YAML pipeline** (build + deploy)

Exact pipeline/infra details live in the `infra/` and pipeline files in this repo (update this section as those paths stabilize).

---

## Roadmap (near-term)

- Harden Supabase schemas (indexes, constraints, retention policies)
- Expand fundamentals coverage (balance sheet, cash flow, ratios)
- Add rate-limit backoff and richer observability
- Add automated tests around feature computation and Supabase RPC contracts
