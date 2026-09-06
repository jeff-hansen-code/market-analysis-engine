using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;

namespace MarketAnalysisEngine.Functions
{
    public static class IngestFundamentalsFromFMP
    {
        private static readonly HttpClient HttpClient = new HttpClient();

        private sealed class StatementConfig
        {
            public StatementConfig(string endpoint, string provider, string statementType)
            {
                Endpoint = endpoint;
                Provider = provider;
                StatementType = statementType;
            }

            public string Endpoint { get; }
            public string Provider { get; }
            public string StatementType { get; }
        }

        [Function("IngestFundamentalsFromFMP")]
        public static async Task Run(
            // Every 2 hours during weekdays, UTC hours 14-22 at :15
            [TimerTrigger("0 15 14-22/2 * * 1-5", RunOnStartup = false)] TimerInfo timer,
            FunctionContext context)
        {
            var log = context.GetLogger("IngestFundamentalsFromFMP");

            var fmpApiKey = Environment.GetEnvironmentVariable("FMP_API_KEY");
            var fmpBaseUrl = Environment.GetEnvironmentVariable("FMP_BASE_URL");
            var supabaseUrl = Environment.GetEnvironmentVariable("SUPABASE_API_URL");
            var supabaseKey = Environment.GetEnvironmentVariable("SUPABASE_SERVICE_ROLE_KEY");

            if (string.IsNullOrWhiteSpace(fmpApiKey) ||
                string.IsNullOrWhiteSpace(fmpBaseUrl) ||
                string.IsNullOrWhiteSpace(supabaseUrl) ||
                string.IsNullOrWhiteSpace(supabaseKey))
            {
                log.LogError(
                    "Missing env vars: FMP_API_KEY, FMP_BASE_URL, SUPABASE_API_URL, SUPABASE_SERVICE_ROLE_KEY");
                return;
            }

            fmpBaseUrl = fmpBaseUrl.TrimEnd('/') + "/";
            supabaseUrl = supabaseUrl.TrimEnd('/');

            const int maxSymbolsPerRun = 25;
            const string period = "quarter";

            // Each statement gets stored in fundamentals_raw with its own
            // provider + statement_type, but uses the same table/schema.
            var statements = new[]
            {
                new StatementConfig(
                    endpoint: "income-statement",
                    provider: "fmp_income_statement",
                    statementType: "income_statement"),

                new StatementConfig(
                    endpoint: "balance-sheet-statement",
                    provider: "fmp_balance_sheet_statement",
                    statementType: "balance_sheet"),

                new StatementConfig(
                    endpoint: "cash-flow-statement",
                    provider: "fmp_cash_flow_statement",
                    statementType: "cash_flow")
            };

            // Quarterly data: consider anything within roughly 120 days recent enough.
            var recencyCutoff = DateTime.UtcNow.AddDays(-120).ToString("yyyy-MM-dd");

            try
            {
                // 1) Pull symbols that have never been checked,
                //    or whose fundamentals haven't been checked in 120 days.
                var staleCutoff = DateTime.UtcNow
                    .AddDays(-120)
                    .ToString("o");

                var allowUrl =
                    $"{supabaseUrl}/rest/v1/fmp_free_tier_symbols" +
                    $"?select=symbol,last_fundamentals_checked_at" +
                    $"&or=(" +
                        $"last_fundamentals_checked_at.is.null," +
                        $"last_fundamentals_checked_at.lt.{Uri.EscapeDataString(staleCutoff)}" +
                    $")" +
                    $"&order=last_fundamentals_checked_at.asc.nullsfirst,symbol.asc" +
                    $"&limit={maxSymbolsPerRun}";

                var allowJson = await SupabaseGet(allowUrl, supabaseKey);
                using var allowDoc = JsonDocument.Parse(allowJson);

                var symbols = allowDoc.RootElement.EnumerateArray()
                    .Select(x => x.TryGetProperty("symbol", out var s) ? s.GetString() : null)
                    .Where(s => !string.IsNullOrWhiteSpace(s))
                    .Select(s => s!.Trim().ToUpperInvariant())
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();

                if (symbols.Count == 0)
                {
                    log.LogInformation("No symbols found in fmp_free_tier_symbols.");
                    return;
                }

                var symbolList = string.Join(",", symbols.Select(Uri.EscapeDataString));

                // 2) Check EACH statement type independently.
                //    Having a recent income statement should not prevent
                //    balance sheet or cash flow from being fetched.
                var missingByStatement =
                    new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);

                foreach (var statement in statements)
                {
                    var checkUrl =
                        $"{supabaseUrl}/rest/v1/fundamentals_raw" +
                        $"?select=symbol" +
                        $"&provider=eq.{Uri.EscapeDataString(statement.Provider)}" +
                        $"&statement_type=eq.{Uri.EscapeDataString(statement.StatementType)}" +
                        $"&period=eq.{period}" +
                        $"&as_of=gte.{Uri.EscapeDataString(recencyCutoff)}" +
                        $"&symbol=in.({symbolList})" +
                        $"&limit=5000";

                    var existingJson = await SupabaseGet(checkUrl, supabaseKey);
                    using var existingDoc = JsonDocument.Parse(existingJson);

                    var existingSymbols = existingDoc.RootElement.EnumerateArray()
                        .Select(x => x.TryGetProperty("symbol", out var s) ? s.GetString() : null)
                        .Where(s => !string.IsNullOrWhiteSpace(s))
                        .Select(s => s!)
                        .ToHashSet(StringComparer.OrdinalIgnoreCase);

                    var missingSymbols = symbols
                        .Where(s => !existingSymbols.Contains(s))
                        .ToHashSet(StringComparer.OrdinalIgnoreCase);

                    missingByStatement[statement.StatementType] = missingSymbols;

                    log.LogInformation(
                        "{StatementType}: {MissingCount} of {TotalCount} symbols need refresh.",
                        statement.StatementType,
                        missingSymbols.Count,
                        symbols.Count);
                }

                if (missingByStatement.Values.All(x => x.Count == 0))
                {
                    log.LogInformation(
                        "All {Count} allowlisted symbols already have recent income, balance sheet, and cash flow data.",
                        symbols.Count);
                    return;
                }

                // 3) Fetch only the statement types that are missing/stale.
                //
                // IMPORTANT:
                // - Successful FMP response with data       -> counts as successful check
                // - Successful FMP response with empty []   -> counts as successful check
                // - HTTP/API/timeout/processing failure     -> does NOT count as successful check
                //
                // We only update last_fundamentals_checked_at after all statement
                // types that needed checking for a symbol completed successfully.
                foreach (var sym in symbols)
                {
                    bool symbolHadFailure = false;
                    bool attemptedAtLeastOneStatement = false;

                    foreach (var statement in statements)
                    {
                        // This statement type already has sufficiently fresh data.
                        if (!missingByStatement[statement.StatementType].Contains(sym))
                            continue;

                        attemptedAtLeastOneStatement = true;

                        try
                        {
                            await FetchAndStoreStatement(
                                symbol: sym,
                                statement: statement,
                                period: period,
                                fmpBaseUrl: fmpBaseUrl,
                                fmpApiKey: fmpApiKey,
                                supabaseUrl: supabaseUrl,
                                supabaseKey: supabaseKey,
                                log: log);

                            // If FetchAndStoreStatement returns normally, we consider
                            // the FMP check successful.
                            //
                            // That includes:
                            //   1) rows returned and stored
                            //   2) valid HTTP response containing []
                        }
                        catch (Exception ex)
                        {
                            symbolHadFailure = true;

                            // One endpoint/symbol failure should not kill the entire run.
                            log.LogError(
                                ex,
                                "Failed processing {StatementType} for {Symbol}. " +
                                "The symbol will NOT be marked as successfully checked.",
                                statement.StatementType,
                                sym);
                        }
                    }

                    // ------------------------------------------------------------
                    // Mark the symbol checked ONLY if none of its required
                    // statement requests failed.
                    // ------------------------------------------------------------
                    if (!symbolHadFailure)
                    {
                        try
                        {
                            await MarkFundamentalsChecked(
                                supabaseUrl,
                                supabaseKey,
                                sym,
                                DateTime.UtcNow);

                            if (attemptedAtLeastOneStatement)
                            {
                                log.LogInformation(
                                    "Successfully completed fundamentals check for {Symbol}. " +
                                    "Updated last_fundamentals_checked_at.",
                                    sym);
                            }
                            else
                            {
                                // This can happen if our existing fundamentals data
                                // already satisfies all statement freshness checks.
                                // Updating the timestamp prevents this symbol from
                                // getting selected over and over unnecessarily.
                                log.LogInformation(
                                    "No fundamentals statements needed refreshing for {Symbol}. " +
                                    "Updated last_fundamentals_checked_at.",
                                    sym);
                            }
                        }
                        catch (Exception ex)
                        {
                            // The FMP work succeeded, but updating the tracking
                            // timestamp failed. Don't kill the whole Function run.
                            //
                            // The symbol may simply get picked again next time.
                            log.LogError(
                                ex,
                                "Fundamentals were processed for {Symbol}, but failed to update " +
                                "last_fundamentals_checked_at.",
                                sym);
                        }
                    }
                    else
                    {
                        log.LogWarning(
                            "Not updating last_fundamentals_checked_at for {Symbol} " +
                            "because at least one required fundamentals request failed.",
                            sym);
                    }
                }
            }
            catch (Exception ex)
            {
                log.LogError(ex, "Unhandled exception in IngestFundamentalsFromFMP.");
            }
        }


