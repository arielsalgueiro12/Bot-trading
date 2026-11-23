using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using TradingBots.App.Models;

namespace TradingBots.App.Services;

public interface IBinanceFuturesExecutionService
{
    Task<bool> IsFuturesSymbolTradableAsync(string symbol, CancellationToken ct = default);
    string GetLastExecutionError();
    /// <summary>Abre o cierra posicion USD-M. positionSide: LONG o SHORT. open=true abre, false cierra.</summary>
    Task<TradeFillResult?> MarketPositionAsync(
        string symbol,
        string positionSide,
        bool open,
        decimal quoteUsdtOrCloseQty,
        bool quoteIsNotional,
        int leverage,
        CancellationToken ct = default);
}

/// <summary>Ejecucion minima USD-M para Copy Mirror (market + hedge/one-way).</summary>
public sealed class BinanceFuturesExecutionService(
    HttpClient httpClient,
    IBinanceSettingsService settingsService,
    ILogger<BinanceFuturesExecutionService> logger) : IBinanceFuturesExecutionService
{
    private string _lastExecutionError = string.Empty;
    private const long DefaultRecvWindow = 60_000;
    private long _timeOffsetMs;
    private bool? _dualSidePosition;
    private FuturesExchangeInfo? _exchangeCache;
    private DateTime _exchangeCacheUtc = DateTime.MinValue;

    public string GetLastExecutionError() => _lastExecutionError;

    public async Task<bool> IsFuturesSymbolTradableAsync(string symbol, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(symbol))
        {
            return false;
        }

        var settings = await settingsService.GetActiveSettingsAsync();
        var filters = await GetExchangeInfoAsync(settings, symbol.Trim().ToUpperInvariant(), ct);
        return filters is not null;
    }

    public async Task<TradeFillResult?> MarketPositionAsync(
        string symbol,
        string positionSide,
        bool open,
        decimal quoteUsdtOrCloseQty,
        bool quoteIsNotional,
        int leverage,
        CancellationToken ct = default)
    {
        var settings = await settingsService.GetActiveSettingsAsync();
        if (!CanTradeReal(settings))
        {
            _lastExecutionError = "Live trading deshabilitado por configuracion/guardas.";
            return null;
        }

        symbol = symbol.Trim().ToUpperInvariant();
        var pos = (positionSide ?? "LONG").Trim().ToUpperInvariant();
        if (pos is not ("LONG" or "SHORT"))
        {
            _lastExecutionError = $"positionSide invalido: {positionSide}";
            return null;
        }

        var filters = await GetExchangeInfoAsync(settings, symbol, ct);
        if (filters is null)
        {
            _lastExecutionError = $"Simbolo Futures no disponible: {symbol}";
            return null;
        }

        decimal qty;
        if (quoteIsNotional)
        {
            var price = await GetMarkPriceAsync(settings, symbol, ct);
            if (price <= 0m)
            {
                _lastExecutionError = $"No se pudo leer precio Futures de {symbol}.";
                return null;
            }

            var notional = decimal.Round(Math.Max(quoteUsdtOrCloseQty, 0m), 8, MidpointRounding.ToZero);
            if (notional < Math.Max(filters.MinNotional, 5m))
            {
                _lastExecutionError = $"Notional {Fmt(notional)} bajo minimo Futures para {symbol}.";
                return null;
            }

            qty = NormalizeQuantity(notional / price, filters.StepSize, filters.MinQty);
        }
        else
        {
            qty = NormalizeQuantity(quoteUsdtOrCloseQty, filters.StepSize, filters.MinQty);
        }

        if (qty <= 0m)
        {
            _lastExecutionError = $"Cantidad Futures invalida para {symbol}.";
            return null;
        }

        var lev = Math.Clamp(leverage <= 0 ? 1 : leverage, 1, 5);
        await TrySetLeverageAsync(settings, symbol, lev, ct);

        var dual = await IsDualSideAsync(settings, ct);
        // open LONG = BUY, close LONG = SELL; open SHORT = SELL, close SHORT = BUY
        var side = (pos == "LONG") == open ? "BUY" : "SELL";
        var positionSideParam = dual ? pos : "BOTH";

        return await SendOrderAsync(settings, symbol, side, positionSideParam, qty, ct);
    }

    private async Task<TradeFillResult?> SendOrderAsync(
        BinanceConnectionSettings settings,
        string symbol,
        string side,
        string positionSide,
        decimal quantity,
        CancellationToken ct)
    {
        try
        {
            _lastExecutionError = string.Empty;
            var baseUrl = settingsService.ResolveFuturesBaseUrl(settings);
            await SyncServerTimeOffsetAsync(baseUrl, ct);
            var clientOrderId = BuildClientOrderId(side, symbol);
            var query =
                $"symbol={symbol}&side={side}&type=MARKET&quantity={Fmt(quantity)}" +
                $"&positionSide={positionSide}&newClientOrderId={clientOrderId}" +
                $"&recvWindow={DefaultRecvWindow}&timestamp={NowMsWithOffset()}";
            var signature = Sign(query, settings.ApiSecret);
            using var request = new HttpRequestMessage(HttpMethod.Post, $"{baseUrl}/fapi/v1/order?{query}&signature={signature}");
            request.Headers.TryAddWithoutValidation("X-MBX-APIKEY", settings.ApiKey);
            var response = await httpClient.SendAsync(request, ct);
            var body = await response.Content.ReadAsStringAsync(ct);
            if (!response.IsSuccessStatusCode)
            {
                _lastExecutionError = ExtractBinanceError(body);
                logger.LogWarning("Futures order fail {Symbol} {Side}/{Pos}: {Err}", symbol, side, positionSide, _lastExecutionError);
                return null;
            }

            var order = JsonSerializer.Deserialize<FuturesOrderResponse>(body, JsonOpts());
            if (order is null)
            {
                _lastExecutionError = "Respuesta Futures vacia.";
                return null;
            }

            var qty = Parse(order.ExecutedQty);
            var avg = Parse(order.AvgPrice);
            if (avg <= 0m && qty > 0m && !string.IsNullOrWhiteSpace(order.CumQuote))
            {
                avg = Parse(order.CumQuote) / qty;
            }

            return new TradeFillResult
            {
                ExecutedQuantity = qty,
                AveragePrice = avg
            };
        }
        catch (Exception ex)
        {
            _lastExecutionError = ex.Message.Length > 180 ? ex.Message[..180] : ex.Message;
            logger.LogWarning(ex, "Error orden Futures {Symbol}", symbol);
            return null;
        }
    }

    private async Task TrySetLeverageAsync(BinanceConnectionSettings settings, string symbol, int leverage, CancellationToken ct)
    {
        try
        {
            var baseUrl = settingsService.ResolveFuturesBaseUrl(settings);
            await SyncServerTimeOffsetAsync(baseUrl, ct);
            var query = $"symbol={symbol}&leverage={leverage}&recvWindow={DefaultRecvWindow}&timestamp={NowMsWithOffset()}";
            var signature = Sign(query, settings.ApiSecret);
            using var request = new HttpRequestMessage(HttpMethod.Post, $"{baseUrl}/fapi/v1/leverage?{query}&signature={signature}");
            request.Headers.TryAddWithoutValidation("X-MBX-APIKEY", settings.ApiKey);
            var response = await httpClient.SendAsync(request, ct);
            if (!response.IsSuccessStatusCode)
            {
                var body = await response.Content.ReadAsStringAsync(ct);
                logger.LogDebug("Set leverage {Symbol}x{Lev} aviso: {Body}", symbol, leverage, ExtractBinanceError(body));
            }
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "No se pudo fijar leverage {Symbol}", symbol);
        }
    }

    private async Task<bool> IsDualSideAsync(BinanceConnectionSettings settings, CancellationToken ct)
    {
        if (_dualSidePosition is not null)
        {
            return _dualSidePosition.Value;
        }

        try
        {
            var baseUrl = settingsService.ResolveFuturesBaseUrl(settings);
            await SyncServerTimeOffsetAsync(baseUrl, ct);
            var query = $"recvWindow={DefaultRecvWindow}&timestamp={NowMsWithOffset()}";
            var signature = Sign(query, settings.ApiSecret);
            using var request = new HttpRequestMessage(HttpMethod.Get, $"{baseUrl}/fapi/v1/positionSide/dual?{query}&signature={signature}");
            request.Headers.TryAddWithoutValidation("X-MBX-APIKEY", settings.ApiKey);
            var response = await httpClient.SendAsync(request, ct);
            var body = await response.Content.ReadAsStringAsync(ct);
            if (!response.IsSuccessStatusCode)
            {
                _dualSidePosition = true; // copy-trade leads usan LONG/SHORT; asumir hedge
                return true;
            }

            using var doc = JsonDocument.Parse(body);
            _dualSidePosition = doc.RootElement.TryGetProperty("dualSidePosition", out var d) && d.GetBoolean();
            return _dualSidePosition.Value;
        }
        catch
        {
            _dualSidePosition = true;
            return true;
        }
    }

    private async Task<decimal> GetMarkPriceAsync(BinanceConnectionSettings settings, string symbol, CancellationToken ct)
    {
        try
        {
            var baseUrl = settingsService.ResolveFuturesBaseUrl(settings);
            using var request = new HttpRequestMessage(HttpMethod.Get, $"{baseUrl}/fapi/v1/ticker/price?symbol={symbol}");
            var response = await httpClient.SendAsync(request, ct);
            response.EnsureSuccessStatusCode();
            var body = await response.Content.ReadAsStringAsync(ct);
            using var doc = JsonDocument.Parse(body);
            return Parse(doc.RootElement.GetProperty("price").GetString());
        }
        catch
        {
            return 0m;
        }
    }

    private async Task<SymbolFilters?> GetExchangeInfoAsync(BinanceConnectionSettings settings, string symbol, CancellationToken ct)
    {
        try
        {
            if (_exchangeCache is null || DateTime.UtcNow - _exchangeCacheUtc > TimeSpan.FromMinutes(30))
            {
                var baseUrl = settingsService.ResolveFuturesBaseUrl(settings);
                using var request = new HttpRequestMessage(HttpMethod.Get, $"{baseUrl}/fapi/v1/exchangeInfo");
                var response = await httpClient.SendAsync(request, ct);
                response.EnsureSuccessStatusCode();
                var json = await response.Content.ReadAsStringAsync(ct);
                _exchangeCache = JsonSerializer.Deserialize<FuturesExchangeInfo>(json, JsonOpts());
                _exchangeCacheUtc = DateTime.UtcNow;
            }

            var s = _exchangeCache?.Symbols?.FirstOrDefault(x =>
                string.Equals(x.Symbol, symbol, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(x.Status, "TRADING", StringComparison.OrdinalIgnoreCase));
            if (s is null)
            {
                return null;
            }

            var lot = s.Filters?.FirstOrDefault(x => x.FilterType == "LOT_SIZE");
            var marketLot = s.Filters?.FirstOrDefault(x => x.FilterType == "MARKET_LOT_SIZE");
            var minNotional = s.Filters?.FirstOrDefault(x => x.FilterType == "MIN_NOTIONAL");
            return new SymbolFilters
            {
                MinQty = FirstPositive(Parse(marketLot?.MinQty), Parse(lot?.MinQty)),
                StepSize = FirstPositive(Parse(marketLot?.StepSize), Parse(lot?.StepSize), 0.001m),
                MinNotional = Parse(minNotional?.Notional)
            };
        }
        catch
        {
            return null;
        }
    }

    private async Task SyncServerTimeOffsetAsync(string baseUrl, CancellationToken ct)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, $"{baseUrl}/fapi/v1/time");
            var response = await httpClient.SendAsync(request, ct);
            response.EnsureSuccessStatusCode();
            var body = await response.Content.ReadAsStringAsync(ct);
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.TryGetProperty("serverTime", out var t))
            {
                _timeOffsetMs = t.GetInt64() - DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            }
        }
        catch
        {
        }
    }

    private static bool CanTradeReal(BinanceConnectionSettings settings) =>
        settings.IsEnabled &&
        settings.ExecutionMode == TradeExecutionMode.Live &&
        settings.LiveEnabledByChecklist &&
        !settings.GlobalKillSwitch &&
        (settings.Environment != BinanceEnvironment.Production || settings.LiveSafetyConfirmed) &&
        !string.IsNullOrWhiteSpace(settings.ApiKey) &&
        !string.IsNullOrWhiteSpace(settings.ApiSecret);

    private static decimal NormalizeQuantity(decimal qty, decimal stepSize, decimal minQty)
    {
        if (stepSize <= 0)
        {
            return qty;
        }

        var steps = Math.Floor(qty / stepSize);
        var normalized = steps * stepSize;
        if (normalized < minQty)
        {
            return 0m;
        }

        return decimal.Round(normalized, 8, MidpointRounding.ToZero);
    }

    private static decimal FirstPositive(params decimal[] values)
    {
        foreach (var v in values)
        {
            if (v > 0m)
            {
                return v;
            }
        }

        return 0m;
    }

    private static string ExtractBinanceError(string body)
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            return "sin cuerpo";
        }

        try
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.TryGetProperty("msg", out var msg))
            {
                var code = doc.RootElement.TryGetProperty("code", out var c) ? c.GetInt32() : 0;
                return code == 0 ? msg.GetString() ?? body : $"{msg.GetString()} (code {code})";
            }
        }
        catch
        {
        }

        return body.Length > 180 ? body[..180] + "..." : body;
    }

    private static string BuildClientOrderId(string side, string symbol)
    {
        var sym = symbol.Length > 8 ? symbol[..8] : symbol;
        var nonce = Guid.NewGuid().ToString("N")[..8];
        return $"tbf-{side.ToLowerInvariant()}-{sym.ToLowerInvariant()}-{nonce}";
    }

    private long NowMsWithOffset() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() + _timeOffsetMs;
    private static string Fmt(decimal value) => value.ToString("0.########", CultureInfo.InvariantCulture);
    private static decimal Parse(string? value) =>
        decimal.TryParse(value, NumberStyles.Any, CultureInfo.InvariantCulture, out var d) ? d : 0m;

    private static string Sign(string query, string secret)
    {
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(secret));
        var hash = hmac.ComputeHash(Encoding.UTF8.GetBytes(query));
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static JsonSerializerOptions JsonOpts() => new() { PropertyNameCaseInsensitive = true };

    private sealed class FuturesOrderResponse
    {
        public string ExecutedQty { get; set; } = "0";
        public string AvgPrice { get; set; } = "0";
        public string CumQuote { get; set; } = "0";
    }

    private sealed class FuturesExchangeInfo
    {
        public List<FuturesSymbolInfo> Symbols { get; set; } = [];
    }

    private sealed class FuturesSymbolInfo
    {
        public string Symbol { get; set; } = string.Empty;
        public string Status { get; set; } = string.Empty;
        public List<FuturesFilter> Filters { get; set; } = [];
    }

    private sealed class FuturesFilter
    {
        public string FilterType { get; set; } = string.Empty;
        public string MinQty { get; set; } = "0";
        public string StepSize { get; set; } = "0";
        public string Notional { get; set; } = "0";
    }

    private sealed class SymbolFilters
    {
        public decimal MinQty { get; init; }
        public decimal StepSize { get; init; }
        public decimal MinNotional { get; init; }
    }
}
