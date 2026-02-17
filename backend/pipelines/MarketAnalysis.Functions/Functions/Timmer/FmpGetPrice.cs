using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;
using Npgsql;
using NpgsqlTypes;

namespace MarketAnalysis.Functions;

public class FmpGetPrice
{
    private readonly ILogger _logger;
    private readonly string _connectionString;
    private readonly string _fmpApiKey;

    // Optional tuning knobs
    private readonly int _maxSymbolsPerRun;
    private readonly int _batchSize;

    private static readonly HttpClient Http = new HttpClient();

    public FmpGetPrice(ILoggerFactory loggerFactory)
    {
        _logger = loggerFactory.CreateLogger<FmpGetPrice>();

        _connectionString = Environment.GetEnvironmentVariable("SUPABASE_DB_URL")
            ?? throw new Exception("SUPABASE_DB_URL not found");

        _fmpApiKey = Environment.GetEnvironmentVariable("FMP_API_KEY")
            ?? throw new Exception("FMP_API_KEY not found");

        // Defaults: your LIMIT 900, and a safe per-request batch size.
        _maxSymbolsPerRun = int.TryParse(Environment.GetEnvironmentVariable("FMP_MAX_SYMBOLS_PER_RUN"), out var ms)
            ? ms
            : 900;

        _batchSize = int.TryParse(Environment.GetEnvironmentVariable("FMP_BATCH_SIZE"), out var bs)
            ? bs
            : 200;
    }