        private static async Task MarkFundamentalsChecked(
                                    string supabaseUrl,
                                    string supabaseKey,
                                    string symbol,
                                    DateTime checkedAt)
        {
            var url =
                $"{supabaseUrl}/rest/v1/fmp_free_tier_symbols" +
                $"?symbol=eq.{Uri.EscapeDataString(symbol)}";

            var payload = new Dictionary<string, object?>
            {
                ["last_fundamentals_checked_at"] = checkedAt
            };

            var json = JsonSerializer.Serialize(payload);

            var req = new HttpRequestMessage(
                HttpMethod.Patch,
                url);

            req.Headers.Add("apikey", supabaseKey);
            req.Headers.Add(
                "Authorization",
                $"Bearer {supabaseKey}");

            req.Headers.Add(
                "Prefer",
                "return=minimal");

            req.Content = new StringContent(
                json,
                Encoding.UTF8,
                "application/json");

            var resp = await HttpClient.SendAsync(req);
            var body = await resp.Content.ReadAsStringAsync();

            if (!resp.IsSuccessStatusCode)
            {
                throw new Exception(
                    $"Failed updating fundamentals check for {symbol}: " +
                    $"{resp.StatusCode} {body}");
            }
        }


        private static async Task FetchAndStoreStatement(
            string symbol,
            StatementConfig statement,
            string period,
            string fmpBaseUrl,
            string fmpApiKey,
            string supabaseUrl,
            string supabaseKey,
            ILogger log)
        {
            var fmpUrl =
                $"{fmpBaseUrl}{statement.Endpoint}" +
                $"?symbol={Uri.EscapeDataString(symbol)}" +
                $"&period={period}" +
                $"&apikey={Uri.EscapeDataString(fmpApiKey)}";

            // ------------------------------------------------------------
            // 1) Call FMP
            // ------------------------------------------------------------
            var fmpResp = await HttpClient.GetAsync(fmpUrl);

            var fmpJson = await fmpResp.Content.ReadAsStringAsync();

            // IMPORTANT:
            // HTTP/API failure must THROW so the outer loop knows
            // NOT to mark this symbol as successfully checked.
            if (!fmpResp.IsSuccessStatusCode)
            {
                log.LogWarning(
                    "FMP {Endpoint} failed for {Symbol}: {Status} {Body}",
                    statement.Endpoint,
                    symbol,
                    fmpResp.StatusCode,
                    fmpJson);

                throw new HttpRequestException(
                    $"FMP {statement.Endpoint} failed for {symbol}: " +
                    $"{(int)fmpResp.StatusCode} {fmpResp.StatusCode}");
            }

            // ------------------------------------------------------------
            // 2) Parse response
            // ------------------------------------------------------------
            using var fmpDoc = JsonDocument.Parse(fmpJson);

            // FMP should always return an array for these endpoints.
            // Anything else is considered a failed check.
            if (fmpDoc.RootElement.ValueKind != JsonValueKind.Array)
            {
                log.LogWarning(
                    "Unexpected FMP {Endpoint} response shape for {Symbol}. Raw: {Json}",
                    statement.Endpoint,
                    symbol,
                    fmpJson);

                throw new InvalidOperationException(
                    $"Unexpected FMP response shape for " +
                    $"{statement.Endpoint} / {symbol}.");
            }

            // ------------------------------------------------------------
            // 3) Empty [] is VALID.
            //
            // It means FMP successfully answered the request but there
            // simply isn't any data for this symbol / statement.
            //
            // Returning normally here tells the outer loop:
            // "Yes, this endpoint was successfully checked."
            // ------------------------------------------------------------
            if (fmpDoc.RootElement.GetArrayLength() == 0)
            {
                log.LogInformation(
                    "FMP returned no {StatementType} rows for {Symbol}. " +
                    "Request was successful.",
                    statement.StatementType,
                    symbol);

                return;
            }

            var rows = new List<Dictionary<string, object?>>();

            // ------------------------------------------------------------
            // 4) Convert FMP rows into fundamentals_raw rows
            // ------------------------------------------------------------
            foreach (var item in fmpDoc.RootElement.EnumerateArray())
            {
                if (!item.TryGetProperty("date", out var dateEl) ||
                    dateEl.ValueKind != JsonValueKind.String)
                {
                    log.LogWarning(
                        "Skipping {StatementType} row for {Symbol} because " +
                        "it does not contain a valid date.",
                        statement.StatementType,
                        symbol);

                    continue;
                }

                var asOfDate = dateEl.GetString();

                if (string.IsNullOrWhiteSpace(asOfDate))
                {
                    log.LogWarning(
                        "Skipping {StatementType} row for {Symbol} because " +
                        "date was empty.",
                        statement.StatementType,
                        symbol);

                    continue;
                }

                rows.Add(new Dictionary<string, object?>
                {
                    ["symbol"] = symbol,
                    ["provider"] = statement.Provider,
                    ["statement_type"] = statement.StatementType,
                    ["period"] = period,
                    ["as_of"] = asOfDate,
                    ["raw_payload"] = item
                });
            }

            // ------------------------------------------------------------
            // 5) FMP returned objects, but NONE were usable.
            //
            // This is different from [].
            //
            // We should NOT mark the symbol successfully checked because
            // something about the response shape/data was unexpected.
            // ------------------------------------------------------------
            if (rows.Count == 0)
            {
                log.LogWarning(
                    "FMP returned {StatementType} data for {Symbol}, " +
                    "but none of the rows contained a usable date.",
                    statement.StatementType,
                    symbol);

                throw new InvalidOperationException(
                    $"FMP returned unusable {statement.StatementType} " +
                    $"data for {symbol}.");
            }

            // ------------------------------------------------------------
            // 6) Store in Supabase
            //
            // Existing unique constraint:
            // symbol, provider, statement_type, period, as_of
            // ------------------------------------------------------------
            var insertUrl =
                $"{supabaseUrl}/rest/v1/fundamentals_raw" +
                $"?on_conflict=symbol,provider,statement_type,period,as_of";

            await SupabasePost(
                insertUrl,
                supabaseKey,
                rows,
                preferResolutionIgnoreDuplicates: true);

            log.LogInformation(
                "Inserted/processed {Count} {StatementType} rows for {Symbol}.",
                rows.Count,
                statement.StatementType,
                symbol);
        }
       
