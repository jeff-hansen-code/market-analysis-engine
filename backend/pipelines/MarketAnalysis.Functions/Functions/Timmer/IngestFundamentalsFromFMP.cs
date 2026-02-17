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

        [Function("IngestFundamentalsFromFMP")]
        public static async Task Run(
            // Pick whatever cadence you want; this mirrors your other fundamentals timer pattern.
            // Example: every 2 hours during weekdays (UTC hours 14-22 every 2 hours at :15)
            [TimerTrigger("0 15 14-22/2 * * 1-5", RunOnStartup = false)] TimerInfo timer,
            FunctionContext context)
        {
            var log = context.GetLogger("IngestFundamentalsFromFMP");

            var fmpApiKey = Environment.GetEnvironmentVariable("FMP_API_KEY");
            var supabaseUrl = Environment.GetEnvironmentVariable("SUPABASE_API_URL");
            var supabaseKey = Environment.GetEnvironmentVariable("SUPABASE_SERVICE_ROLE_KEY");

            if (string.IsNullOrWhiteSpace(fmpApiKey) ||
                string.IsNullOrWhiteSpace(supabaseUrl) ||
                string.IsNullOrWhiteSpace(supabaseKey))
            {
                log.LogError("Missing env vars: FMP_API_KEY, SUPABASE_API_URL, SUPABASE_SERVICE_ROLE_KEY");
                return;
            }

            supabaseUrl = supabaseUrl.TrimEnd('/');

            // knobs
            const int maxSymbolsPerRun = 25;                 // safe default for fundamentals
            const string period = "quarter";
            const string statementType = "income_statement";
            const string provider = "fmp_income_statement";

            // “recent enough” cutoff (quarterly): if you want to skip truly old rows
            var recencyCutoff = DateTime.UtcNow.AddDays(-120).ToString("yyyy-MM-dd");

            try
            {
                // 1) Pull allowed symbols from the allowlist table
                //    PostgREST: /rest/v1/fmp_free_fundamentals_allowed?select=symbol&order=symbol.asc&limit=25
                var allowUrl =
                    $"{supabaseUrl}/rest/v1/fmp_free_fundamentals_allowed" +
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
                    log.LogInformation("No symbols found in fmp_free_fundamentals_allowed.");
                    return;
                }

                // 2) Check which already have recent fundamentals in fundamentals_raw
                var symbolList = string.Join(",", symbols.Select(Uri.EscapeDataString));

                // Add as_of=gte cutoff so “old historical” rows don’t block refresh forever
                var fundamentalsCheckUrl =
                    $"{supabaseUrl}/rest/v1/fundamentals_raw" +
                    $"?select=symbol" +
                    $"&provider=eq.{provider}" +
                    $"&statement_type=eq.{statementType}" +
                    $"&period=eq.{period}" +
                    $"&as_of=gte.{Uri.EscapeDataString(recencyCutoff)}" +
                    $"&symbol=in.({symbolList})" +
                    $"&limit=5000";

                var existingJson = await SupabaseGet(fundamentalsCheckUrl, supabaseKey);
                using var existingDoc = JsonDocument.Parse(existingJson);

                var existing = existingDoc.RootElement.EnumerateArray()
                    .Select(x => x.TryGetProperty("symbol", out var s) ? s.GetString() : null)
                    .Where(s => !string.IsNullOrWhiteSpace(s))
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);

                var toFetch = symbols.Where(s => !existing.Contains(s)).ToList();

                if (toFetch.Count == 0)
                {
                    log.LogInformation("All {Count} allowlisted symbols already have recent fundamentals.", symbols.Count);
                    return;
                }

                log.LogInformation("Fetching income statements for {Count} symbols: {Symbols}",
                    toFetch.Count, string.Join(",", toFetch));

                // 3) For each symbol, call FMP and insert rows into fundamentals_raw
                foreach (var sym in toFetch)
                {
                    var fmpUrl =
                        $"https://financialmodelingprep.com/stable/income-statement" +
                        $"?symbol={Uri.EscapeDataString(sym)}" +
                        $"&period={period}" +
                        $"&apikey={fmpApiKey}";

                    var fmpResp = await HttpClient.GetAsync(fmpUrl);

                    if (!fmpResp.IsSuccessStatusCode)
                    {
                        var body = await fmpResp.Content.ReadAsStringAsync();
                        log.LogWarning("FMP income-statement failed for {Symbol}: {Status} {Body}", sym, fmpResp.StatusCode, body);
                        continue;
                    }

                    var fmpJson = await fmpResp.Content.ReadAsStringAsync();
                    using var fmpDoc = JsonDocument.Parse(fmpJson);

                    if (fmpDoc.RootElement.ValueKind != JsonValueKind.Array)
                    {
                        log.LogWarning("Unexpected FMP income-statement shape for {Symbol}. Raw: {Json}", sym, fmpJson);
                        continue;
                    }

                    var rows = new List<Dictionary<string, object?>>();

                    foreach (var item in fmpDoc.RootElement.EnumerateArray())
                    {
                        if (!item.TryGetProperty("date", out var dateEl) || dateEl.ValueKind != JsonValueKind.String)
                            continue;

                        var asOfDate = dateEl.GetString();
                        if (string.IsNullOrWhiteSpace(asOfDate))
                            continue;

                        rows.Add(new Dictionary<string, object?>
                        {
                            ["symbol"] = sym,
                            ["provider"] = provider,
                            ["statement_type"] = statementType,
                            ["period"] = period,
                            ["as_of"] = asOfDate,          // YYYY-MM-DD
                            ["raw_payload"] = item         // JsonElement is OK; serializer will handle it
                        });
                    }

                    if (rows.Count == 0)
                    {
                        log.LogInformation("No income-statement rows for {Symbol}.", sym);
                        continue;
                    }

                    var insertUrl =
                        $"{supabaseUrl}/rest/v1/fundamentals_raw" +
                        $"?on_conflict=symbol,provider,statement_type,period,as_of";

                    await SupabasePost(insertUrl, supabaseKey, rows, preferResolutionIgnoreDuplicates: true);

                    log.LogInformation("Inserted {Count} fundamentals rows for {Symbol}.", rows.Count, sym);
                }
            }
            catch (Exception ex)
            {
                log.LogError(ex, "Unhandled exception in IngestFundamentalsFromFMP.");
            }
        }

        private static async Task<string> SupabaseGet(string url, string supabaseKey)
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

        private static async Task SupabasePost(string url, string supabaseKey, object payload, bool preferResolutionIgnoreDuplicates)
        {
            var json = JsonSerializer.Serialize(payload, new JsonSerializerOptions
            {
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase
            });

            var req = new HttpRequestMessage(HttpMethod.Post, url);
            req.Headers.Add("apikey", supabaseKey);
            req.Headers.Add("Authorization", $"Bearer {supabaseKey}");
            req.Headers.Add("Prefer", preferResolutionIgnoreDuplicates
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
