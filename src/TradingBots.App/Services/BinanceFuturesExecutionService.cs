using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using TradingBots.App.Models;

namespace TradingBots.App.Services;

public interface IBinanceFuturesExecutionService
{
    Task<bool> IsFuturesSymbolTradableAsync(string symbol, CancellationToken ct = default);
    Task<BinanceFuturesAccountSummary> GetAccountSummaryAsync(CancellationToken ct = default);
    string GetLastExecutionError();
    /// <summary>Qty absoluta abierta en exchange para LONG o SHORT (0 si no hay).</summary>
    Task<decimal> GetPositionQuantityAsync(string symbol, string positionSide, CancellationToken ct = default);
    /// <summary>Todas las posiciones Futures abiertas (qty &gt; 0).</summary>
    Task<IReadOnlyList<FuturesOpenPosition>> GetOpenPositionsAsync(CancellationToken ct = default);
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

public sealed record FuturesOpenPosition(string Symbol, string PositionSide, decimal Quantity);

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

    public async Task<BinanceFuturesAccountSummary> GetAccountSummaryAsync(CancellationToken ct = default)
    {
        var settings = await settingsService.GetActiveSettingsAsync();
        var envName = settings.Environment == BinanceEnvironment.Production ? "Production" : "Sandbox/Demo";
        var baseUrl = settingsService.ResolveFuturesBaseUrl(settings);
        if (!HasFuturesCredentials(settings))
        {
            return new BinanceFuturesAccountSummary
            {
                Connected = false,
                EnvironmentName = envName,
                BaseUrl = baseUrl,
                Message = "Configura Futures API Key y Secret en Settings (demo.binance.com)."
            };
        }

        try
        {
            await SyncServerTimeOffsetAsync(baseUrl, ct);
            var query = $"recvWindow={DefaultRecvWindow}&timestamp={NowMsWithOffset()}";
            var signature = Sign(query, settings.FuturesApiSecret);
            using var request = new HttpRequestMessage(HttpMethod.Get, $"{baseUrl}/fapi/v2/balance?{query}&signature={signature}");
            request.Headers.TryAddWithoutValidation("X-MBX-APIKEY", settings.FuturesApiKey);
            var response = await httpClient.SendAsync(request, ct);
            var body = await response.Content.ReadAsStringAsync(ct);
            if (!response.IsSuccessStatusCode)
            {
                return new BinanceFuturesAccountSummary
                {
                    Connected = false,
                    EnvironmentName = envName,
                    BaseUrl = baseUrl,
                    Message = ExtractBinanceError(body)
                };
            }

            using var doc = JsonDocument.Parse(body);
            decimal available = 0m;
            decimal wallet = 0m;
            foreach (var item in doc.RootElement.EnumerateArray())
            {
                if (!item.TryGetProperty("asset", out var asset) ||
                    !string.Equals(asset.GetString(), "USDT", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                available = Parse(item.TryGetProperty("availableBalance", out var ab) ? ab.GetString() : "0");
                wallet = Parse(item.TryGetProperty("balance", out var bal) ? bal.GetString() : "0");
                break;
            }

            return new BinanceFuturesAccountSummary
            {
                Connected = true,
                EnvironmentName = envName,
                BaseUrl = baseUrl,
                UsdtAvailable = available,
                UsdtWalletBalance = wallet,
                Message = "Conexion Futures OK"
            };
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "No se pudo leer balance Futures.");
            return new BinanceFuturesAccountSummary
            {
                Connected = false,
                EnvironmentName = envName,
                BaseUrl = baseUrl,
                Message = ex.Message.Length > 180 ? ex.Message[..180] : ex.Message
            };
        }
    }

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

    public async Task<decimal> GetPositionQuantityAsync(string symbol, string positionSide, CancellationToken ct = default)
    {
        symbol = (symbol ?? string.Empty).Trim().ToUpperInvariant();
        var pos = (positionSide ?? "LONG").Trim().ToUpperInvariant();
        if (string.IsNullOrWhiteSpace(symbol) || pos is not ("LONG" or "SHORT"))
        {
            return 0m;
        }

        var open = await GetOpenPositionsAsync(ct);
        return open.FirstOrDefault(p =>
            string.Equals(p.Symbol, symbol, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(p.PositionSide, pos, StringComparison.OrdinalIgnoreCase))?.Quantity ?? 0m;
    }

    public async Task<IReadOnlyList<FuturesOpenPosition>> GetOpenPositionsAsync(CancellationToken ct = default)
    {
        var settings = await settingsService.GetActiveSettingsAsync();
        if (!HasFuturesCredentials(settings))
        {
            return [];
        }

        try
        {
            var baseUrl = settingsService.ResolveFuturesBaseUrl(settings);
            await SyncServerTimeOffsetAsync(baseUrl, ct);
            var query = $"recvWindow={DefaultRecvWindow}&timestamp={NowMsWithOffset()}";
            var signature = Sign(query, settings.FuturesApiSecret);
            using var request = new HttpRequestMessage(HttpMethod.Get, $"{baseUrl}/fapi/v2/positionRisk?{query}&signature={signature}");
            request.Headers.TryAddWithoutValidation("X-MBX-APIKEY", settings.FuturesApiKey);
            var response = await httpClient.SendAsync(request, ct);
            var body = await response.Content.ReadAsStringAsync(ct);
            if (!response.IsSuccessStatusCode)
            {
                logger.LogDebug("positionRisk fail: {Err}", ExtractBinanceError(body));
                return [];
            }

            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.ValueKind != JsonValueKind.Array)
            {
                return [];
            }

            var list = new List<FuturesOpenPosition>();
            foreach (var item in doc.RootElement.EnumerateArray())
            {
                var sym = item.TryGetProperty("symbol", out var s) ? s.GetString() ?? "" : "";
                var amt = Parse(item.TryGetProperty("positionAmt", out var a) ? a.GetString() : "0");
                if (string.IsNullOrWhiteSpace(sym) || amt == 0m)
                {
                    continue;
                }

                var sideRaw = item.TryGetProperty("positionSide", out var ps) ? ps.GetString() ?? "BOTH" : "BOTH";
                string side;
                if (string.Equals(sideRaw, "LONG", StringComparison.OrdinalIgnoreCase))
                {
                    side = "LONG";
                }
                else if (string.Equals(sideRaw, "SHORT", StringComparison.OrdinalIgnoreCase))
                {
                    side = "SHORT";
                }
                else
                {
                    // one-way: amt>0 long, amt<0 short
                    side = amt > 0m ? "LONG" : "SHORT";
                }

                list.Add(new FuturesOpenPosition(sym.ToUpperInvariant(), side, Math.Abs(amt)));
            }

            return list;
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "GetOpenPositionsAsync failed");
            return [];
        }
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
            _lastExecutionError = HasFuturesCredentials(settings)
                ? "Live trading Futures deshabilitado por configuracion/guardas."
                : "Configura Futures API Key y Secret en Settings (demo.binance.com).";
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
        decimal? markPrice = null;
        // Binance USD-M suele exigir >=20 USDT aunque el filtro diga menos.
        var minNotional = Math.Max(filters.MinNotional > 0m ? filters.MinNotional : 5m, 20m);
        if (quoteIsNotional)
        {
            var price = await GetMarkPriceAsync(settings, symbol, ct);
            if (price <= 0m)
            {
                _lastExecutionError = $"No se pudo leer precio Futures de {symbol}.";
                return null;
            }

            markPrice = price;
            var requested = decimal.Round(Math.Max(quoteUsdtOrCloseQty, 0m), 8, MidpointRounding.ToZero);
            // Auto-sube al minimo del simbolo (p.ej. BTC ~100) para que el copy no falle en OPEN.
            var targetNotional = Math.Max(requested, minNotional);
            if (targetNotional > requested)
            {
                logger.LogInformation(
                    "Futures {Symbol}: notional {Req} → {Target} (min {Min})",
                    symbol, Fmt(requested), Fmt(targetNotional), Fmt(minNotional));
            }

            qty = QuantityForNotional(targetNotional, price, filters.StepSize, filters.MinQty, minNotional);
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

        var posBefore = await GetPositionQuantityAsync(symbol, pos, ct);
        var fill = await SendOrderAsync(settings, symbol, side, positionSideParam, qty, reduceOnly: !open, ct);
        // Tras redondeo a veces queda 19.xx < 20 → Binance -4164; subir 1–3 steps y reintentar.
        if (fill is null && quoteIsNotional && markPrice is > 0m && IsNotionalTooSmall(_lastExecutionError))
        {
            for (var i = 0; i < 3 && fill is null; i++)
            {
                qty = decimal.Round(qty + filters.StepSize, 8, MidpointRounding.AwayFromZero);
                if (qty * markPrice.Value < minNotional)
                {
                    qty = QuantityForNotional(minNotional * 1.05m, markPrice.Value, filters.StepSize, filters.MinQty, minNotional);
                }

                logger.LogInformation("Futures retry notional {Symbol} qty={Qty}", symbol, Fmt(qty));
                fill = await SendOrderAsync(settings, symbol, side, positionSideParam, qty, reduceOnly: !open, ct);
            }
        }

        if (fill is null)
        {
            return null;
        }

        // Demo a veces responde HTTP 200 con executedQty=0: recuperar por poll / delta de posicion.
        fill = await EnsureCompleteFillAsync(
            settings, symbol, pos, open, qty, posBefore, markPrice, fill, side, positionSideParam, ct);

        if (fill is null || fill.ExecutedQuantity <= 0m)
        {
            _lastExecutionError = string.IsNullOrWhiteSpace(_lastExecutionError)
                ? $"Orden Futures {symbol} sin fill verificable (qty=0)."
                : _lastExecutionError;
            return null;
        }

        if (fill.AveragePrice <= 0m)
        {
            var px = markPrice is > 0m
                ? markPrice.Value
                : await GetMarkPriceAsync(settings, symbol, ct);
            fill = CloneFill(fill, averagePrice: px);
        }

        fill = await EnrichFillFromUserTradesAsync(settings, symbol, fill, ct);
        if (fill.CommissionUsdt <= 0m && fill.ExecutedQuantity > 0m && fill.AveragePrice > 0m)
        {
            fill = CloneFill(
                fill,
                commissionUsdt: EstimateFuturesFeeUsdt(fill.ExecutedQuantity, fill.AveragePrice),
                feeIsEstimated: true);
        }

        return fill;
    }

    private static TradeFillResult CloneFill(
        TradeFillResult fill,
        decimal? executedQuantity = null,
        decimal? averagePrice = null,
        decimal? commissionUsdt = null,
        decimal? realizedPnlUsdt = null,
        bool? feeIsEstimated = null,
        long? orderId = null) =>
        new()
        {
            ExecutedQuantity = executedQuantity ?? fill.ExecutedQuantity,
            AveragePrice = averagePrice ?? fill.AveragePrice,
            CommissionUsdt = commissionUsdt ?? fill.CommissionUsdt,
            RealizedPnlUsdt = realizedPnlUsdt ?? fill.RealizedPnlUsdt,
            FeeIsEstimated = feeIsEstimated ?? fill.FeeIsEstimated,
            OrderId = orderId ?? fill.OrderId
        };

    private static decimal EstimateFuturesFeeUsdt(decimal qty, decimal price) =>
        qty > 0m && price > 0m
            ? decimal.Round(qty * price * 0.0004m, 8, MidpointRounding.AwayFromZero) // ~4 bps taker VIP0
            : 0m;

    private async Task<TradeFillResult> EnrichFillFromUserTradesAsync(
        BinanceConnectionSettings settings,
        string symbol,
        TradeFillResult fill,
        CancellationToken ct)
    {
        if (fill.OrderId <= 0)
        {
            return fill;
        }

        try
        {
            var baseUrl = settingsService.ResolveFuturesBaseUrl(settings);
            await SyncServerTimeOffsetAsync(baseUrl, ct);
            var query =
                $"symbol={symbol}&orderId={fill.OrderId}&recvWindow={DefaultRecvWindow}&timestamp={NowMsWithOffset()}";
            var signature = Sign(query, settings.FuturesApiSecret);
            using var request = new HttpRequestMessage(
                HttpMethod.Get,
                $"{baseUrl}/fapi/v1/userTrades?{query}&signature={signature}");
            request.Headers.TryAddWithoutValidation("X-MBX-APIKEY", settings.FuturesApiKey);
            var response = await httpClient.SendAsync(request, ct);
            if (!response.IsSuccessStatusCode)
            {
                return fill;
            }

            var body = await response.Content.ReadAsStringAsync(ct);
            var trades = JsonSerializer.Deserialize<List<FuturesUserTrade>>(body, JsonOpts());
            if (trades is null || trades.Count == 0)
            {
                return fill;
            }

            decimal commission = 0m;
            decimal realized = 0m;
            var estimated = false;
            foreach (var t in trades)
            {
                var c = Parse(t.Commission);
                var asset = (t.CommissionAsset ?? string.Empty).Trim().ToUpperInvariant();
                if (asset is "USDT" or "USDC" or "FDUSD" or "BUSD")
                {
                    commission += c;
                }
                else if (c > 0m)
                {
                    // BNB u otro: aproximar 4 bps del notional del trade.
                    var q = Parse(t.Qty);
                    var p = Parse(t.Price);
                    if (q > 0m && p > 0m)
                    {
                        commission += q * p * 0.0004m;
                        estimated = true;
                    }
                }

                realized += Parse(t.RealizedPnl);
            }

            return CloneFill(
                fill,
                commissionUsdt: decimal.Round(commission, 8, MidpointRounding.AwayFromZero),
                realizedPnlUsdt: decimal.Round(realized, 8, MidpointRounding.AwayFromZero),
                feeIsEstimated: estimated);
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Futures userTrades enrich failed {Symbol} order={OrderId}", symbol, fill.OrderId);
            return fill;
        }
    }

    /// <summary>
    /// Garantiza fill con qty&gt;0: recovery de posicion, reintento de orden, o cierre flat verificado.
    /// Nunca devuelve exito con qty=0.
    /// </summary>
    private async Task<TradeFillResult?> EnsureCompleteFillAsync(
        BinanceConnectionSettings settings,
        string symbol,
        string positionSide,
        bool open,
        decimal requestedQty,
        decimal posBefore,
        decimal? markPrice,
        TradeFillResult fill,
        string side,
        string positionSideParam,
        CancellationToken ct)
    {
        if (fill.ExecutedQuantity > 0m)
        {
            return fill;
        }

        var recovered = await RecoverFillFromPositionAsync(
            settings, symbol, positionSide, open, requestedQty, posBefore, markPrice, ct);
        if (recovered is { ExecutedQuantity: > 0m })
        {
            return recovered;
        }

        // Segundo intento de mercado (Demo a veces traga el primero sin fill legible).
        logger.LogWarning("Futures {Symbol} fill qty=0 tras 1er intento; reenviando orden.", symbol);
        var posMid = await GetPositionQuantityAsync(symbol, positionSide, ct);
        var retry = await SendOrderAsync(settings, symbol, side, positionSideParam, requestedQty, reduceOnly: !open, ct);
        if (retry is { ExecutedQuantity: > 0m })
        {
            return retry;
        }

        recovered = await RecoverFillFromPositionAsync(
            settings, symbol, positionSide, open, requestedQty, posMid, markPrice, ct);
        if (recovered is { ExecutedQuantity: > 0m })
        {
            return recovered;
        }

        if (!open)
        {
            var live = await GetPositionQuantityAsync(symbol, positionSide, ct);
            if (live <= 0m && posBefore > 0m)
            {
                var px = markPrice is > 0m
                    ? markPrice.Value
                    : await GetMarkPriceAsync(settings, symbol, ct);
                return new TradeFillResult
                {
                    ExecutedQuantity = posBefore,
                    AveragePrice = px > 0m ? px : 0m,
                    OrderId = fill.OrderId
                };
            }
        }

        _lastExecutionError = $"Orden Futures {symbol} sin fill verificable (qty=0 tras retry).";
        return null;
    }

    private async Task<TradeFillResult?> RecoverFillFromPositionAsync(
        BinanceConnectionSettings settings,
        string symbol,
        string positionSide,
        bool open,
        decimal requestedQty,
        decimal posBefore,
        decimal? markPrice,
        CancellationToken ct)
    {
        // Varias lecturas: Demo tarda en reflejar positionRisk.
        for (var attempt = 0; attempt < 4; attempt++)
        {
            await Task.Delay(300 + attempt * 200, ct);
            var live = await GetPositionQuantityAsync(symbol, positionSide, ct);
            var px = markPrice is > 0m
                ? markPrice.Value
                : await GetMarkPriceAsync(settings, symbol, ct);

            if (open)
            {
                var delta = live - posBefore;
                if (delta > 0m)
                {
                    return new TradeFillResult { ExecutedQuantity = delta, AveragePrice = px };
                }

                if (live > 0m && posBefore <= 0m)
                {
                    return new TradeFillResult
                    {
                        ExecutedQuantity = Math.Min(live, requestedQty > 0m ? requestedQty : live),
                        AveragePrice = px
                    };
                }
            }
            else
            {
                var closed = posBefore - live;
                if (closed > 0m)
                {
                    return new TradeFillResult { ExecutedQuantity = closed, AveragePrice = px };
                }

                if (live <= 0m && posBefore > 0m)
                {
                    return new TradeFillResult { ExecutedQuantity = posBefore, AveragePrice = px };
                }
            }
        }

        return null;
    }

    private async Task<TradeFillResult?> SendOrderAsync(
        BinanceConnectionSettings settings,
        string symbol,
        string side,
        string positionSide,
        decimal quantity,
        bool reduceOnly,
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
                (reduceOnly && positionSide is "BOTH" ? "&reduceOnly=true" : "") +
                $"&recvWindow={DefaultRecvWindow}&timestamp={NowMsWithOffset()}";
            var signature = Sign(query, settings.FuturesApiSecret);
            using var request = new HttpRequestMessage(HttpMethod.Post, $"{baseUrl}/fapi/v1/order?{query}&signature={signature}");
            request.Headers.TryAddWithoutValidation("X-MBX-APIKEY", settings.FuturesApiKey);
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

            var fill = ParseFill(order, quantity);
            if (fill.ExecutedQuantity > 0m)
            {
                return fill;
            }

            // Poll orden: Demo a veces confirma despues.
            var orderIdStr = order.OrderId > 0 ? order.OrderId.ToString(CultureInfo.InvariantCulture) : null;
            if (!string.IsNullOrWhiteSpace(orderIdStr) || !string.IsNullOrWhiteSpace(order.ClientOrderId))
            {
                for (var i = 0; i < 6; i++)
                {
                    await Task.Delay(250, ct);
                    var polled = await GetOrderAsync(settings, baseUrl, symbol, orderIdStr, order.ClientOrderId, ct);
                    if (polled is null)
                    {
                        continue;
                    }

                    fill = ParseFill(polled, quantity);
                    if (fill.ExecutedQuantity > 0m ||
                        string.Equals(polled.Status, "FILLED", StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(polled.Status, "CANCELED", StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(polled.Status, "EXPIRED", StringComparison.OrdinalIgnoreCase))
                    {
                        if (fill.ExecutedQuantity <= 0m &&
                            string.Equals(polled.Status, "FILLED", StringComparison.OrdinalIgnoreCase))
                        {
                            fill = new TradeFillResult
                            {
                                ExecutedQuantity = quantity,
                                AveragePrice = fill.AveragePrice
                            };
                        }

                        return fill;
                    }
                }
            }

            return fill;
        }
        catch (Exception ex)
        {
            _lastExecutionError = ex.Message.Length > 180 ? ex.Message[..180] : ex.Message;
            logger.LogWarning(ex, "Error orden Futures {Symbol}", symbol);
            return null;
        }
    }

    private async Task<FuturesOrderResponse?> GetOrderAsync(
        BinanceConnectionSettings settings,
        string baseUrl,
        string symbol,
        string? orderId,
        string? clientOrderId,
        CancellationToken ct)
    {
        try
        {
            await SyncServerTimeOffsetAsync(baseUrl, ct);
            var idPart = !string.IsNullOrWhiteSpace(orderId)
                ? $"orderId={orderId}"
                : $"origClientOrderId={Uri.EscapeDataString(clientOrderId!)}";
            var query = $"symbol={symbol}&{idPart}&recvWindow={DefaultRecvWindow}&timestamp={NowMsWithOffset()}";
            var signature = Sign(query, settings.FuturesApiSecret);
            using var request = new HttpRequestMessage(HttpMethod.Get, $"{baseUrl}/fapi/v1/order?{query}&signature={signature}");
            request.Headers.TryAddWithoutValidation("X-MBX-APIKEY", settings.FuturesApiKey);
            var response = await httpClient.SendAsync(request, ct);
            var body = await response.Content.ReadAsStringAsync(ct);
            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            return JsonSerializer.Deserialize<FuturesOrderResponse>(body, JsonOpts());
        }
        catch
        {
            return null;
        }
    }

    private static TradeFillResult ParseFill(FuturesOrderResponse order, decimal fallbackQty)
    {
        var qty = Parse(order.ExecutedQty);
        var avg = Parse(order.AvgPrice);
        var cum = Parse(order.CumQuote);
        if (avg <= 0m && qty > 0m && cum > 0m)
        {
            avg = cum / qty;
        }

        if (qty <= 0m && cum > 0m && avg > 0m)
        {
            qty = decimal.Round(cum / avg, 8, MidpointRounding.ToZero);
        }

        if (qty <= 0m &&
            string.Equals(order.Status, "FILLED", StringComparison.OrdinalIgnoreCase) &&
            fallbackQty > 0m)
        {
            qty = fallbackQty;
        }

        return new TradeFillResult
        {
            ExecutedQuantity = qty,
            AveragePrice = avg,
            OrderId = order.OrderId
        };
    }

    private async Task TrySetLeverageAsync(BinanceConnectionSettings settings, string symbol, int leverage, CancellationToken ct)
    {
        try
        {
            var baseUrl = settingsService.ResolveFuturesBaseUrl(settings);
            await SyncServerTimeOffsetAsync(baseUrl, ct);
            var query = $"symbol={symbol}&leverage={leverage}&recvWindow={DefaultRecvWindow}&timestamp={NowMsWithOffset()}";
            var signature = Sign(query, settings.FuturesApiSecret);
            using var request = new HttpRequestMessage(HttpMethod.Post, $"{baseUrl}/fapi/v1/leverage?{query}&signature={signature}");
            request.Headers.TryAddWithoutValidation("X-MBX-APIKEY", settings.FuturesApiKey);
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
            var signature = Sign(query, settings.FuturesApiSecret);
            using var request = new HttpRequestMessage(HttpMethod.Get, $"{baseUrl}/fapi/v1/positionSide/dual?{query}&signature={signature}");
            request.Headers.TryAddWithoutValidation("X-MBX-APIKEY", settings.FuturesApiKey);
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
            var minNotional = s.Filters?.FirstOrDefault(x =>
                x.FilterType is "MIN_NOTIONAL" or "NOTIONAL");
            return new SymbolFilters
            {
                MinQty = FirstPositive(Parse(marketLot?.MinQty), Parse(lot?.MinQty)),
                StepSize = FirstPositive(Parse(marketLot?.StepSize), Parse(lot?.StepSize), 0.001m),
                MinNotional = FirstPositive(Parse(minNotional?.Notional), Parse(minNotional?.MinNotional), 5m)
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

    private static bool HasFuturesCredentials(BinanceConnectionSettings settings) =>
        !string.IsNullOrWhiteSpace(settings.FuturesApiKey) &&
        !string.IsNullOrWhiteSpace(settings.FuturesApiSecret);

    private static bool CanTradeReal(BinanceConnectionSettings settings) =>
        settings.IsEnabled &&
        settings.ExecutionMode == TradeExecutionMode.Live &&
        settings.LiveEnabledByChecklist &&
        !settings.GlobalKillSwitch &&
        (settings.Environment != BinanceEnvironment.Production || settings.LiveSafetyConfirmed) &&
        HasFuturesCredentials(settings);

    private static decimal NormalizeQuantity(decimal qty, decimal stepSize, decimal minQty)
    {
        if (stepSize <= 0)
        {
            return qty < minQty ? 0m : qty;
        }

        var steps = Math.Floor(qty / stepSize);
        var normalized = steps * stepSize;
        if (normalized < minQty)
        {
            return 0m;
        }

        return decimal.Round(normalized, 8, MidpointRounding.ToZero);
    }

    /// <summary>Ceil a step size y asegura qty*price >= minNotional (evita -4164 tras floor).</summary>
    private static decimal QuantityForNotional(
        decimal notional,
        decimal price,
        decimal stepSize,
        decimal minQty,
        decimal minNotional)
    {
        if (price <= 0m)
        {
            return 0m;
        }

        var step = stepSize > 0m ? stepSize : 0.001m;
        var raw = Math.Max(notional, minNotional) / price;
        var steps = Math.Ceiling(raw / step);
        var qty = decimal.Round(steps * step, 8, MidpointRounding.AwayFromZero);
        if (minQty > 0m && qty < minQty)
        {
            qty = decimal.Round(Math.Ceiling(minQty / step) * step, 8, MidpointRounding.AwayFromZero);
        }

        for (var i = 0; i < 25 && qty * price < minNotional; i++)
        {
            qty = decimal.Round(qty + step, 8, MidpointRounding.AwayFromZero);
        }

        return qty * price >= minNotional ? qty : 0m;
    }

    private static bool IsNotionalTooSmall(string? err) =>
        !string.IsNullOrWhiteSpace(err) &&
        (err.Contains("-4164", StringComparison.Ordinal) ||
         err.Contains("no smaller than", StringComparison.OrdinalIgnoreCase) ||
         err.Contains("bajo minimo", StringComparison.OrdinalIgnoreCase));

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
        public long OrderId { get; set; }
        public string ClientOrderId { get; set; } = string.Empty;
        public string Status { get; set; } = string.Empty;
        public string OrigQty { get; set; } = "0";
        public string ExecutedQty { get; set; } = "0";
        public string AvgPrice { get; set; } = "0";
        public string CumQuote { get; set; } = "0";
    }

    private sealed class FuturesUserTrade
    {
        public string Qty { get; set; } = "0";
        public string Price { get; set; } = "0";
        public string Commission { get; set; } = "0";
        public string CommissionAsset { get; set; } = string.Empty;
        public string RealizedPnl { get; set; } = "0";
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
        public string MinNotional { get; set; } = "0";
    }

    private sealed class SymbolFilters
    {
        public decimal MinQty { get; init; }
        public decimal StepSize { get; init; }
        public decimal MinNotional { get; init; }
    }
}