    // Every 2 minutes; market-hours gating keeps us well under 250 calls/day
    [Function("fmp_get_price")]
    public async Task Run(
        [TimerTrigger("0 */2 * * * *", RunOnStartup = false)] TimerInfo myTimer)
    {
        var nowUtc = DateTime.UtcNow;

        var bypass = Environment.GetEnvironmentVariable("BYPASS_MARKET_HOURS") == "true";
        if (!bypass && !IsWithinUsMarketHours(nowUtc))
        {
            _logger.LogInformation("Outside US market hours at {NowUtc}, skipping FMP batch call.", nowUtc);
            return;
        }

        _logger.LogInformation("fmp_get_price triggered at {NowUtc}", nowUtc);

        try
        {
            await using var conn = new NpgsqlConnection(_connectionString);
            await conn.OpenAsync();

            // 1) Pull symbols to refresh from fundamentals_raw
            var symbols = await GetSymbolsToCheckAsync(conn, _maxSymbolsPerRun);

            if (symbols.Count == 0)
            {
                _logger.LogInformation("No symbols eligible for price check (all checked within last 30 minutes).");
                return;
            }

            _logger.LogInformation("Eligible symbols this run: {Count}", symbols.Count);

            // 2) Process in batches so URL doesn't explode / API limits don't bite
            int totalInserted = 0;
            int batchNum = 0;

            foreach (var batch in Chunk(symbols, _batchSize))
            {
                batchNum++;
                var csv = string.Join(",", batch);

                var url =
                    $"https://financialmodelingprep.com/stable/batch-quote?symbols={csv}&apikey={_fmpApiKey}";

                var response = await Http.GetAsync(url);

                if (!response.IsSuccessStatusCode)
                {
                    _logger.LogWarning("Batch {BatchNum}: FMP returned {Status}. Skipping this batch.", batchNum, response.StatusCode);
                    continue;
                }

                var json = await response.Content.ReadAsStringAsync();
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;

                if (root.ValueKind != JsonValueKind.Array || root.GetArrayLength() == 0)
                {
                    _logger.LogWarning("Batch {BatchNum}: FMP returned empty or non-array payload.", batchNum);
                    continue;
                }

                // 3) Insert quotes into stocks_raw
                var insertedThisBatch = await InsertStocksRawAsync(conn, root, nowUtc);
                totalInserted += insertedThisBatch;

                // 4) Update last_price_checked_at ONLY for this processed batch
                //    (Use nowUtc so DB and function agree.)
                await MarkLastPriceCheckedAsync(conn, batch, nowUtc);

                _logger.LogInformation(
                    "Batch {BatchNum}: inserted {Inserted} row(s), marked {Marked} symbols checked.",
                    batchNum,
                    insertedThisBatch,
                    batch.Count);
            }

            _logger.LogInformation(
                "Run complete. Inserted {Rows} row(s) into stocks_raw at {NowUtc}.",
                totalInserted,
                nowUtc);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "FMP /stable/batch-quote ingestion failed.");
        }
    }

    private static async Task<List<string>> GetSymbolsToCheckAsync(NpgsqlConnection conn, int limit)
    {
        // Your query strategy
        const string sql = @"
            SELECT symbol
            FROM fundamentals_raw
            WHERE last_price_checked_at IS NULL
               OR last_price_checked_at < now() - interval '30 minutes'
            ORDER BY last_price_checked_at NULLS FIRST
            LIMIT @limit;
        ";

        await using var cmd = new NpgsqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("limit", NpgsqlDbType.Integer, limit);

        var results = new List<string>(capacity: Math.Min(limit, 1024));

        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            var sym = reader.GetString(0);
            if (!string.IsNullOrWhiteSpace(sym))
                results.Add(sym.Trim().ToUpperInvariant());
        }

        return results;
    }

    private async Task<int> InsertStocksRawAsync(NpgsqlConnection conn, JsonElement rootArray, DateTime nowUtc)
    {
        const string sql = @"
            insert into stocks_raw (symbol, provider, as_of, price, raw_payload)
            values (@symbol, @provider, @as_of, @price, @raw_payload);
        ";

        await using var cmd = new NpgsqlCommand(sql, conn);
        cmd.Parameters.Add("symbol", NpgsqlDbType.Text);
        cmd.Parameters.Add("provider", NpgsqlDbType.Text);
        cmd.Parameters.Add("as_of", NpgsqlDbType.TimestampTz);
        cmd.Parameters.Add("price", NpgsqlDbType.Numeric);
        cmd.Parameters.Add("raw_payload", NpgsqlDbType.Jsonb);

        int inserted = 0;

        foreach (var node in rootArray.EnumerateArray())
        {
            if (!node.TryGetProperty("symbol", out var symProp) ||
                !node.TryGetProperty("price", out var priceProp))
            {
                continue;
            }

            var symbol = symProp.GetString() ?? "UNKNOWN";

            // FMP sometimes returns null price fields depending on symbol/exchange state
            if (priceProp.ValueKind != JsonValueKind.Number)
                continue;

            var price = priceProp.GetDecimal();

            cmd.Parameters["symbol"].Value = symbol;
            cmd.Parameters["provider"].Value = "fmp_batch_quote";
            cmd.Parameters["as_of"].Value = nowUtc;
            cmd.Parameters["price"].Value = price;

            // Store just this symbol's JSON slice, not the whole array
            cmd.Parameters["raw_payload"].Value = node.GetRawText();

            inserted += await cmd.ExecuteNonQueryAsync();
        }

        return inserted;
    }

    private static async Task MarkLastPriceCheckedAsync(NpgsqlConnection conn, List<string> symbols, DateTime nowUtc)
    {
        // Update only the rows for the symbols we attempted/processed in this batch.
        // Uses ANY(@symbols) with a text[] parameter.
        const string sql = @"
            UPDATE fundamentals_raw
            SET last_price_checked_at = @now_utc
            WHERE symbol = ANY(@symbols);
        ";

        await using var cmd = new NpgsqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("now_utc", NpgsqlDbType.TimestampTz, nowUtc);
        cmd.Parameters.AddWithValue("symbols", NpgsqlDbType.Array | NpgsqlDbType.Text, symbols.ToArray());

        await cmd.ExecuteNonQueryAsync();
    }

    private static IEnumerable<List<T>> Chunk<T>(List<T> source, int size)
    {
        for (var i = 0; i < source.Count; i += size)
        {
            yield return source.GetRange(i, Math.Min(size, source.Count - i));
        }
    }

    private static bool IsWithinUsMarketHours(DateTime utcNow)
    {
        // Regular hours 9:30–16:00 America/New_York, Mon–Fri
        TimeZoneInfo nyTz;
        try
        {
            nyTz = TimeZoneInfo.FindSystemTimeZoneById("America/New_York");
        }
        catch
        {
            // Windows fallback
            nyTz = TimeZoneInfo.FindSystemTimeZoneById("Eastern Standard Time");
        }

        var nyTime = TimeZoneInfo.ConvertTimeFromUtc(utcNow, nyTz);

        if (nyTime.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday)
            return false;

        var t = nyTime.TimeOfDay;
        var open = new TimeSpan(9, 30, 0);
        var close = new TimeSpan(16, 0, 0);

        return t >= open && t <= close;
    }
}
