using System;
using System.Collections.Generic;
using System.Globalization;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;

namespace MarketAnalysisEngine.Functions
{
    public static class IngestInsiderTrading
    {
        private static readonly HttpClient HttpClient = new HttpClient();

        [Function("IngestInsiderTrading")]
        public static async Task Run(
            // twice per hour
            [TimerTrigger("0 10,40 13-21 * * 1-5", RunOnStartup = false)]
            TimerInfo timer,
            FunctionContext context)
        {
            var log = context.GetLogger("IngestInsiderTrading");

            var fmpApiKey =
                Environment.GetEnvironmentVariable("FMP_API_KEY");

            var fmpBaseUrl =
                Environment.GetEnvironmentVariable("FMP_BASE_URL");

            var supabaseUrl =
                Environment.GetEnvironmentVariable("SUPABASE_API_URL");

            var supabaseKey =
                Environment.GetEnvironmentVariable("SUPABASE_SERVICE_ROLE_KEY");

            if (string.IsNullOrWhiteSpace(fmpApiKey) ||
                string.IsNullOrWhiteSpace(supabaseUrl) ||
                string.IsNullOrWhiteSpace(supabaseKey))
            {
                log.LogError(
                    "Missing required environment variables. " +
                    "Required: FMP_API_KEY, SUPABASE_API_URL, SUPABASE_SERVICE_ROLE_KEY");

                return;
            }

            if (string.IsNullOrWhiteSpace(fmpBaseUrl))
            {
                fmpBaseUrl = "https://financialmodelingprep.com/stable";
            }

            fmpBaseUrl = NormalizeFmpBaseUrl(fmpBaseUrl);
            supabaseUrl = supabaseUrl.TrimEnd('/');

            try
            {
                var easternNow = GetEasternTime();
                var date = easternNow.ToString("yyyy-MM-dd");

                log.LogInformation(
                    "Starting insider trading ingestion for {Date}.",
                    date);

                var url =
                    $"{fmpBaseUrl}/insider-trading/latest" +
                    $"?date={Uri.EscapeDataString(date)}" +
                    $"&apikey={Uri.EscapeDataString(fmpApiKey)}";

                var response = await HttpClient.GetAsync(url);
                var responseBody =
                    await response.Content.ReadAsStringAsync();

                if (!response.IsSuccessStatusCode)
                {
                    log.LogError(
                        "FMP insider trading request failed. Status: {Status}. Body: {Body}",
                        response.StatusCode,
                        responseBody);

                    return;
                }

                using var document =
                    JsonDocument.Parse(responseBody);

                if (document.RootElement.ValueKind != JsonValueKind.Array)
                {
                    log.LogWarning(
                        "Unexpected FMP insider trading response. Expected JSON array. Body: {Body}",
                        responseBody);

                    return;
                }

                var rows =
                    new List<Dictionary<string, object?>>();

                foreach (var item in document.RootElement.EnumerateArray())
                {
                    var symbol =
                        GetString(item, "symbol")?
                            .Trim()
                            .ToUpperInvariant();

                    if (string.IsNullOrWhiteSpace(symbol))
                    {
                        continue;
                    }

                    var filingDate =
                        GetString(item, "filingDate");

                    var transactionDate =
                        GetString(item, "transactionDate");

                    var reportingCik =
                        GetString(item, "reportingCik");

                    var companyCik =
                        GetString(item, "companyCik");

                    var transactionType =
                        GetString(item, "transactionType");

                    var reportingName =
                        GetString(item, "reportingName");

                    var typeOfOwner =
                        GetString(item, "typeOfOwner");

                    var acquisitionOrDisposition =
                        GetString(item, "acquisitionOrDisposition");

                    var directOrIndirect =
                        GetString(item, "directOrIndirect");

                    var formType =
                        GetString(item, "formType");

                    var securityName =
                        GetString(item, "securityName");

                    var secUrl =
                        GetString(item, "url");

                    var securitiesOwned =
                        GetDecimal(item, "securitiesOwned");

                    var securitiesTransacted =
                        GetDecimal(item, "securitiesTransacted");

                    var price =
                        GetDecimal(item, "price");

                    // Split values like:
                    // P-Purchase -> P / Purchase
                    // S-Sale     -> S / Sale
                    string? transactionCode = null;
                    string? transactionDescription = null;

                    if (!string.IsNullOrWhiteSpace(transactionType))
                    {
                        var parts = transactionType.Split('-', 2);

                        transactionCode = parts[0];

                        if (parts.Length > 1)
                        {
                            transactionDescription = parts[1];
                        }
                    }

                    /*
                     * Build a stable transaction identity.
                     * This prevents duplicate inserts when the timer
                     * sees the same FMP transaction again later.
                     */
                    var sourceHash =
                        CreateSourceHash(
                            symbol,
                            filingDate,
                            transactionDate,
                            reportingCik,
                            companyCik,
                            transactionType,
                            reportingName,
                            securitiesTransacted,
                            price,
                            securityName,
                            secUrl);

                    rows.Add(
                        new Dictionary<string, object?>
                        {
                            ["symbol"] = symbol,

                            ["filing_date"] =
                                NullIfEmpty(filingDate),

                            ["transaction_date"] =
                                NullIfEmpty(transactionDate),

                            ["reporting_cik"] =
                                NullIfEmpty(reportingCik),

                            ["company_cik"] =
                                NullIfEmpty(companyCik),

                            ["transaction_type"] =
                                NullIfEmpty(transactionType),

                            ["transaction_code"] =
                                NullIfEmpty(transactionCode),

                            ["transaction_description"] =
                                NullIfEmpty(transactionDescription),

                            ["securities_owned"] =
                                securitiesOwned,

                            ["reporting_name"] =
                                NullIfEmpty(reportingName),

                            ["type_of_owner"] =
                                NullIfEmpty(typeOfOwner),

                            ["acquisition_or_disposition"] =
                                NullIfEmpty(acquisitionOrDisposition),

                            ["direct_or_indirect"] =
                                NullIfEmpty(directOrIndirect),

                            ["form_type"] =
                                NullIfEmpty(formType),

                            ["securities_transacted"] =
                                securitiesTransacted,

                            ["price"] =
                                price,

                            ["security_name"] =
                                NullIfEmpty(securityName),

                            ["url"] =
                                NullIfEmpty(secUrl),

                            ["provider"] =
                                "fmp_insider_trading",

                            ["source_hash"] =
                                sourceHash,

                            ["raw_payload"] =
                                item.Clone()
                        });
                }

                if (rows.Count == 0)
                {
                    log.LogInformation(
                        "No insider trading records returned for {Date}.",
                        date);

                    return;
                }

                var insertUrl =
                    $"{supabaseUrl}/rest/v1/insider_trades_raw" +
                    "?on_conflict=source_hash";

                await SupabasePost(
                    insertUrl,
                    supabaseKey,
                    rows);

                log.LogInformation(
                    "Processed {Count} insider trading records for {Date}.",
                    rows.Count,
                    date);
            }
            catch (Exception ex)
            {
                log.LogError(
                    ex,
                    "Unhandled exception in IngestInsiderTrading.");
            }
        }


