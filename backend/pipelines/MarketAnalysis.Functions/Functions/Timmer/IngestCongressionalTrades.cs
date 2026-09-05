using System;
using System.Collections.Generic;
using System.Globalization;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;

namespace MarketAnalysisEngine.Functions
{
    public static class IngestCongressionalTrades
    {
        private static readonly HttpClient HttpClient = new HttpClient();

        [Function("IngestCongressionalTrades")]
        public static async Task Run(
            // 3 times per hour:
            [TimerTrigger("0 5,25,45 13-21 * * 1-5", RunOnStartup = false)]
            TimerInfo timer,
            FunctionContext context)
        {
            var log = context.GetLogger("IngestCongressionalTrades");

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
                    "Required: FMP_API_KEY, SUPABASE_API_URL, " +
                    "SUPABASE_SERVICE_ROLE_KEY");

                return;
            }

            if (string.IsNullOrWhiteSpace(fmpBaseUrl))
            {
                fmpBaseUrl =
                    "https://financialmodelingprep.com/stable";
            }

            fmpBaseUrl =
                NormalizeFmpBaseUrl(fmpBaseUrl);

            supabaseUrl =
                supabaseUrl.TrimEnd('/');

            try
            {
                log.LogInformation(
                    "Starting congressional trade ingestion.");

                /*
                 * One Azure Function invocation.
                 *
                 * Inside that invocation we make two FMP calls:
                 *
                 * 1. Senate
                 * 2. House
                 */
                await FetchAndStoreTrades(
                    chamber: "Senate",
                    endpoint: "senate-latest",
                    fmpBaseUrl: fmpBaseUrl,
                    fmpApiKey: fmpApiKey,
                    supabaseUrl: supabaseUrl,
                    supabaseKey: supabaseKey,
                    log: log);

                await FetchAndStoreTrades(
                    chamber: "House",
                    endpoint: "house-latest",
                    fmpBaseUrl: fmpBaseUrl,
                    fmpApiKey: fmpApiKey,
                    supabaseUrl: supabaseUrl,
                    supabaseKey: supabaseKey,
                    log: log);

                log.LogInformation(
                    "Congressional trade ingestion completed.");
            }
            catch (Exception ex)
            {
                log.LogError(
                    ex,
                    "Unhandled exception in IngestCongressionalTrades.");
            }
        }


        // ------------------------------------------------------------
        // Fetch FMP data and store in Supabase
        // ------------------------------------------------------------

        private static async Task FetchAndStoreTrades(
            string chamber,
            string endpoint,
            string fmpBaseUrl,
            string fmpApiKey,
            string supabaseUrl,
            string supabaseKey,
            ILogger log)
        {
            var url =
                $"{fmpBaseUrl}/{endpoint}" +
                $"?apikey={Uri.EscapeDataString(fmpApiKey)}";

            /*
             * NOTE:
             * We are intentionally NOT adding limit=25 yet.
             *
             * Your current FMP test response is returning about
             * 100 rows even though the documentation mentions 25.
             *
             * We'll take whatever FMP gives us and monitor it.
             */

            var response =
                await HttpClient.GetAsync(url);

            var responseBody =
                await response.Content.ReadAsStringAsync();

            if (!response.IsSuccessStatusCode)
            {
                log.LogError(
                    "FMP {Chamber} request failed. " +
                    "Status: {Status}. Body: {Body}",
                    chamber,
                    response.StatusCode,
                    responseBody);

                return;
            }

            using var document =
                JsonDocument.Parse(responseBody);

            if (document.RootElement.ValueKind !=
                JsonValueKind.Array)
            {
                log.LogWarning(
                    "Unexpected FMP {Chamber} response. " +
                    "Expected JSON array. Body: {Body}",
                    chamber,
                    responseBody);

                return;
            }

            var rows =
                new List<Dictionary<string, object?>>();

            foreach (var item in
                     document.RootElement.EnumerateArray())
            {
                var symbol =
                    GetString(item, "symbol")?
                        .Trim()
                        .ToUpperInvariant();

                /*
                 * Some congressional disclosures may not have
                 * a usable ticker.
                 *
                 * Unlike stock quote ingestion, we do NOT throw
                 * the row away just because symbol is missing.
                 */
                var sourceMemberId =
                    GetString(item, "senateID");

                var disclosureDate =
                    GetString(item, "disclosureDate");

                var transactionDate =
                    GetString(item, "transactionDate");

                var firstName =
                    GetString(item, "firstName");

                var lastName =
                    GetString(item, "lastName");

                var office =
                    GetString(item, "office");

                var district =
                    GetString(item, "district");

                var owner =
                    GetString(item, "owner");

                var assetDescription =
                    GetString(item, "assetDescription");

                var assetType =
                    GetString(item, "assetType");

                var transactionType =
                    GetString(item, "type");

                var amountRange =
                    GetString(item, "amount");

                var comment =
                    GetString(item, "comment");

                var link =
                    GetString(item, "link");

                /*
                 * House currently returns this field.
                 * Senate may not.
                 *
                 * NULL is fine for Senate rows.
                 */
                var capitalGainsOver200Usd =
                    GetNullableBoolean(
                        item,
                        "capitalGainsOver200USD");

                /*
                 * Example:
                 *
                 * "$1,001 - $15,000"
                 *
                 * becomes:
                 *
                 * amount_min = 1001
                 * amount_max = 15000
                 */
                var (amountMin, amountMax) =
                    ParseAmountRange(amountRange);

                /*
                 * Create deterministic transaction identity.
                 *
                 * Important because the "latest" endpoints will
                 * return many of the same records every time this
                 * timer executes.
                 */
                var sourceHash =
                    CreateSourceHash(
                        chamber,
                        symbol,
                        sourceMemberId,
                        disclosureDate,
                        transactionDate,
                        firstName,
                        lastName,
                        owner,
                        assetDescription,
                        transactionType,
                        amountRange,
                        link);

                rows.Add(
                    new Dictionary<string, object?>
                    {
                        ["chamber"] =
                            chamber,

                        ["symbol"] =
                            NullIfEmpty(symbol),

                        ["source_member_id"] =
                            NullIfEmpty(sourceMemberId),

                        ["disclosure_date"] =
                            NullIfEmpty(disclosureDate),

                        ["transaction_date"] =
                            NullIfEmpty(transactionDate),

                        ["first_name"] =
                            NullIfEmpty(firstName),

                        ["last_name"] =
                            NullIfEmpty(lastName),

                        ["office"] =
                            NullIfEmpty(office),

                        ["district"] =
                            NullIfEmpty(district),

                        ["owner"] =
                            NullIfEmpty(owner),

                        ["asset_description"] =
                            NullIfEmpty(assetDescription),

                        ["asset_type"] =
                            NullIfEmpty(assetType),

                        ["transaction_type"] =
                            NullIfEmpty(transactionType),

                        ["amount_range"] =
                            NullIfEmpty(amountRange),

                        ["amount_min"] =
                            amountMin,

                        ["amount_max"] =
                            amountMax,

                        ["capital_gains_over_200_usd"] =
                            capitalGainsOver200Usd,

                        ["comment"] =
                            NullIfEmpty(comment),

                        ["link"] =
                            NullIfEmpty(link),

                        ["provider"] =
                            "fmp_congressional_trades",

                        ["source_hash"] =
                            sourceHash,

                        /*
                         * Keep complete original FMP record.
                         */
                        ["raw_payload"] =
                            item.Clone()
                    });
            }

            log.LogInformation(
                "FMP {Chamber} returned {Count} records.",
                chamber,
                rows.Count);

            if (rows.Count == 0)
            {
                return;
            }

            var insertUrl =
                $"{supabaseUrl}" +
                "/rest/v1/congressional_trades_raw" +
                "?on_conflict=source_hash";

            await SupabasePost(
                insertUrl,
                supabaseKey,
                rows);

            log.LogInformation(
                "Processed {Count} {Chamber} congressional trade records.",
                rows.Count,
                chamber);
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

            /*
             * source_hash is UNIQUE.
             *
             * If we've already seen the same congressional
             * transaction, Supabase ignores it.
             */
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
        // Amount parsing
        // ------------------------------------------------------------

        private static (decimal? min, decimal? max)
            ParseAmountRange(string? amount)
        {
            if (string.IsNullOrWhiteSpace(amount))
            {
                return (null, null);
            }

            /*
             * Examples handled:
             *
             * $1,001 - $15,000
             * $15,001 - $50,000
             * Over $50,000,000
             * Under $1,000
             */

            var cleaned =
                amount.Trim();

            var numbers =
                Regex.Matches(
                    cleaned,
                    @"\$?([\d,]+(?:\.\d+)?)");

            var values =
                new List<decimal>();

            foreach (Match match in numbers)
            {
                var raw =
                    match.Groups[1]
                        .Value
                        .Replace(",", "");

                if (decimal.TryParse(
                        raw,
                        NumberStyles.Any,
                        CultureInfo.InvariantCulture,
                        out var parsed))
                {
                    values.Add(parsed);
                }
            }

            if (values.Count == 0)
            {
                return (null, null);
            }

            if (cleaned.StartsWith(
                    "Over",
                    StringComparison.OrdinalIgnoreCase))
            {
                return (values[0], null);
            }

            if (cleaned.StartsWith(
                    "Under",
                    StringComparison.OrdinalIgnoreCase))
            {
                return (null, values[0]);
            }

            if (values.Count >= 2)
            {
                return (
                    values[0],
                    values[1]);
            }

            return (
                values[0],
                values[0]);
        }


        // ------------------------------------------------------------
        // Hash / duplicate prevention
        // ------------------------------------------------------------

        private static string CreateSourceHash(
            string? chamber,
            string? symbol,
            string? sourceMemberId,
            string? disclosureDate,
            string? transactionDate,
            string? firstName,
            string? lastName,
            string? owner,
            string? assetDescription,
            string? transactionType,
            string? amountRange,
            string? link)
        {
            var identity =
                string.Join(
                    "|",
                    chamber ?? "",
                    symbol ?? "",
                    sourceMemberId ?? "",
                    disclosureDate ?? "",
                    transactionDate ?? "",
                    firstName ?? "",
                    lastName ?? "",
                    owner ?? "",
                    assetDescription ?? "",
                    transactionType ?? "",
                    amountRange ?? "",
                    link ?? "");

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


        private static bool? GetNullableBoolean(
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
                JsonValueKind.True)
            {
                return true;
            }

            if (property.ValueKind ==
                JsonValueKind.False)
            {
                return false;
            }

            if (property.ValueKind ==
                JsonValueKind.String)
            {
                var value =
                    property.GetString();

                if (bool.TryParse(
                        value,
                        out var parsed))
                {
                    return parsed;
                }
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
        // FMP URL
        // ------------------------------------------------------------

        private static string NormalizeFmpBaseUrl(
            string baseUrl)
        {
            baseUrl =
                baseUrl.TrimEnd('/');

            /*
             * Supports either:
             *
             * https://financialmodelingprep.com
             *
             * OR
             *
             * https://financialmodelingprep.com/stable
             */
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