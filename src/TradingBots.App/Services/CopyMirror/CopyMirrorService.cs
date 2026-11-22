using System.Collections.Concurrent;

namespace TradingBots.App.Services.CopyMirror;

public interface ICopyMirrorService
{
    CopyMirrorStatus GetStatus();
    void UpdateSettings(CopyMirrorSettings settings);
    void Stop();
    int GetRecommendedDelayMs();
    Task<IReadOnlyList<CopyLeadSummary>> GetLeaderboardAsync(CancellationToken ct = default);
    Task PollOnceAsync(IServiceProvider scopedServices, CancellationToken ct = default);
}

public sealed class CopyMirrorService(
    IBinanceCopyTradingLeadClient leadClient,
    ILogger<CopyMirrorService> logger) : ICopyMirrorService
{
    private readonly object _gate = new();
    private CopyMirrorSettings _settings = new();
    private readonly ConcurrentDictionary<string, byte> _seen = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, decimal> _openQty = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentQueue<CopyMirrorEvent> _events = new();
    private bool _warmingUp;
    private DateTime? _lastPollUtc;
    private DateTime? _lastSuccessUtc;
    private string _lastError = string.Empty;
    private int _lastPollMs;
    private int _mirroredBuys;
    private int _mirroredSells;
    private int _skipped;
    private int _failStreak;

    public CopyMirrorStatus GetStatus()
    {
        lock (_gate)
        {
            return new CopyMirrorStatus
            {
                Settings = CloneSettings(_settings),
                WarmingUp = _warmingUp,
                LastPollUtc = _lastPollUtc,
                LastSuccessUtc = _lastSuccessUtc,
                LastError = _lastError,
                SeenOrders = _seen.Count,
                MirroredBuys = _mirroredBuys,
                MirroredSells = _mirroredSells,
                Skipped = _skipped,
                LastPollMs = _lastPollMs,
                FailStreak = _failStreak,
                RecommendedDelayMs = ComputeRecommendedDelayMsUnlocked(),
                RecentEvents = _events.Reverse().Take(60).ToList(),
                RecentMovements = _events
                    .Reverse()
                    .Where(e => e.IsMovement && e.Action is "BUY" or "SELL" or "SKIP")
                    .Take(50)
                    .ToList(),
                OpenSpotQty = new Dictionary<string, decimal>(_openQty, StringComparer.OrdinalIgnoreCase)
            };
        }
    }

    public int GetRecommendedDelayMs()
    {
        lock (_gate)
        {
            return ComputeRecommendedDelayMsUnlocked();
        }
    }

    public void UpdateSettings(CopyMirrorSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        lock (_gate)
        {
            var leadChanged = !string.Equals(_settings.LeadPortfolioId, settings.LeadPortfolioId, StringComparison.Ordinal);
            _settings = Normalize(settings);
            if (leadChanged || !_settings.Enabled)
            {
                _seen.Clear();
                _openQty.Clear();
                _warmingUp = _settings.Enabled && !string.IsNullOrWhiteSpace(_settings.LeadPortfolioId);
                _failStreak = 0;
                _lastError = string.Empty;
            }

            PushEvent(_settings.Enabled ? "info" : "info",
                _settings.Enabled
                    ? $"Mirror {(_settings.DryRun ? "DRY-RUN" : "LIVE")} → {_settings.LeadNickname} ({_settings.LeadPortfolioId}), {Fmt(_settings.QuotePerTradeUsdt)} USDT/trade, poll {_settings.PollIntervalMs}ms."
                    : "Mirror detenido.");
        }
    }

    public void Stop()
    {
        lock (_gate)
        {
            _settings.Enabled = false;
            _warmingUp = false;
            PushEvent("info", "Mirror detenido.");
        }
    }

    public Task<IReadOnlyList<CopyLeadSummary>> GetLeaderboardAsync(CancellationToken ct = default) =>
        leadClient.GetLeaderboardAsync(ct: ct);

    public async Task PollOnceAsync(IServiceProvider scopedServices, CancellationToken ct = default)
    {
        CopyMirrorSettings settings;
        lock (_gate)
        {
            settings = CloneSettings(_settings);
        }

        if (!settings.Enabled || string.IsNullOrWhiteSpace(settings.LeadPortfolioId))
        {
            return;
        }

        try
        {
            var (orders, pollMs) = await leadClient.GetOrderHistoryAsync(settings.LeadPortfolioId, ct: ct);
            lock (_gate)
            {
                _lastPollUtc = DateTime.UtcNow;
                _lastSuccessUtc = DateTime.UtcNow;
                _lastPollMs = pollMs;
                _lastError = string.Empty;
                _failStreak = 0;
            }

            // API returns newest first.
            var chronological = orders.OrderBy(o => o.OrderTimeMs).ToList();
            bool warmingUp;
            lock (_gate)
            {
                warmingUp = _warmingUp;
            }

            if (warmingUp)
            {
                foreach (var order in chronological)
                {
                    _seen.TryAdd(order.Fingerprint, 0);
                }

                lock (_gate)
                {
                    _warmingUp = false;
                }

                PushEvent("info", $"Warm-up OK: {_seen.Count} ordenes históricas marcadas (no se replican).");
                return;
            }

            foreach (var order in chronological)
            {
                if (!_seen.TryAdd(order.Fingerprint, 0))
                {
                    continue;
                }

                await HandleNewOrderAsync(order, settings, scopedServices, ct);
            }
        }
        catch (Exception ex)
        {
            int streak;
            lock (_gate)
            {
                _lastPollUtc = DateTime.UtcNow;
                _lastError = ex.Message;
                _failStreak++;
                streak = _failStreak;
            }

            var busy = IsBinanceBusy(ex.Message);
            logger.LogWarning(ex, "Copy mirror poll failed (streak={Streak}, busy={Busy})", streak, busy);
            // Binance "busy"/rate-limit: no spamear el log; el hosted service sube el delay.
            if (!busy && (streak <= 2 || streak % 10 == 0))
            {
                PushEvent("error", $"Poll fail: {ex.Message}");
            }
            else if (busy && (streak == 1 || streak % 5 == 0))
            {
                PushEvent("error",
                    $"Binance order-history ocupado/rate-limit (intento {streak}). Reintentando mas lento; no es por dry-run/LIVE.");
            }
        }
    }

    private int ComputeRecommendedDelayMsUnlocked()
    {
        var baseMs = Math.Clamp(_settings.PollIntervalMs <= 0 ? 3500 : _settings.PollIntervalMs, 2500, 30_000);
        if (!_settings.Enabled || _failStreak <= 0)
        {
            return _settings.Enabled ? baseMs : 3000;
        }

        // Backoff exponencial suave ante busy/rate-limit (hasta 45s).
        var extra = Math.Min(40_000, (int)(1500 * Math.Pow(1.65, Math.Min(_failStreak, 10))));
        return Math.Min(45_000, baseMs + extra);
    }

    private static bool IsBinanceBusy(string? message)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            return false;
        }

        return message.Contains("busy", StringComparison.OrdinalIgnoreCase) ||
               message.Contains("try again later", StringComparison.OrdinalIgnoreCase) ||
               message.Contains("rate", StringComparison.OrdinalIgnoreCase) ||
               message.Contains("429", StringComparison.OrdinalIgnoreCase) ||
               message.Contains("418", StringComparison.OrdinalIgnoreCase);
    }

    private async Task HandleNewOrderAsync(
        CopyLeadOrder order,
        CopyMirrorSettings settings,
        IServiceProvider scopedServices,
        CancellationToken ct)
    {
        var detectUtc = DateTime.UtcNow;
        var detectDelayMs = order.OrderTimeMs > 0
            ? (int)Math.Max(0, (detectUtc - DateTimeOffset.FromUnixTimeMilliseconds(order.OrderTimeMs).UtcDateTime).TotalMilliseconds)
            : (int?)null;

        var leadAction = string.Equals(order.Side, "BUY", StringComparison.OrdinalIgnoreCase) ? "BUY"
            : string.Equals(order.Side, "SELL", StringComparison.OrdinalIgnoreCase) ? "SELL"
            : order.Side;
        PushEvent("detected",
            $"LEAD {leadAction} {order.PositionSide} {order.Symbol} qty={Fmt(order.ExecutedQty)} @ {Fmt(order.AvgPrice)}",
            order.Symbol, leadAction, "lead", order.ExecutedQty, order.AvgPrice, null,
            order.OrderTimeMs, detectDelayMs);

        // Binance a veces manda LONG/SHORT; muchos leads usan BOTH (one-way).
        // Spot mirror: BUY+LONG/BOTH => compra; SELL+LONG/BOTH => venta; SHORT se ignora si LongOnly.
        var pos = order.PositionSide ?? string.Empty;
        var isShort = pos.Equals("SHORT", StringComparison.OrdinalIgnoreCase);
        var isLongLike = pos.Equals("LONG", StringComparison.OrdinalIgnoreCase) ||
                         pos.Equals("BOTH", StringComparison.OrdinalIgnoreCase) ||
                         string.IsNullOrWhiteSpace(pos);

        if (settings.LongOnly && isShort)
        {
            Interlocked.Increment(ref _skipped);
            PushEvent("skipped", $"SKIP SHORT: {order.Symbol} {order.Side}/{order.PositionSide}",
                order.Symbol, "SKIP", "lead", order.ExecutedQty, order.AvgPrice, null,
                order.OrderTimeMs, detectDelayMs);
            return;
        }

        var isBuy = order.Side.Equals("BUY", StringComparison.OrdinalIgnoreCase) && isLongLike;
        var isSell = order.Side.Equals("SELL", StringComparison.OrdinalIgnoreCase) && isLongLike;

        if (!isBuy && !isSell)
        {
            Interlocked.Increment(ref _skipped);
            PushEvent("skipped", $"SKIP lado no mapeable: {order.Side}/{order.PositionSide}",
                order.Symbol, "SKIP", "lead", null, null, null,
                order.OrderTimeMs, detectDelayMs);
            return;
        }

        var execution = scopedServices.GetRequiredService<IBinanceTradeExecutionService>();
        // Leads Futures operan pares que a menudo NO existen en Spot (testnet ni mainnet).
        if (!await execution.IsSpotSymbolTradableAsync(order.Symbol, ct))
        {
            Interlocked.Increment(ref _skipped);
            PushEvent("skipped",
                $"SKIP {order.Symbol}: no cotiza en Spot del entorno activo (tipico Futures-only). Elige un lead que opere BTC/ETH u otros Spot.",
                order.Symbol, "SKIP", settings.DryRun ? "dryrun" : "live", order.ExecutedQty, order.AvgPrice, null,
                order.OrderTimeMs, detectDelayMs);
            return;
        }

        if (settings.DryRun)
        {
            if (isBuy)
            {
                Interlocked.Increment(ref _mirroredBuys);
                PushEvent("dryrun",
                    $"COMPRA (dry-run) {order.Symbol} ~{Fmt(settings.QuotePerTradeUsdt)} USDT",
                    order.Symbol, "BUY", "dryrun", null, order.AvgPrice, settings.QuotePerTradeUsdt,
                    order.OrderTimeMs, detectDelayMs);
            }
            else
            {
                Interlocked.Increment(ref _mirroredSells);
                PushEvent("dryrun",
                    $"VENTA (dry-run) {order.Symbol} — cerraria posicion mirror si hubiera",
                    order.Symbol, "SELL", "dryrun", null, order.AvgPrice, null,
                    order.OrderTimeMs, detectDelayMs);
            }

            return;
        }

        var execStart = DateTime.UtcNow;

        if (isBuy)
        {
            var fill = await execution.MarketBuyAsync(order.Symbol, settings.QuotePerTradeUsdt, botId: null, ct);
            var execDelay = (int)(DateTime.UtcNow - execStart).TotalMilliseconds;
            if (fill is null)
            {
                Interlocked.Increment(ref _skipped);
                PushEvent("error",
                    $"COMPRA FALLIDA {order.Symbol}: {execution.GetLastExecutionError()}",
                    order.Symbol, "BUY", "live", null, null, settings.QuotePerTradeUsdt,
                    order.OrderTimeMs, detectDelayMs, execDelay);
                return;
            }

            _openQty.AddOrUpdate(order.Symbol, fill.ExecutedQuantity, (_, cur) => cur + fill.ExecutedQuantity);
            Interlocked.Increment(ref _mirroredBuys);
            PushEvent("executed",
                $"COMPRA {order.Symbol} qty={Fmt(fill.ExecutedQuantity)} @ {Fmt(fill.AveragePrice)}",
                order.Symbol, "BUY", "live", fill.ExecutedQuantity, fill.AveragePrice, settings.QuotePerTradeUsdt,
                order.OrderTimeMs, detectDelayMs, execDelay);
            return;
        }

        if (!_openQty.TryGetValue(order.Symbol, out var qty) || qty <= 0m)
        {
            Interlocked.Increment(ref _skipped);
            PushEvent("skipped",
                $"SKIP VENTA: sin posicion mirror abierta en {order.Symbol}",
                order.Symbol, "SKIP", "live", null, null, null,
                order.OrderTimeMs, detectDelayMs);
            return;
        }

        var sellFill = await execution.MarketSellAsync(order.Symbol, qty, botId: null, ct);
        var sellExecDelay = (int)(DateTime.UtcNow - execStart).TotalMilliseconds;
        if (sellFill is null)
        {
            Interlocked.Increment(ref _skipped);
            PushEvent("error",
                $"VENTA FALLIDA {order.Symbol}: {execution.GetLastExecutionError()}",
                order.Symbol, "SELL", "live", qty, null, null,
                order.OrderTimeMs, detectDelayMs, sellExecDelay);
            return;
        }

        _openQty.TryRemove(order.Symbol, out _);
        Interlocked.Increment(ref _mirroredSells);
        PushEvent("executed",
            $"VENTA {order.Symbol} qty={Fmt(sellFill.ExecutedQuantity)} @ {Fmt(sellFill.AveragePrice)}",
            order.Symbol, "SELL", "live", sellFill.ExecutedQuantity, sellFill.AveragePrice, null,
            order.OrderTimeMs, detectDelayMs, sellExecDelay);
    }

    private void PushEvent(
        string kind,
        string message,
        string? symbol = null,
        string? action = null,
        string? mode = null,
        decimal? qty = null,
        decimal? price = null,
        decimal? quoteUsdt = null,
        long? leadOrderTimeMs = null,
        int? detectDelayMs = null,
        int? execDelayMs = null)
    {
        _events.Enqueue(new CopyMirrorEvent
        {
            Utc = DateTime.UtcNow,
            Kind = kind,
            Message = message,
            Symbol = symbol,
            Action = action,
            Mode = mode,
            Qty = qty,
            Price = price,
            QuoteUsdt = quoteUsdt,
            LeadOrderTimeMs = leadOrderTimeMs,
            DetectDelayMs = detectDelayMs,
            ExecDelayMs = execDelayMs
        });

        while (_events.Count > 200 && _events.TryDequeue(out _))
        {
        }
    }

    private static CopyMirrorSettings Normalize(CopyMirrorSettings s) => new()
    {
        Enabled = s.Enabled,
        DryRun = s.DryRun,
        LongOnly = s.LongOnly,
        LeadPortfolioId = (s.LeadPortfolioId ?? string.Empty).Trim(),
        LeadNickname = (s.LeadNickname ?? string.Empty).Trim(),
        QuotePerTradeUsdt = Math.Clamp(s.QuotePerTradeUsdt <= 0 ? 15m : s.QuotePerTradeUsdt, 5m, 5000m),
        PollIntervalMs = Math.Clamp(s.PollIntervalMs <= 0 ? 3500 : s.PollIntervalMs, 2500, 30_000)
    };

    private static CopyMirrorSettings CloneSettings(CopyMirrorSettings s) => new()
    {
        Enabled = s.Enabled,
        DryRun = s.DryRun,
        LongOnly = s.LongOnly,
        LeadPortfolioId = s.LeadPortfolioId,
        LeadNickname = s.LeadNickname,
        QuotePerTradeUsdt = s.QuotePerTradeUsdt,
        PollIntervalMs = s.PollIntervalMs
    };

    private static string Fmt(decimal v) =>
        v.ToString("0.####", System.Globalization.CultureInfo.InvariantCulture);
}
