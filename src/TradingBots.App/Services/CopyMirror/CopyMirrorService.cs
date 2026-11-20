using System.Collections.Concurrent;

namespace TradingBots.App.Services.CopyMirror;

public interface ICopyMirrorService
{
    CopyMirrorStatus GetStatus();
    void UpdateSettings(CopyMirrorSettings settings);
    void Stop();
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
                RecentEvents = _events.Reverse().Take(40).ToList(),
                OpenSpotQty = new Dictionary<string, decimal>(_openQty, StringComparer.OrdinalIgnoreCase)
            };
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

            logger.LogWarning(ex, "Copy mirror poll failed (streak={Streak})", streak);
            if (streak <= 3 || streak % 10 == 0)
            {
                PushEvent("error", $"Poll fail: {ex.Message}");
            }
        }
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

        PushEvent("detected",
            $"Lead {order.Side} {order.PositionSide} {order.Symbol} qty={Fmt(order.ExecutedQty)} @ {Fmt(order.AvgPrice)}",
            order.Symbol, order.OrderTimeMs, detectDelayMs);

        if (settings.LongOnly &&
            !string.Equals(order.PositionSide, "LONG", StringComparison.OrdinalIgnoreCase))
        {
            Interlocked.Increment(ref _skipped);
            PushEvent("skipped", $"Skip SHORT/no-LONG: {order.Symbol} {order.Side}/{order.PositionSide}",
                order.Symbol, order.OrderTimeMs, detectDelayMs);
            return;
        }

        var isBuy = string.Equals(order.Side, "BUY", StringComparison.OrdinalIgnoreCase)
                    && string.Equals(order.PositionSide, "LONG", StringComparison.OrdinalIgnoreCase);
        var isSell = string.Equals(order.Side, "SELL", StringComparison.OrdinalIgnoreCase)
                     && string.Equals(order.PositionSide, "LONG", StringComparison.OrdinalIgnoreCase);

        if (!isBuy && !isSell)
        {
            Interlocked.Increment(ref _skipped);
            PushEvent("skipped", $"Skip lado no mapeable: {order.Side}/{order.PositionSide}",
                order.Symbol, order.OrderTimeMs, detectDelayMs);
            return;
        }

        if (settings.DryRun)
        {
            if (isBuy)
            {
                Interlocked.Increment(ref _mirroredBuys);
                PushEvent("dryrun",
                    $"DRY BUY {order.Symbol} ~{Fmt(settings.QuotePerTradeUsdt)} USDT (detectDelay={detectDelayMs}ms)",
                    order.Symbol, order.OrderTimeMs, detectDelayMs);
            }
            else
            {
                Interlocked.Increment(ref _mirroredSells);
                PushEvent("dryrun",
                    $"DRY SELL {order.Symbol} (cerrar posicion mirror si hubiera)",
                    order.Symbol, order.OrderTimeMs, detectDelayMs);
            }

            return;
        }

        var execution = scopedServices.GetRequiredService<IBinanceTradeExecutionService>();
        var execStart = DateTime.UtcNow;

        if (isBuy)
        {
            var fill = await execution.MarketBuyAsync(order.Symbol, settings.QuotePerTradeUsdt, botId: null, ct);
            var execDelay = (int)(DateTime.UtcNow - execStart).TotalMilliseconds;
            if (fill is null)
            {
                Interlocked.Increment(ref _skipped);
                PushEvent("error",
                    $"BUY fail {order.Symbol}: {execution.GetLastExecutionError()}",
                    order.Symbol, order.OrderTimeMs, detectDelayMs, execDelay);
                return;
            }

            _openQty.AddOrUpdate(order.Symbol, fill.ExecutedQuantity, (_, cur) => cur + fill.ExecutedQuantity);
            Interlocked.Increment(ref _mirroredBuys);
            PushEvent("executed",
                $"BUY {order.Symbol} qty={Fmt(fill.ExecutedQuantity)} @ {Fmt(fill.AveragePrice)} (detect={detectDelayMs}ms exec={execDelay}ms)",
                order.Symbol, order.OrderTimeMs, detectDelayMs, execDelay);
            return;
        }

        if (!_openQty.TryGetValue(order.Symbol, out var qty) || qty <= 0m)
        {
            Interlocked.Increment(ref _skipped);
            PushEvent("skipped",
                $"SELL sin posicion mirror abierta en {order.Symbol}",
                order.Symbol, order.OrderTimeMs, detectDelayMs);
            return;
        }

        var sellFill = await execution.MarketSellAsync(order.Symbol, qty, botId: null, ct);
        var sellExecDelay = (int)(DateTime.UtcNow - execStart).TotalMilliseconds;
        if (sellFill is null)
        {
            Interlocked.Increment(ref _skipped);
            PushEvent("error",
                $"SELL fail {order.Symbol}: {execution.GetLastExecutionError()}",
                order.Symbol, order.OrderTimeMs, detectDelayMs, sellExecDelay);
            return;
        }

        _openQty.TryRemove(order.Symbol, out _);
        Interlocked.Increment(ref _mirroredSells);
        PushEvent("executed",
            $"SELL {order.Symbol} qty={Fmt(sellFill.ExecutedQuantity)} @ {Fmt(sellFill.AveragePrice)} (detect={detectDelayMs}ms exec={sellExecDelay}ms)",
            order.Symbol, order.OrderTimeMs, detectDelayMs, sellExecDelay);
    }

    private void PushEvent(
        string kind,
        string message,
        string? symbol = null,
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
            LeadOrderTimeMs = leadOrderTimeMs,
            DetectDelayMs = detectDelayMs,
            ExecDelayMs = execDelayMs
        });

        while (_events.Count > 80 && _events.TryDequeue(out _))
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
        PollIntervalMs = Math.Clamp(s.PollIntervalMs <= 0 ? 2000 : s.PollIntervalMs, 1000, 30_000)
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