        private static async Task<string> SupabaseGet(
            string url,
            string supabaseKey)
        {
            var req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.Add("apikey", supabaseKey);
            req.Headers.Add("Authorization", $"Bearer {supabaseKey}");

            var resp = await HttpClient.SendAsync(req);
            var body = await resp.Content.ReadAsStringAsync();

            if (!resp.IsSuccessStatusCode)
                throw new Exception($"Supabase GET failed: {resp.StatusCode} {body}");

            return body;
        }

        private static async Task SupabasePost(
            string url,
            string supabaseKey,
            object payload,
            bool preferResolutionIgnoreDuplicates)
        {
            var json = JsonSerializer.Serialize(
                payload,
                new JsonSerializerOptions
                {
                    PropertyNamingPolicy = JsonNamingPolicy.CamelCase
                });

            var req = new HttpRequestMessage(HttpMethod.Post, url);
            req.Headers.Add("apikey", supabaseKey);
            req.Headers.Add("Authorization", $"Bearer {supabaseKey}");
            req.Headers.Add(
                "Prefer",
                preferResolutionIgnoreDuplicates
                    ? "resolution=ignore-duplicates,return=minimal"
                    : "return=minimal");

            req.Content = new StringContent(json, Encoding.UTF8, "application/json");

            var resp = await HttpClient.SendAsync(req);
            var body = await resp.Content.ReadAsStringAsync();

            if (!resp.IsSuccessStatusCode)
                throw new Exception($"Supabase POST failed: {resp.StatusCode} {body}");
        }
    }
}