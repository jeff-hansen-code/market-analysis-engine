using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;

namespace MarketEngine.Functions
{
    public static class Analysis
    {
        private static readonly HttpClient http = new HttpClient();
        private static readonly string? SUPABASE_API_URL = Environment.GetEnvironmentVariable("SUPABASE_API_URL");
        private static readonly string? SERVICE_ROLE = Environment.GetEnvironmentVariable("SUPABASE_SERVICE_ROLE_KEY");

        [Function("Analysis")]
        public static async Task<IActionResult> Run(
            [HttpTrigger(AuthorizationLevel.Function, "get", Route = "analysis/{symbol}")] HttpRequest req,
            string symbol,
            ILogger log)
        {
            if (string.IsNullOrWhiteSpace(SUPABASE_API_URL) || string.IsNullOrWhiteSpace(SERVICE_ROLE))
            {
                log.LogError("Missing env vars: SUPABASE_API_URL and/or SUPABASE_SERVICE_ROLE_KEY");
                return new StatusCodeResult(StatusCodes.Status500InternalServerError);
            }

            log.LogInformation("Running analysis for {Symbol}", symbol);

            int points = TryParse(req.Query["points"].FirstOrDefault(), 200);
            int window = TryParse(req.Query["window"].FirstOrDefault(), 20);
            int predLimit = TryParse(req.Query["predictions"].FirstOrDefault(), 25);

            string? featuresRaw = req.Query["features"].FirstOrDefault();
            var features = string.IsNullOrWhiteSpace(featuresRaw)
                ? new List<string> { "ma", "vol", "pct_change" }
                : featuresRaw.Split(',').Select(f => f.Trim()).Where(f => f.Length > 0).ToList();

            // 1) Pull quotes via RPC
            var quotes = await FetchLatestQuotes(symbol, points);
            var enriched = ComputeFeatures(quotes, features, window);

            // 2) Pull predictions via RPC
            var preds = await FetchPredictions(symbol, predLimit);

            var result = new
            {
                symbol = symbol.ToUpperInvariant(),
                quotes = enriched,
                predictions = preds,
                meta = new
                {
                    points,
                    window,
                    features,
                    predLimit
                }
            };

            return new JsonResult(result);
        }

        private static int TryParse(string? input, int fallback)
            => int.TryParse(input, out int val) ? val : fallback;

        private static async Task<List<QuotePoint>> FetchLatestQuotes(string symbol, int limit)
        {
            var url = $"{SUPABASE_API_URL!.TrimEnd('/')}/rest/v1/rpc/api_get_latest_quotes";

            var payload = new
            {
                symbol,
                limit
            };

            var req = new HttpRequestMessage(HttpMethod.Post, url);
            req.Headers.Add("apikey", SERVICE_ROLE);
            req.Headers.Add("Authorization", $"Bearer {SERVICE_ROLE}");
            req.Content = JsonContent.Create(payload);

            var resp = await http.SendAsync(req);
            resp.EnsureSuccessStatusCode();

            return (await resp.Content.ReadFromJsonAsync<List<QuotePoint>>()) ?? new List<QuotePoint>();
        }

        private static async Task<List<PredictionRecord>> FetchPredictions(string symbol, int limit)
        {
            var url = $"{SUPABASE_API_URL!.TrimEnd('/')}/rest/v1/rpc/api_get_latest_predictions";

            var payload = new
            {
                symbol,
                limit
            };

            var req = new HttpRequestMessage(HttpMethod.Post, url);
            req.Headers.Add("apikey", SERVICE_ROLE);
            req.Headers.Add("Authorization", $"Bearer {SERVICE_ROLE}");
            req.Content = JsonContent.Create(payload);

            var resp = await http.SendAsync(req);
            resp.EnsureSuccessStatusCode();

            return (await resp.Content.ReadFromJsonAsync<List<PredictionRecord>>()) ?? new List<PredictionRecord>();
        }

        // ===== Feature Computation =====

        private static List<FeaturePoint> ComputeFeatures(
            List<QuotePoint> raw,
            List<string> features,
            int window)
        {
            raw = raw.OrderBy(q => q.as_of_utc).ToList();

            List<FeaturePoint> output = new();

            for (int i = 0; i < raw.Count; i++)
            {
                var f = new FeaturePoint
                {
                    as_of_utc = raw[i].as_of_utc,
                    price = raw[i].price
                };

                if (features.Contains("ma") && i >= window - 1)
                {
                    f.sma = (double)(raw.Skip(i - (window - 1)).Take(window).Average(r => r.price));
                }

                if (features.Contains("vol") && i >= window - 1)
                {
                    var slice = raw.Skip(i - (window - 1)).Take(window).Select(r => r.price).ToList();
                    double avg = (double)slice.Average();
                    double variance = slice.Select(v => Math.Pow((double)v - avg, 2)).Average();
                    f.volatility = Math.Sqrt(variance);
                }

                if (features.Contains("pct_change") && i > 0 && raw[i - 1].price != 0)
                {
                    f.pct_change = (double)(raw[i].price - raw[i - 1].price) / (double)raw[i - 1].price;
                }

                output.Add(f);
            }

            return output.OrderByDescending(o => o.as_of_utc).ToList();
        }

        // ===== Models =====

        public class QuotePoint
        {
            public DateTime as_of_utc { get; set; }
            public decimal price { get; set; }
        }

        public class FeaturePoint : QuotePoint
        {
            public double? sma { get; set; }
            public double? pct_change { get; set; }
            public double? volatility { get; set; }
        }

        public class PredictionRecord
        {
            public long id { get; set; }
            public string? symbol { get; set; }
            public string? model_name { get; set; }
            public int horizon_years { get; set; }
            public decimal? expected_return { get; set; }
            public decimal? confidence { get; set; }
            public DateTime as_of_utc { get; set; }
            public long? input_snapshot_id { get; set; }
            public JsonElement extra_meta { get; set; }
            public DateTime created_at { get; set; }
        }
    }
}