        // ------------------------------------------------------------
        // Supabase
        // ------------------------------------------------------------

        private static async Task SupabasePost(
            string url,
            string supabaseKey,
            object payload)
        {
            var json =
                JsonSerializer.Serialize(payload);

            using var request =
                new HttpRequestMessage(
                    HttpMethod.Post,
                    url);

            request.Headers.Add(
                "apikey",
                supabaseKey);

            request.Headers.Add(
                "Authorization",
                $"Bearer {supabaseKey}");

            request.Headers.Add(
                "Prefer",
                "resolution=ignore-duplicates,return=minimal");

            request.Content =
                new StringContent(
                    json,
                    Encoding.UTF8,
                    "application/json");

            var response =
                await HttpClient.SendAsync(request);

            var body =
                await response.Content.ReadAsStringAsync();

            if (!response.IsSuccessStatusCode)
            {
                throw new Exception(
                    $"Supabase POST failed: " +
                    $"{response.StatusCode} {body}");
            }
        }


        // ------------------------------------------------------------
        // Hash / deduplication
        // ------------------------------------------------------------

        private static string CreateSourceHash(
            string? symbol,
            string? filingDate,
            string? transactionDate,
            string? reportingCik,
            string? companyCik,
            string? transactionType,
            string? reportingName,
            decimal? securitiesTransacted,
            decimal? price,
            string? securityName,
            string? url)
        {
            var identity =
                string.Join(
                    "|",
                    symbol ?? "",
                    filingDate ?? "",
                    transactionDate ?? "",
                    reportingCik ?? "",
                    companyCik ?? "",
                    transactionType ?? "",
                    reportingName ?? "",
                    securitiesTransacted?.ToString(
                        CultureInfo.InvariantCulture) ?? "",
                    price?.ToString(
                        CultureInfo.InvariantCulture) ?? "",
                    securityName ?? "",
                    url ?? "");

            var bytes =
                SHA256.HashData(
                    Encoding.UTF8.GetBytes(identity));

            return Convert
                .ToHexString(bytes)
                .ToLowerInvariant();
        }


        // ------------------------------------------------------------
        // JSON helpers
        // ------------------------------------------------------------

        private static string? GetString(
            JsonElement item,
            string propertyName)
        {
            if (!item.TryGetProperty(
                    propertyName,
                    out var property))
            {
                return null;
            }

            if (property.ValueKind ==
                JsonValueKind.Null)
            {
                return null;
            }

            if (property.ValueKind ==
                JsonValueKind.String)
            {
                return property.GetString();
            }

            return property.ToString();
        }


        private static decimal? GetDecimal(
            JsonElement item,
            string propertyName)
        {
            if (!item.TryGetProperty(
                    propertyName,
                    out var property))
            {
                return null;
            }

            if (property.ValueKind ==
                JsonValueKind.Null)
            {
                return null;
            }

            if (property.ValueKind ==
                JsonValueKind.Number &&
                property.TryGetDecimal(out var value))
            {
                return value;
            }

            if (property.ValueKind ==
                JsonValueKind.String &&
                decimal.TryParse(
                    property.GetString(),
                    NumberStyles.Any,
                    CultureInfo.InvariantCulture,
                    out value))
            {
                return value;
            }

            return null;
        }


        private static object? NullIfEmpty(
            string? value)
        {
            return string.IsNullOrWhiteSpace(value)
                ? null
                : value;
        }


        // ------------------------------------------------------------
        // Time / URL helpers
        // ------------------------------------------------------------

        private static DateTime GetEasternTime()
        {
            TimeZoneInfo eastern;

            try
            {
                // Linux / Azure Functions
                eastern =
                    TimeZoneInfo.FindSystemTimeZoneById(
                        "America/New_York");
            }
            catch
            {
                // Windows fallback
                eastern =
                    TimeZoneInfo.FindSystemTimeZoneById(
                        "Eastern Standard Time");
            }

            return TimeZoneInfo.ConvertTimeFromUtc(
                DateTime.UtcNow,
                eastern);
        }


        private static string NormalizeFmpBaseUrl(
            string baseUrl)
        {
            baseUrl =
                baseUrl.TrimEnd('/');

            if (!baseUrl.EndsWith(
                    "/stable",
                    StringComparison.OrdinalIgnoreCase))
            {
                baseUrl += "/stable";
            }

            return baseUrl;
        }
    }
}