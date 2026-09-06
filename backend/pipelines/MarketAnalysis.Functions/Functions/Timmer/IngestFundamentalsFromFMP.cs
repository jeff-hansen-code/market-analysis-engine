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
                // 1) Pull symbols from the FMP free-plan allowlist.
                var allowUrl =
                    $"{supabaseUrl}/rest/v1/fmp_free_tier_symbols" +
                    $"?select=symbol" +
                    $"&order=symbol.asc" +
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
                foreach (var sym in symbols)
                {
                    foreach (var statement in statements)
                    {
                        if (!missingByStatement[statement.StatementType].Contains(sym))
                            continue;

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
                        }
                        catch (Exception ex)
                        {
                            // One endpoint/symbol failure should not kill the entire run.
                            log.LogError(
                                ex,
                                "Failed processing {StatementType} for {Symbol}. Continuing.",
                                statement.StatementType,
                                sym);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                log.LogError(ex, "Unhandled exception in IngestFundamentalsFromFMP.");
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

            var fmpResp = await HttpClient.GetAsync(fmpUrl);

            if (!fmpResp.IsSuccessStatusCode)
            {
                var body = await fmpResp.Content.ReadAsStringAsync();

                log.LogWarning(
                    "FMP {Endpoint} failed for {Symbol}: {Status} {Body}",
                    statement.Endpoint,
                    symbol,
                    fmpResp.StatusCode,
                    body);

                return;
            }

            var fmpJson = await fmpResp.Content.ReadAsStringAsync();
            using var fmpDoc = JsonDocument.Parse(fmpJson);

            if (fmpDoc.RootElement.ValueKind != JsonValueKind.Array)
            {
                log.LogWarning(
                    "Unexpected FMP {Endpoint} response shape for {Symbol}. Raw: {Json}",
                    statement.Endpoint,
                    symbol,
                    fmpJson);

                return;
            }

            var rows = new List<Dictionary<string, object?>>();

            foreach (var item in fmpDoc.RootElement.EnumerateArray())
            {
                if (!item.TryGetProperty("date", out var dateEl) ||
                    dateEl.ValueKind != JsonValueKind.String)
                {
                    continue;
                }

                var asOfDate = dateEl.GetString();

                if (string.IsNullOrWhiteSpace(asOfDate))
                    continue;

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

            if (rows.Count == 0)
            {
                log.LogInformation(
                    "No {StatementType} rows returned for {Symbol}.",
                    statement.StatementType,
                    symbol);

                return;
            }

            // Existing unique constraint:
            // symbol, provider, statement_type, period, as_of
            var insertUrl =
                $"{supabaseUrl}/rest/v1/fundamentals_raw" +
                $"?on_conflict=symbol,provider,statement_type,period,as_of";

            await SupabasePost(
                insertUrl,
                supabaseKey,
                rows,
                preferResolutionIgnoreDuplicates: true);

            log.LogInformation(
                "Inserted {Count} {StatementType} rows for {Symbol}.",
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