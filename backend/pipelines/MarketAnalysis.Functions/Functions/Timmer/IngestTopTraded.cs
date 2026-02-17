using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.Azure.Functions.Worker;

using Microsoft.Extensions.Logging;

namespace MarketAnalysisEngine.Functions
{
    public static class IngestTopTraded
    {
        private static readonly HttpClient HttpClient = new HttpClient();

        [Function("IngestTopTraded")]
        public static async Task Run(
            [TimerTrigger("0 0 14-22/2 * * 1-5", RunOnStartup = false)] TimerInfo myTimer, // Simplest (works, no timezone pain): run every 2 hours 14–22 UTC (≈ 8am–4pm Chicago depending on DST)That fires at 14:00, 16:00, 18:00, 20:00, 22:00 UTC Mon–Fri.
            FunctionContext context  )  //ILogger log)
        {
            var log = context.GetLogger("IngestTopTraded");
            var fmpApiKey  = Environment.GetEnvironmentVariable("FMP_API_KEY");
            var supabaseUrl = Environment.GetEnvironmentVariable("SUPABASE_API_URL");
            var supabaseKey = Environment.GetEnvironmentVariable("SUPABASE_SERVICE_ROLE_KEY");

            if (string.IsNullOrWhiteSpace(fmpApiKey) ||
                string.IsNullOrWhiteSpace(supabaseUrl) ||
                string.IsNullOrWhiteSpace(supabaseKey))
            {
                log.LogError("Missing required environment variables (FMP_API_KEY, SUPABASE_API_URL, SUPABASE_SERVICE_ROLE_KEY).");
                return;
            }

            try
            {
                var nowUtc = DateTime.UtcNow;

                // 1) Call FMP Top Traded / Most Actives
                var fmpUrl = $"https://financialmodelingprep.com/stable/most-actives?apikey={fmpApiKey}";
                log.LogInformation("Requesting Top Traded from {Url}", fmpUrl);

                var fmpResponse = await HttpClient.GetAsync(fmpUrl);

                if (!fmpResponse.IsSuccessStatusCode)
                {
                    var body = await fmpResponse.Content.ReadAsStringAsync();
                    log.LogError("FMP request failed: {Status} {Body}", fmpResponse.StatusCode, body);
                    return;
                }

                var json = await fmpResponse.Content.ReadAsStringAsync();
                using var doc = JsonDocument.Parse(json);

                if (doc.RootElement.ValueKind != JsonValueKind.Array)
                {
                    log.LogError("Unexpected FMP response shape (expected array). Raw: {Json}", json);
                    return;
                }

                // 2) Map FMP rows → Supabase rows
                var rows = new List<Dictionary<string, object>>();
                int rank = 1;

                foreach (var el in doc.RootElement.EnumerateArray())
                {
                    // Helper safely gets numeric properties if present:
                    decimal? GetDecimal(string name)
                        => el.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.Number
                            ? p.GetDecimal()
                            : (decimal?)null;

                    long? GetLong(string name)
                        => el.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.Number
                            ? p.GetInt64()
                            : (long?)null;

                    string GetString(string name)
                        => el.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.String
                            ? p.GetString()
                            : null;

                    var row = new Dictionary<string, object?>
                    {
                        ["symbol"]            = GetString("symbol"),
                        ["name"]              = GetString("name"),
                        ["price"]             = GetDecimal("price"),
                        ["change"]            = GetDecimal("change"),
                        ["change_percentage"] = GetDecimal("changesPercentage"),
                        ["exchange"]          = GetString("exchange"),
                        ["volume"]            = GetLong("volume"),
                        ["high"]              = GetDecimal("dayHigh"),
                        ["low"]               = GetDecimal("dayLow"),
                        // You may not have a last trade datetime field; leave null for now:
                        ["last_trade"]        = null,
                        ["provider"]          = "fmp_most_actives",
                        ["as_of"]             = nowUtc,
                        ["rank"]              = rank++,
                        ["raw_payload"]       = el // System.Text.Json.JsonElement; Supabase accepts this as JSON
                    };

                    // Basic sanity: must have a symbol
                    if (row["symbol"] is string s && !string.IsNullOrWhiteSpace(s))
                    {
                        rows.Add(row);
                    }
                }

                if (rows.Count == 0)
                {
                    log.LogWarning("No valid rows parsed from FMP Top Traded response.");
                    return;
                }

                // 3) POST to Supabase REST endpoint
                var supabaseEndpoint = $"{supabaseUrl.TrimEnd('/')}/rest/v1/top_traded_raw";
                log.LogInformation("Posting {Count} rows to Supabase {Endpoint}", rows.Count, supabaseEndpoint);

                var supabaseJson = JsonSerializer.Serialize(
                    rows,
                    new JsonSerializerOptions
                    {
                        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
                    });

                var supabaseRequest = new HttpRequestMessage(HttpMethod.Post, supabaseEndpoint)
                {
                    Content = new StringContent(supabaseJson, Encoding.UTF8, "application/json")
                };

                supabaseRequest.Headers.Add("apikey", supabaseKey);
                supabaseRequest.Headers.Add("Authorization", $"Bearer {supabaseKey}");
                supabaseRequest.Headers.Add("Prefer", "return=minimal");

                var supabaseResponse = await HttpClient.SendAsync(supabaseRequest);

                if (!supabaseResponse.IsSuccessStatusCode)
                {
                    var body = await supabaseResponse.Content.ReadAsStringAsync();
                    log.LogError("Supabase insert failed: {Status} {Body}", supabaseResponse.StatusCode, body);
                    return;
                }

                log.LogInformation("Successfully inserted {Count} Top Traded rows at {Time}", rows.Count, nowUtc);
            }
            catch (Exception ex)
            {
                log.LogError(ex, "Unhandled exception in IngestTopTraded.");
            }
        }
    }
}


/* 
Timer schedule "0 5 21 * * 1-5"

Runs at 21:05 UTC, Monday–Friday (adjust to whenever you want).

We don’t dedupe here. For backtesting/history, you want multiple days of snapshots.
Later we can add logic if you want to avoid double-inserts for the same as_of.
=======================================================
-------------------------------------------------------

Add a second function that:

Reads new symbols out of top_traded_raw,

Calls FMP fundamentals endpoint for those,
Writes into fundamentals_raw.

That would complete the “Top Traded → Fundamentals” flow.

 */