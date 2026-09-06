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
    public static class IngestAnalystEstimatesFromFMP
    {
        private static readonly HttpClient HttpClient = new HttpClient();

        [Function("IngestAnalystEstimatesFromFMP")]
        public static async Task Run(
            // Once each weekday at 22:30 UTC.
            // This is after US regular market hours year-round.
            [TimerTrigger("0 30 22 * * 1-5", RunOnStartup = false)] TimerInfo timer,
            FunctionContext context)
        {
            var log = context.GetLogger("IngestAnalystEstimatesFromFMP");

            var fmpApiKey =
                Environment.GetEnvironmentVariable("FMP_API_KEY");

            var fmpBaseUrl =
                Environment.GetEnvironmentVariable("FMP_BASE_URL");

            var supabaseUrl =
                Environment.GetEnvironmentVariable("SUPABASE_API_URL");

            var supabaseKey =
                Environment.GetEnvironmentVariable("SUPABASE_SERVICE_ROLE_KEY");

            if (string.IsNullOrWhiteSpace(fmpApiKey) ||
                string.IsNullOrWhiteSpace(fmpBaseUrl) ||
                string.IsNullOrWhiteSpace(supabaseUrl) ||
                string.IsNullOrWhiteSpace(supabaseKey))
            {
                log.LogError(
                    "Missing env vars: FMP_API_KEY, FMP_BASE_URL, " +
                    "SUPABASE_API_URL, SUPABASE_SERVICE_ROLE_KEY");

                return;
            }

            fmpBaseUrl = fmpBaseUrl.TrimEnd('/') + "/";
            supabaseUrl = supabaseUrl.TrimEnd('/');

            // Analyst estimates move much more slowly than price data.
            // 20/day gives us a complete rotation through ~87 symbols
            // in roughly 5 weekdays.
            const int maxSymbolsPerRun = 20;
            const string period = "annual";

            var nowUtc = DateTime.UtcNow;

            // Prevent accidental reruns from hammering the same symbols.
            // Anything checked in the last 3 days isn't eligible.
            var staleCutoff = nowUtc.AddDays(-3).ToString("o");

            try
            {
                // ========================================================
                // 1. Get the oldest / never-checked free-tier symbols
                // ========================================================

                var allowUrl =
                    $"{supabaseUrl}/rest/v1/fmp_free_tier_symbols" +
                    $"?select=symbol,last_analyst_estimates_checked_at" +
                    $"&or=(" +
                        $"last_analyst_estimates_checked_at.is.null," +
                        $"last_analyst_estimates_checked_at.lt.{Uri.EscapeDataString(staleCutoff)}" +
                    $")" +
                    $"&order=last_analyst_estimates_checked_at.asc.nullsfirst,symbol.asc" +
                    $"&limit={maxSymbolsPerRun}";

                var allowJson =
                    await SupabaseGet(allowUrl, supabaseKey);

                using var allowDoc =
                    JsonDocument.Parse(allowJson);

                if (allowDoc.RootElement.ValueKind != JsonValueKind.Array)
                {
                    log.LogWarning(
                        "Unexpected response from fmp_free_tier_symbols.");

                    return;
                }

                var symbols = allowDoc.RootElement
                    .EnumerateArray()
                    .Select(x =>
                        x.TryGetProperty("symbol", out var s)
                            ? s.GetString()
                            : null)
                    .Where(s => !string.IsNullOrWhiteSpace(s))
                    .Select(s => s!.Trim().ToUpperInvariant())
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .Take(maxSymbolsPerRun)
                    .ToList();

                if (symbols.Count == 0)
                {
                    log.LogInformation(
                        "No analyst-estimate symbols currently need refreshing.");

                    return;
                }

                log.LogInformation(
                    "Refreshing analyst estimates for {Count} symbols: {Symbols}",
                    symbols.Count,
                    string.Join(",", symbols));


                // ========================================================
                // 2. Process each symbol
                // ========================================================

                int totalRowsInserted = 0;
                int successfulSymbols = 0;

                foreach (var sym in symbols)
                {
                    var fetchedAt = DateTime.UtcNow;

                    // Exact FMP endpoint you supplied:
                    //
                    // /stable/analyst-estimates
                    // ?symbol=AAPL
                    // &period=annual
                    // &apikey=...
                    //
                    var fmpUrl =
                        $"{fmpBaseUrl}analyst-estimates" +
                        $"?symbol={Uri.EscapeDataString(sym)}" +
                        $"&period={period}" +
                        $"&apikey={Uri.EscapeDataString(fmpApiKey)}";

                    log.LogInformation(
                        "Requesting analyst estimates for {Symbol}.",
                        sym);

                    HttpResponseMessage fmpResp;

                    try
                    {
                        fmpResp =
                            await HttpClient.GetAsync(fmpUrl);
                    }
                    catch (Exception ex)
                    {
                        log.LogWarning(
                            ex,
                            "HTTP error requesting analyst estimates for {Symbol}.",
                            sym);

                        continue;
                    }

                    var fmpJson =
                        await fmpResp.Content.ReadAsStringAsync();

                    if (!fmpResp.IsSuccessStatusCode)
                    {
                        log.LogWarning(
                            "FMP analyst-estimates failed for {Symbol}: " +
                            "{Status} {Body}",
                            sym,
                            fmpResp.StatusCode,
                            fmpJson);

                        continue;
                    }


                    // ====================================================
                    // 3. Parse FMP response
                    // ====================================================

                    using var fmpDoc =
                        JsonDocument.Parse(fmpJson);

                    if (fmpDoc.RootElement.ValueKind != JsonValueKind.Array)
                    {
                        log.LogWarning(
                            "Unexpected analyst-estimates response " +
                            "for {Symbol}. Raw: {Json}",
                            sym,
                            fmpJson);

                        continue;
                    }

                    var rows =
                        new List<Dictionary<string, object?>>();

                    foreach (var item in
                             fmpDoc.RootElement.EnumerateArray())
                    {
                        // --------------------------------------------
                        // Local helper methods
                        // --------------------------------------------

                        decimal? GetDecimal(string propertyName)
                        {
                            if (!item.TryGetProperty(
                                    propertyName,
                                    out var value))
                                return null;

                            if (value.ValueKind != JsonValueKind.Number)
                                return null;

                            return value.TryGetDecimal(out var result)
                                ? result
                                : null;
                        }

                        int? GetInt(string propertyName)
                        {
                            if (!item.TryGetProperty(
                                    propertyName,
                                    out var value))
                                return null;

                            if (value.ValueKind != JsonValueKind.Number)
                                return null;

                            return value.TryGetInt32(out var result)
                                ? result
                                : null;
                        }


                        // --------------------------------------------
                        // Date is the fiscal estimate date
                        // Example: 2030-09-27
                        // --------------------------------------------

                        if (!item.TryGetProperty(
                                "date",
                                out var dateEl) ||
                            dateEl.ValueKind != JsonValueKind.String)
                        {
                            continue;
                        }

                        var estimateDate =
                            dateEl.GetString();

                        if (string.IsNullOrWhiteSpace(estimateDate))
                            continue;


                        // --------------------------------------------
                        // Map FMP → Supabase
                        // --------------------------------------------

                        var row =
                            new Dictionary<string, object?>
                            {
                                ["symbol"] = sym,

                                ["estimate_date"] =
                                    estimateDate,

                                ["period"] =
                                    period,


                                // Revenue
                                ["revenue_low"] =
                                    GetDecimal("revenueLow"),

                                ["revenue_avg"] =
                                    GetDecimal("revenueAvg"),

                                ["revenue_high"] =
                                    GetDecimal("revenueHigh"),


                                // EBITDA
                                ["ebitda_low"] =
                                    GetDecimal("ebitdaLow"),

                                ["ebitda_avg"] =
                                    GetDecimal("ebitdaAvg"),

                                ["ebitda_high"] =
                                    GetDecimal("ebitdaHigh"),


                                // EBIT
                                ["ebit_low"] =
                                    GetDecimal("ebitLow"),

                                ["ebit_avg"] =
                                    GetDecimal("ebitAvg"),

                                ["ebit_high"] =
                                    GetDecimal("ebitHigh"),


                                // Net Income
                                ["net_income_low"] =
                                    GetDecimal("netIncomeLow"),

                                ["net_income_avg"] =
                                    GetDecimal("netIncomeAvg"),

                                ["net_income_high"] =
                                    GetDecimal("netIncomeHigh"),


                                // SG&A Expense
                                ["sga_expense_low"] =
                                    GetDecimal("sgaExpenseLow"),

                                ["sga_expense_avg"] =
                                    GetDecimal("sgaExpenseAvg"),

                                ["sga_expense_high"] =
                                    GetDecimal("sgaExpenseHigh"),


                                // EPS
                                ["eps_low"] =
                                    GetDecimal("epsLow"),

                                ["eps_avg"] =
                                    GetDecimal("epsAvg"),

                                ["eps_high"] =
                                    GetDecimal("epsHigh"),


                                // Analyst counts
                                ["num_analysts_revenue"] =
                                    GetInt("numAnalystsRevenue"),

                                ["num_analysts_eps"] =
                                    GetInt("numAnalystsEps"),


                                // Preserve everything FMP gave us
                                ["raw_json"] =
                                    item,


                                // VERY IMPORTANT:
                                // This identifies when WE observed
                                // this particular estimate snapshot.
                                ["fetched_at"] =
                                    fetchedAt
                            };

                        rows.Add(row);
                    }


                    // ====================================================
                    // 4. Store snapshot
                    // ====================================================

                    if (rows.Count > 0)
                    {
                        var insertUrl =
                            $"{supabaseUrl}" +
                            $"/rest/v1/analyst_estimates_raw";

                        await SupabasePost(
                            insertUrl,
                            supabaseKey,
                            rows);

                        totalRowsInserted += rows.Count;

                        log.LogInformation(
                            "Inserted {Count} analyst estimate rows " +
                            "for {Symbol}.",
                            rows.Count,
                            sym);
                    }
                    else
                    {
                        // Important:
                        // A valid HTTP response containing [] isn't an
                        // API failure. Mark it checked so we don't keep
                        // wasting calls on it every run.
                        log.LogInformation(
                            "FMP returned no analyst estimates for {Symbol}.",
                            sym);
                    }


                    // ====================================================
                    // 5. Mark symbol checked
                    // ====================================================

                    await MarkSymbolChecked(
                        supabaseUrl,
                        supabaseKey,
                        sym,
                        fetchedAt);

                    successfulSymbols++;


                    // Small throttle to be nice to FMP's free API.
                    await Task.Delay(250);
                }


                log.LogInformation(
                    "Analyst estimate run complete. " +
                    "{SuccessfulSymbols} symbols processed, " +
                    "{Rows} estimate rows inserted.",
                    successfulSymbols,
                    totalRowsInserted);
            }
            catch (Exception ex)
            {
                log.LogError(
                    ex,
                    "Unhandled exception in IngestAnalystEstimatesFromFMP.");
            }
        }


        // ================================================================
        // Supabase GET
        // ================================================================

        private static async Task<string> SupabaseGet(
            string url,
            string supabaseKey)
        {
            var req =
                new HttpRequestMessage(HttpMethod.Get, url);

            req.Headers.Add(
                "apikey",
                supabaseKey);

            req.Headers.Add(
                "Authorization",
                $"Bearer {supabaseKey}");

            var resp =
                await HttpClient.SendAsync(req);

            var body =
                await resp.Content.ReadAsStringAsync();

            if (!resp.IsSuccessStatusCode)
            {
                throw new Exception(
                    $"Supabase GET failed: " +
                    $"{resp.StatusCode} {body}");
            }

            return body;
        }


        // ================================================================
        // Supabase POST
        // ================================================================

        private static async Task SupabasePost(
            string url,
            string supabaseKey,
            object payload)
        {
            var json =
                JsonSerializer.Serialize(
                    payload,
                    new JsonSerializerOptions
                    {
                        PropertyNamingPolicy =
                            JsonNamingPolicy.CamelCase
                    });

            var req =
                new HttpRequestMessage(
                    HttpMethod.Post,
                    url);

            req.Headers.Add(
                "apikey",
                supabaseKey);

            req.Headers.Add(
                "Authorization",
                $"Bearer {supabaseKey}");

            req.Headers.Add(
                "Prefer",
                "return=minimal");

            req.Content =
                new StringContent(
                    json,
                    Encoding.UTF8,
                    "application/json");

            var resp =
                await HttpClient.SendAsync(req);

            var body =
                await resp.Content.ReadAsStringAsync();

            if (!resp.IsSuccessStatusCode)
            {
                throw new Exception(
                    $"Supabase POST failed: " +
                    $"{resp.StatusCode} {body}");
            }
        }


        // ================================================================
        // Update whitelist tracking timestamp
        // ================================================================

        private static async Task MarkSymbolChecked(
            string supabaseUrl,
            string supabaseKey,
            string symbol,
            DateTime fetchedAt)
        {
            var url =
                $"{supabaseUrl}" +
                $"/rest/v1/fmp_free_tier_symbols" +
                $"?symbol=eq.{Uri.EscapeDataString(symbol)}";

            var payload =
                new Dictionary<string, object?>
                {
                    ["last_analyst_estimates_checked_at"] =
                        fetchedAt
                };

            var json =
                JsonSerializer.Serialize(payload);

            var req =
                new HttpRequestMessage(
                    HttpMethod.Patch,
                    url);

            req.Headers.Add(
                "apikey",
                supabaseKey);

            req.Headers.Add(
                "Authorization",
                $"Bearer {supabaseKey}");

            req.Headers.Add(
                "Prefer",
                "return=minimal");

            req.Content =
                new StringContent(
                    json,
                    Encoding.UTF8,
                    "application/json");

            var resp =
                await HttpClient.SendAsync(req);

            var body =
                await resp.Content.ReadAsStringAsync();

            if (!resp.IsSuccessStatusCode)
            {
                throw new Exception(
                    $"Supabase PATCH failed for {symbol}: " +
                    $"{resp.StatusCode} {body}");
            }
        }
    }
}