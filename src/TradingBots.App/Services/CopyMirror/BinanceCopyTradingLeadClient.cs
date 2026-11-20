using System.Diagnostics;
using System.Globalization;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace TradingBots.App.Services.CopyMirror;

public interface IBinanceCopyTradingLeadClient
{
    Task<IReadOnlyList<CopyLeadSummary>> GetLeaderboardAsync(
        string timeRange = "30D",
        string dataType = "ROI",
        int pageSize = 20,
        CancellationToken ct = default);

    Task<(IReadOnlyList<CopyLeadOrder> Orders, int ElapsedMs)> GetOrderHistoryAsync(
        string portfolioId,
        int pageSize = 30,
        CancellationToken ct = default);
}

public sealed class BinanceCopyTradingLeadClient(HttpClient httpClient, ILogger<BinanceCopyTradingLeadClient> logger)
    : IBinanceCopyTradingLeadClient
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };
    private const string Base = "https://www.binance.com/bapi/futures/v1/friendly/future/copy-trade";

    public async Task<IReadOnlyList<CopyLeadSummary>> GetLeaderboardAsync(
        string timeRange = "30D",
        string dataType = "ROI",
        int pageSize = 20,
        CancellationToken ct = default)
    {
        EnsureBrowserHeaders();
        var payload = new
        {
            pageNumber = 1,
            pageSize = Math.Clamp(pageSize, 1, 30),
            timeRange,
            dataType,
            favoriteOnly = false,
            hideFull = false,
            nickname = "",
            order = "DESC",
            userAsset = 0,
            portfolioType = "PUBLIC"
        };

        using var content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");
        using var response = await httpClient.PostAsync($"{Base}/home-page/query-list", content, ct);
        var body = await response.Content.ReadAsStringAsync(ct);
        if (!response.IsSuccessStatusCode)
        {
            logger.LogWarning("Leaderboard HTTP {Status}: {Body}", (int)response.StatusCode, Truncate(body));
            return [];
        }

        using var doc = JsonDocument.Parse(body);
        if (!IsSuccess(doc.RootElement))
        {
            logger.LogWarning("Leaderboard API fail: {Body}", Truncate(body));
            return [];
        }

        if (!doc.RootElement.TryGetProperty("data", out var data) ||
            !data.TryGetProperty("list", out var list) ||
            list.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var results = new List<CopyLeadSummary>();
        foreach (var item in list.EnumerateArray())
        {
            var id = ReadString(item, "leadPortfolioId");
            if (string.IsNullOrWhiteSpace(id))
            {
                continue;
            }

            results.Add(new CopyLeadSummary
            {
                LeadPortfolioId = id,
                Nickname = ReadString(item, "nickname"),
                Roi = ReadDecimal(item, "roi"),
                Pnl = ReadDecimal(item, "pnl"),
                Aum = ReadDecimal(item, "aum"),
                Mdd = ReadDecimal(item, "mdd"),
                WinRate = ReadDecimal(item, "winRate"),
                CurrentCopyCount = (int)ReadDecimal(item, "currentCopyCount"),
                MaxCopyCount = (int)ReadDecimal(item, "maxCopyCount")
            });
        }

        return results;
    }

    public async Task<(IReadOnlyList<CopyLeadOrder> Orders, int ElapsedMs)> GetOrderHistoryAsync(
        string portfolioId,
        int pageSize = 30,
        CancellationToken ct = default)
    {
        EnsureBrowserHeaders();
        var payload = new
        {
            portfolioId,
            pageNumber = 1,
            pageSize = Math.Clamp(pageSize, 1, 50)
        };

        var sw = Stopwatch.StartNew();
        using var content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");
        using var response = await httpClient.PostAsync($"{Base}/lead-portfolio/order-history", content, ct);
        var body = await response.Content.ReadAsStringAsync(ct);
        sw.Stop();

        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"order-history HTTP {(int)response.StatusCode}: {Truncate(body)}");
        }

        using var doc = JsonDocument.Parse(body);
        if (!IsSuccess(doc.RootElement))
        {
            var msg = doc.RootElement.TryGetProperty("message", out var m) ? m.GetString() : "unknown";
            throw new InvalidOperationException($"order-history fail: {msg}");
        }

        if (!doc.RootElement.TryGetProperty("data", out var data) ||
            !data.TryGetProperty("list", out var list) ||
            list.ValueKind != JsonValueKind.Array)
        {
            return ([], (int)sw.ElapsedMilliseconds);
        }

        var orders = new List<CopyLeadOrder>();
        foreach (var item in list.EnumerateArray())
        {
            var symbol = ReadString(item, "symbol");
            var side = ReadString(item, "side");
            if (string.IsNullOrWhiteSpace(symbol) || string.IsNullOrWhiteSpace(side))
            {
                continue;
            }

            orders.Add(new CopyLeadOrder
            {
                Symbol = symbol,
                Side = side.ToUpperInvariant(),
                PositionSide = ReadString(item, "positionSide").ToUpperInvariant(),
                ExecutedQty = ReadDecimal(item, "executedQty"),
                AvgPrice = ReadDecimal(item, "avgPrice"),
                OrderTimeMs = ReadLong(item, "orderTime"),
                OrderUpdateTimeMs = ReadLong(item, "orderUpdateTime")
            });
        }

        return (orders, (int)sw.ElapsedMilliseconds);
    }

    private void EnsureBrowserHeaders()
    {
        if (httpClient.DefaultRequestHeaders.UserAgent.Count == 0)
        {
            httpClient.DefaultRequestHeaders.UserAgent.ParseAdd(
                "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36");
        }

        if (!httpClient.DefaultRequestHeaders.Contains("clienttype"))
        {
            httpClient.DefaultRequestHeaders.TryAddWithoutValidation("clienttype", "web");
        }

        if (!httpClient.DefaultRequestHeaders.Contains("Origin"))
        {
            httpClient.DefaultRequestHeaders.TryAddWithoutValidation("Origin", "https://www.binance.com");
        }

        if (httpClient.DefaultRequestHeaders.Referrer is null)
        {
            httpClient.DefaultRequestHeaders.Referrer = new Uri("https://www.binance.com/en/copy-trading");
        }

        if (httpClient.DefaultRequestHeaders.Accept.Count == 0)
        {
            httpClient.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        }
    }

    private static bool IsSuccess(JsonElement root)
    {
        if (root.TryGetProperty("success", out var success) && success.ValueKind is JsonValueKind.True or JsonValueKind.False)
        {
            return success.GetBoolean();
        }

        if (root.TryGetProperty("code", out var code))
        {
            var c = code.ValueKind == JsonValueKind.Number ? code.GetRawText() : code.GetString();
            return c is "000000" or "0";
        }

        return false;
    }

    private static string ReadString(JsonElement el, string name) =>
        el.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.String
            ? p.GetString() ?? string.Empty
            : el.TryGetProperty(name, out p) ? p.ToString() : string.Empty;

    private static decimal ReadDecimal(JsonElement el, string name)
    {
        if (!el.TryGetProperty(name, out var p))
        {
            return 0m;
        }

        return p.ValueKind switch
        {
            JsonValueKind.Number => p.TryGetDecimal(out var d) ? d : 0m,
            JsonValueKind.String => decimal.TryParse(p.GetString(), NumberStyles.Any, CultureInfo.InvariantCulture, out var d)
                ? d
                : 0m,
            _ => 0m
        };
    }

    private static long ReadLong(JsonElement el, string name)
    {
        if (!el.TryGetProperty(name, out var p))
        {
            return 0;
        }

        return p.ValueKind switch
        {
            JsonValueKind.Number => p.TryGetInt64(out var v) ? v : 0,
            JsonValueKind.String => long.TryParse(p.GetString(), NumberStyles.Any, CultureInfo.InvariantCulture, out var v)
                ? v
                : 0,
            _ => 0
        };
    }

    private static string Truncate(string s) =>
        string.IsNullOrEmpty(s) ? string.Empty : (s.Length <= 240 ? s : s[..240]);
}
