using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using TradingBots.App.Data;
using TradingBots.App.Models;

namespace TradingBots.App.Services.CopyMirror;

public interface ICopyMirrorService
{
    CopyMirrorStatus GetStatus();
    /// <summary>Inicia o actualiza un mirror para el lead (multi-mirror, no sustituye otros).</summary>
    void UpdateSettings(CopyMirrorSettings settings);
    void Stop();
    void Stop(string leadPortfolioId);
    int GetRecommendedDelayMs();
    Task RestorePersistedSessionsAsync(CancellationToken ct = default);
    Task<IReadOnlyList<CopyLeadSummary>> GetLeaderboardAsync(
        string dataType = "PNL",
        string nickname = "",
        CancellationToken ct = default);
    Task<CopyLeadBoardResult> GetSpotCompatibleLeaderboardAsync(CancellationToken ct = default);
    Task PollOnceAsync(IServiceProvider scopedServices, CancellationToken ct = default);
}

public sealed class CopyMirrorService(
    IBinanceCopyTradingLeadClient leadClient,
    IServiceScopeFactory scopeFactory,
    ILogger<CopyMirrorService> logger) : ICopyMirrorService
{
    private readonly object _gate = new();
    private readonly Dictionary<string, MirrorSession> _sessions = new(StringComparer.Ordinal);
    private readonly ConcurrentQueue<CopyMirrorEvent> _hubEvents = new();
    private CopyLeadBoardResult? _spotBoardCache;
    private DateTime _spotBoardCacheUtc = DateTime.MinValue;
    private static readonly TimeSpan SpotBoardCacheTtl = TimeSpan.FromMinutes(12);
    private int _restored;

    private sealed class MirrorSession
    {
        public CopyMirrorSettings Settings { get; set; } = new();
        public ConcurrentDictionary<string, byte> Seen { get; } = new(StringComparer.Ordinal);
        public ConcurrentDictionary<string, decimal> OpenQty { get; } = new(StringComparer.OrdinalIgnoreCase);
        public ConcurrentDictionary<string, decimal> OpenFutLong { get; } = new(StringComparer.OrdinalIgnoreCase);
        public ConcurrentDictionary<string, decimal> OpenFutShort { get; } = new(StringComparer.OrdinalIgnoreCase);
        /// <summary>Simbolos Futures bloqueados (p.ej. TradFi sin acuerdo) — no reintentar OPEN ni contar SKIP de cierre.</summary>
        public ConcurrentDictionary<string, string> BlockedFuturesSymbols { get; } = new(StringComparer.OrdinalIgnoreCase);
        public bool WarmingUp { get; set; }
        public DateTime? LastPollUtc { get; set; }
        public DateTime? LastSuccessUtc { get; set; }
        public string LastError { get; set; } = string.Empty;
        public int LastPollMs { get; set; }
        public int MirroredBuys;
        public int MirroredSells;
        public int Skipped;
        public int FailStreak;
    }

    public CopyMirrorStatus GetStatus()
    {
        lock (_gate)
        {
            var sessions = _sessions.Values
                .Where(s => s.Settings.Enabled)
                .Select(ToSessionStatus)
                .OrderBy(s => s.Settings.LeadNickname)
                .ThenBy(s => s.Settings.LeadPortfolioId)
                .ToList();

            var primary = sessions.FirstOrDefault();
            var allOpen = new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase);
            foreach (var s in _sessions.Values.Where(x => x.Settings.Enabled))
            {
                foreach (var kv in ToSessionStatus(s).OpenSpotQty)
                {
                    allOpen[kv.Key] = allOpen.TryGetValue(kv.Key, out var cur) ? cur + kv.Value : kv.Value;
                }
            }

            return new CopyMirrorStatus
            {
                ActiveCount = sessions.Count,
                MaxConcurrent = CopyMirrorStatus.MaxConcurrentMirrors,
                Sessions = sessions,
                RecentEvents = _hubEvents.Reverse().Take(80).ToList(),
                RecentMovements = _hubEvents
                    .Reverse()
                    .Where(e => e.IsMovement && e.Action is "BUY" or "SELL" or "SKIP")
                    .Take(60)
                    .ToList(),
                Settings = primary?.Settings ?? new CopyMirrorSettings(),
                WarmingUp = primary?.WarmingUp ?? false,
                LastPollUtc = MaxNullable(sessions.Select(s => s.LastPollUtc)),
                LastSuccessUtc = MaxNullable(sessions.Select(s => s.LastSuccessUtc)),
                LastError = sessions.FirstOrDefault(s => !string.IsNullOrWhiteSpace(s.LastError))?.LastError ?? string.Empty,
                SeenOrders = sessions.Sum(s => s.SeenOrders),
                MirroredBuys = sessions.Sum(s => s.MirroredBuys),
                MirroredSells = sessions.Sum(s => s.MirroredSells),
                Skipped = sessions.Sum(s => s.Skipped),
                LastPollMs = sessions.LastOrDefault()?.LastPollMs ?? 0,
                FailStreak = sessions.Select(s => s.FailStreak).DefaultIfEmpty(0).Max(),
                RecommendedDelayMs = ComputeRecommendedDelayMsUnlocked(),
                OpenSpotQty = allOpen
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
        var normalized = Normalize(settings);
        if (string.IsNullOrWhiteSpace(normalized.LeadPortfolioId) || !normalized.LeadPortfolioId.All(char.IsDigit))
        {
            throw new ArgumentException("LeadPortfolioId numerico requerido.");
        }

        lock (_gate)
        {
            if (!_sessions.TryGetValue(normalized.LeadPortfolioId, out var session))
            {
                var active = _sessions.Values.Count(s => s.Settings.Enabled);
                if (normalized.Enabled && active >= CopyMirrorStatus.MaxConcurrentMirrors)
                {
                    throw new InvalidOperationException(
                        $"Maximo {CopyMirrorStatus.MaxConcurrentMirrors} mirrors activos. Deten uno antes de añadir otro.");
                }

                session = new MirrorSession();
                _sessions[normalized.LeadPortfolioId] = session;
            }

            var wasEnabled = session.Settings.Enabled;
            var leadSame = string.Equals(session.Settings.LeadPortfolioId, normalized.LeadPortfolioId, StringComparison.Ordinal);
            session.Settings = normalized;

            if (normalized.Enabled && (!wasEnabled || !leadSame || session.Seen.IsEmpty))
            {
                session.Seen.Clear();
                session.OpenQty.Clear();
                session.OpenFutLong.Clear();
                session.OpenFutShort.Clear();
                session.WarmingUp = true;
                session.FailStreak = 0;
                session.LastError = string.Empty;
            }

            if (!normalized.Enabled)
            {
                session.WarmingUp = false;
            }

            PushHubEvent(session, "info",
                normalized.Enabled
                    ? $"Mirror {(normalized.DryRun ? "DRY-RUN" : "LIVE")} ON → {DisplayName(session)} " +
                      $"({normalized.LeadPortfolioId}), {Fmt(normalized.QuotePerTradeUsdt)} USDT/trade, " +
                      $"Venue={normalized.VenueMode}" +
                      (normalized.AllowFutures ? $" x{normalized.FuturesLeverage}" : "") +
                      $", LongOnly={normalized.LongOnly}."
                    : $"Mirror OFF → {DisplayName(session)}.");
        }

        _ = PersistSessionAsync(normalized);
    }

    public void Stop()
    {
        List<CopyMirrorSettings> toPersist;
        lock (_gate)
        {
            toPersist = [];
            foreach (var session in _sessions.Values)
            {
                if (!session.Settings.Enabled)
                {
                    continue;
                }

                session.Settings.Enabled = false;
                session.WarmingUp = false;
                toPersist.Add(CloneSettings(session.Settings));
                PushHubEvent(session, "info", $"Mirror detenido → {DisplayName(session)}.");
            }
        }

        foreach (var s in toPersist)
        {
            _ = PersistSessionAsync(s);
        }
    }

    public void Stop(string leadPortfolioId)
    {
        if (string.IsNullOrWhiteSpace(leadPortfolioId))
        {
            Stop();
            return;
        }

        CopyMirrorSettings? snapshot = null;
        lock (_gate)
        {
            if (!_sessions.TryGetValue(leadPortfolioId.Trim(), out var session))
            {
                return;
            }

            session.Settings.Enabled = false;
            session.WarmingUp = false;
            snapshot = CloneSettings(session.Settings);
            PushHubEvent(session, "info", $"Mirror detenido → {DisplayName(session)}.");
        }

        if (snapshot is not null)
        {
            _ = PersistSessionAsync(snapshot);
        }
    }

    public async Task RestorePersistedSessionsAsync(CancellationToken ct = default)
    {
        if (Interlocked.Exchange(ref _restored, 1) == 1)
        {
            return;
        }

        try
        {
            using var scope = scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var rows = await db.CopyMirrorSessions
                .AsNoTracking()
                .Where(x => x.Enabled)
                .OrderBy(x => x.UpdatedAtUtc)
                .Take(CopyMirrorStatus.MaxConcurrentMirrors)
                .ToListAsync(ct);

            foreach (var row in rows)
            {
                UpdateSettings(new CopyMirrorSettings
                {
                    Enabled = true,
                    DryRun = row.DryRun,
                    LongOnly = row.LongOnly,
                    VenueMode = ResolvePersistedVenue(row),
                    FuturesLeverage = row.FuturesLeverage,
                    LeadPortfolioId = row.LeadPortfolioId,
                    LeadNickname = row.LeadNickname,
                    QuotePerTradeUsdt = row.QuotePerTradeUsdt,
                    PollIntervalMs = row.PollIntervalMs
                });

                lock (_gate)
                {
                    if (_sessions.TryGetValue(row.LeadPortfolioId, out var session))
                    {
                        ApplyOpenPositionsJson(session, row.OpenPositionsJson);
                    }
                }
            }

            if (rows.Count > 0)
            {
                logger.LogInformation("CopyMirror restored {Count} persisted session(s) after restart.", rows.Count);
                _hubEvents.Enqueue(new CopyMirrorEvent
                {
                    Utc = DateTime.UtcNow,
                    Kind = "info",
                    Message = $"Restaurados {rows.Count} mirror(s) persistidos tras reinicio/deploy."
                });
            }
        }
        catch (Exception ex)
        {
            Interlocked.Exchange(ref _restored, 0);
            logger.LogWarning(ex, "CopyMirror restore from DB failed");
        }
    }

    private async Task PersistSessionAsync(CopyMirrorSettings settings)
    {
        try
        {
            using var scope = scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var id = settings.LeadPortfolioId.Trim();
            var row = await db.CopyMirrorSessions.FirstOrDefaultAsync(x => x.LeadPortfolioId == id);
            if (row is null)
            {
                row = new CopyMirrorPersistedSession { LeadPortfolioId = id };
                db.CopyMirrorSessions.Add(row);
            }

            row.LeadNickname = settings.LeadNickname ?? string.Empty;
            row.Enabled = settings.Enabled;
            row.DryRun = settings.DryRun;
            row.LongOnly = settings.LongOnly;
            row.AllowFutures = settings.AllowFutures;
            row.VenueMode = (int)settings.VenueMode;
            row.FuturesLeverage = settings.FuturesLeverage;
            row.QuotePerTradeUsdt = settings.QuotePerTradeUsdt;
            row.PollIntervalMs = settings.PollIntervalMs;
            lock (_gate)
            {
                if (_sessions.TryGetValue(id, out var session))
                {
                    row.OpenPositionsJson = SerializeOpenPositions(session);
                }
            }

            row.UpdatedAtUtc = DateTime.UtcNow;
            await db.SaveChangesAsync();
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "CopyMirror persist failed for {Lead}", settings.LeadPortfolioId);
        }
    }

    private void PersistOpenPositions(MirrorSession session) =>
        _ = PersistSessionAsync(CloneSettings(session.Settings));

    private static string SerializeOpenPositions(MirrorSession session)
    {
        var dto = new OpenPositionsDto
        {
            Spot = new Dictionary<string, decimal>(session.OpenQty, StringComparer.OrdinalIgnoreCase),
            FutL = new Dictionary<string, decimal>(session.OpenFutLong, StringComparer.OrdinalIgnoreCase),
            FutS = new Dictionary<string, decimal>(session.OpenFutShort, StringComparer.OrdinalIgnoreCase)
        };
        return JsonSerializer.Serialize(dto);
    }

    private static void ApplyOpenPositionsJson(MirrorSession session, string? json)
    {
        session.OpenQty.Clear();
        session.OpenFutLong.Clear();
        session.OpenFutShort.Clear();
        if (string.IsNullOrWhiteSpace(json))
        {
            return;
        }

        try
        {
            var dto = JsonSerializer.Deserialize<OpenPositionsDto>(json);
            if (dto is null)
            {
                return;
            }

            foreach (var kv in dto.Spot ?? [])
            {
                if (kv.Value > 0m)
                {
                    session.OpenQty[kv.Key] = kv.Value;
                }
            }

            foreach (var kv in dto.FutL ?? [])
            {
                if (kv.Value > 0m)
                {
                    session.OpenFutLong[kv.Key] = kv.Value;
                }
            }

            foreach (var kv in dto.FutS ?? [])
            {
                if (kv.Value > 0m)
                {
                    session.OpenFutShort[kv.Key] = kv.Value;
                }
            }
        }
        catch
        {
            // ignore corrupt payload
        }
    }

    private sealed class OpenPositionsDto
    {
        public Dictionary<string, decimal>? Spot { get; set; }
        public Dictionary<string, decimal>? FutL { get; set; }
        public Dictionary<string, decimal>? FutS { get; set; }
    }

    public Task<IReadOnlyList<CopyLeadSummary>> GetLeaderboardAsync(
        string dataType = "PNL",
        string nickname = "",
        CancellationToken ct = default) =>
        leadClient.GetLeaderboardAsync(
            dataType: string.IsNullOrWhiteSpace(dataType) ? "PNL" : dataType.Trim().ToUpperInvariant(),
            nickname: nickname ?? "",
            ct: ct);

    public async Task<CopyLeadBoardResult> GetSpotCompatibleLeaderboardAsync(CancellationToken ct = default)
    {
        lock (_gate)
        {
            if (_spotBoardCache is not null && DateTime.UtcNow - _spotBoardCacheUtc < SpotBoardCacheTtl)
            {
                return _spotBoardCache;
            }
        }

        var scanned = new List<CopyLeadSummary>();
        for (var page = 1; page <= 3; page++)
        {
            var pageLeads = await leadClient.GetLeaderboardAsync(pageNumber: page, pageSize: 20, ct: ct);
            if (pageLeads.Count == 0)
            {
                break;
            }

            scanned.AddRange(pageLeads);
            if (scanned.Count >= 50)
            {
                break;
            }
        }

        var compatible = new List<CopyLeadSummary>();
        foreach (var lead in scanned)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                await Task.Delay(350, ct);
                var (orders, _) = await leadClient.GetOrderHistoryAsync(lead.LeadPortfolioId, pageSize: 20, ct);
                if (orders.Count == 0)
                {
                    continue;
                }

                var symbols = orders
                    .Select(o => o.Symbol)
                    .Where(s => !string.IsNullOrWhiteSpace(s))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();

                var spot = 0;
                var futOnly = 0;
                var spotNames = new List<string>();
                foreach (var sym in symbols)
                {
                    if (await leadClient.IsSpotSymbolAsync(sym, ct))
                    {
                        spot++;
                        if (spotNames.Count < 5)
                        {
                            spotNames.Add(sym);
                        }
                    }
                    else
                    {
                        futOnly++;
                    }
                }

                if (spot <= 0 || futOnly > 0)
                {
                    continue;
                }

                compatible.Add(new CopyLeadSummary
                {
                    LeadPortfolioId = lead.LeadPortfolioId,
                    Nickname = lead.Nickname,
                    Roi = lead.Roi,
                    Pnl = lead.Pnl,
                    Aum = lead.Aum,
                    Mdd = lead.Mdd,
                    WinRate = lead.WinRate,
                    CurrentCopyCount = lead.CurrentCopyCount,
                    MaxCopyCount = lead.MaxCopyCount,
                    SpotCompatible = true,
                    SpotSymbolCount = spot,
                    FuturesOnlySymbolCount = futOnly,
                    SpotSymbolsSample = string.Join(", ", spotNames)
                });
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                logger.LogDebug(ex, "Spot filter skip for lead {Lead}", lead.LeadPortfolioId);
            }
        }

        var result = new CopyLeadBoardResult
        {
            Leads = compatible,
            Scanned = scanned.Count,
            SpotCompatibleCount = compatible.Count,
            Note = compatible.Count == 0
                ? "Ningun lead del ranking Copy publico paso el filtro Spot estricto (muchos operan solo Futures)."
                : $"Mostrando {compatible.Count} leads Spot-compatible de {scanned.Count} revisados (ordenes recientes 100% Spot).",
            GeneratedAtUtc = DateTime.UtcNow
        };

        lock (_gate)
        {
            _spotBoardCache = result;
            _spotBoardCacheUtc = DateTime.UtcNow;
        }

        return result;
    }

    public async Task PollOnceAsync(IServiceProvider scopedServices, CancellationToken ct = default)
    {
        List<MirrorSession> active;
        lock (_gate)
        {
            active = _sessions.Values.Where(s => s.Settings.Enabled && !string.IsNullOrWhiteSpace(s.Settings.LeadPortfolioId)).ToList();
        }

        if (active.Count == 0)
        {
            return;
        }

        foreach (var session in active)
        {
            ct.ThrowIfCancellationRequested();
            await PollSessionAsync(session, scopedServices, ct);
            // Pacing entre leads para no tumbar order-history.
            if (active.Count > 1)
            {
                await Task.Delay(400, ct);
            }
        }
    }

    private async Task PollSessionAsync(MirrorSession session, IServiceProvider scopedServices, CancellationToken ct)
    {
        CopyMirrorSettings settings;
        lock (_gate)
        {
            settings = CloneSettings(session.Settings);
        }

        try
        {
            var (orders, pollMs) = await leadClient.GetOrderHistoryAsync(settings.LeadPortfolioId, ct: ct);
            lock (_gate)
            {
                session.LastPollUtc = DateTime.UtcNow;
                session.LastSuccessUtc = DateTime.UtcNow;
                session.LastPollMs = pollMs;
                session.LastError = string.Empty;
                session.FailStreak = 0;
            }

            var chronological = orders.OrderBy(o => o.OrderTimeMs).ToList();
            bool warmingUp;
            lock (_gate)
            {
                warmingUp = session.WarmingUp;
            }

            if (warmingUp)
            {
                foreach (var order in chronological)
                {
                    session.Seen.TryAdd(order.Fingerprint, 0);
                }

                lock (_gate)
                {
                    session.WarmingUp = false;
                }

                PushHubEvent(session, "info",
                    $"Warm-up OK [{DisplayName(session)}]: {session.Seen.Count} ordenes historicas marcadas.");
                return;
            }

            foreach (var order in chronological)
            {
                if (!session.Seen.TryAdd(order.Fingerprint, 0))
                {
                    continue;
                }

                await HandleNewOrderAsync(session, order, settings, scopedServices, ct);
            }
        }
        catch (Exception ex)
        {
            int streak;
            lock (_gate)
            {
                session.LastPollUtc = DateTime.UtcNow;
                session.LastError = ex.Message;
                session.FailStreak++;
                streak = session.FailStreak;
            }

            var busy = IsBinanceBusy(ex.Message);
            logger.LogWarning(ex, "Copy mirror poll failed lead={Lead} streak={Streak} busy={Busy}",
                settings.LeadPortfolioId, streak, busy);
            if (!busy && (streak <= 2 || streak % 10 == 0))
            {
                PushHubEvent(session, "error", $"Poll fail [{DisplayName(session)}]: {ex.Message}");
            }
            else if (busy && (streak == 1 || streak % 5 == 0))
            {
                PushHubEvent(session, "error",
                    $"Binance busy/rate-limit [{DisplayName(session)}] intento {streak}. Reintento mas lento.");
            }
        }
    }

    private async Task HandleNewOrderAsync(
        MirrorSession session,
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
        PushHubEvent(session, "detected",
            $"[{DisplayName(session)}] LEAD {leadAction} {order.PositionSide} {order.Symbol} qty={Fmt(order.ExecutedQty)} @ {Fmt(order.AvgPrice)}",
            order.Symbol, leadAction, "lead", order.ExecutedQty, order.AvgPrice, null,
            order.OrderTimeMs, detectDelayMs);

        // Ordenes muy viejas (no estaban en warm-up / fingerprint cambió): no abrir ni cerrar.
        const int maxMirrorLagMs = 15 * 60 * 1000;
        if (detectDelayMs is > maxMirrorLagMs)
        {
            PushHubEvent(session, "info",
                $"[{DisplayName(session)}] IGNORADA orden antigua {order.Symbol} " +
                $"(lag {(detectDelayMs.Value / 1000):0}s > 15min).",
                order.Symbol, leadAction, "lead", order.ExecutedQty, order.AvgPrice, null,
                order.OrderTimeMs, detectDelayMs);
            return;
        }

        if (!TryMapLeadIntent(order, settings, out var intent, out var skipReason))
        {
            Interlocked.Increment(ref session.Skipped);
            PushHubEvent(session, "skipped",
                $"[{DisplayName(session)}] SKIP {skipReason}: {order.Symbol}",
                order.Symbol, "SKIP", "lead", order.ExecutedQty, order.AvgPrice, null,
                order.OrderTimeMs, detectDelayMs);
            return;
        }

        var spotExec = scopedServices.GetRequiredService<IBinanceTradeExecutionService>();
        var futExec = scopedServices.GetRequiredService<IBinanceFuturesExecutionService>();
        var venue = await ResolveVenueAsync(session, order.Symbol, intent, settings, spotExec, futExec, ct);
        if (venue is null)
        {
            Interlocked.Increment(ref session.Skipped);
            PushHubEvent(session, "skipped",
                $"[{DisplayName(session)}] SKIP {order.Symbol}: sin venue Spot/Futures disponible " +
                $"(Venue={settings.VenueMode}).",
                order.Symbol, "SKIP", settings.DryRun ? "dryrun" : "live", order.ExecutedQty, order.AvgPrice, null,
                order.OrderTimeMs, detectDelayMs);
            return;
        }

        var venueTag = venue == MirrorVenue.Spot ? "Spot" : "Futures";
        var actionLabel = intent.IsOpen ? "BUY" : "SELL";

        if (settings.DryRun)
        {
            if (intent.IsOpen)
            {
                Interlocked.Increment(ref session.MirroredBuys);
            }
            else
            {
                Interlocked.Increment(ref session.MirroredSells);
            }

            PushHubEvent(session, "dryrun",
                $"[{DisplayName(session)}] {(intent.IsOpen ? "ABRIR" : "CERRAR")} {intent.PositionSide} ({venueTag} dry-run) " +
                $"{order.Symbol} ~{Fmt(settings.QuotePerTradeUsdt)} USDT",
                order.Symbol, actionLabel, "dryrun", null, order.AvgPrice,
                intent.IsOpen ? settings.QuotePerTradeUsdt : null,
                order.OrderTimeMs, detectDelayMs);
            return;
        }

        var execStart = DateTime.UtcNow;
        if (venue == MirrorVenue.Spot)
        {
            await ExecuteSpotAsync(session, order, settings, intent, spotExec, detectDelayMs, execStart, ct);
            return;
        }

        await ExecuteFuturesAsync(session, order, settings, intent, futExec, detectDelayMs, execStart, ct);
    }

    private enum MirrorVenue
    {
        Spot,
        Futures
    }

    /// <summary>PositionSide LONG/SHORT; IsOpen true=abrir, false=cerrar.</summary>
    private readonly record struct MirrorIntent(string PositionSide, bool IsOpen);

    private static bool TryMapLeadIntent(
        CopyLeadOrder order,
        CopyMirrorSettings settings,
        out MirrorIntent intent,
        out string skipReason)
    {
        intent = default;
        skipReason = string.Empty;
        var pos = (order.PositionSide ?? string.Empty).Trim().ToUpperInvariant();
        var side = (order.Side ?? string.Empty).Trim().ToUpperInvariant();
        if (side is not ("BUY" or "SELL"))
        {
            skipReason = $"lado no mapeable ({order.Side}/{order.PositionSide})";
            return false;
        }

        // Hedge Binance: BUY+LONG open, SELL+LONG close, SELL+SHORT open, BUY+SHORT close.
        // BOTH / vacio: one-way → BUY open long, SELL close long.
        if (pos is "SHORT")
        {
            if (settings.LongOnly || settings.VenueMode == CopyMirrorVenueMode.SpotOnly)
            {
                skipReason = "SHORT";
                return false;
            }

            // SELL SHORT = abrir short; BUY SHORT = cerrar short
            intent = new MirrorIntent("SHORT", IsOpen: side == "SELL");
            return true;
        }

        if (pos is "LONG" or "BOTH" or "")
        {
            intent = new MirrorIntent("LONG", IsOpen: side == "BUY");
            return true;
        }

        skipReason = $"lado no mapeable ({order.Side}/{order.PositionSide})";
        return false;
    }

    private static async Task<MirrorVenue?> ResolveVenueAsync(
        MirrorSession session,
        string symbol,
        MirrorIntent intent,
        CopyMirrorSettings settings,
        IBinanceTradeExecutionService spotExec,
        IBinanceFuturesExecutionService futExec,
        CancellationToken ct)
    {
        var mode = settings.VenueMode;

        // Cierre: usar el venue donde ya hay posicion mirror.
        if (!intent.IsOpen)
        {
            if (intent.PositionSide == "LONG")
            {
                if (session.OpenFutLong.TryGetValue(symbol, out var fl) && fl > 0m)
                {
                    return MirrorVenue.Futures;
                }

                if (session.OpenQty.TryGetValue(symbol, out var sq) && sq > 0m)
                {
                    return MirrorVenue.Spot;
                }
            }
            else if (intent.PositionSide == "SHORT" &&
                     session.OpenFutShort.TryGetValue(symbol, out var fs) && fs > 0m)
            {
                return MirrorVenue.Futures;
            }
        }

        if (mode == CopyMirrorVenueMode.SpotOnly)
        {
            if (intent.PositionSide == "SHORT")
            {
                return null;
            }

            return await spotExec.IsSpotSymbolTradableAsync(symbol, ct) ? MirrorVenue.Spot : null;
        }

        if (mode == CopyMirrorVenueMode.FuturesOnly)
        {
            return await futExec.IsFuturesSymbolTradableAsync(symbol, ct) ? MirrorVenue.Futures : null;
        }

        // Auto / match lead: Futures primero (LONG y SHORT); Spot solo fallback LONG.
        if (await futExec.IsFuturesSymbolTradableAsync(symbol, ct))
        {
            return MirrorVenue.Futures;
        }

        if (intent.PositionSide == "LONG" && await spotExec.IsSpotSymbolTradableAsync(symbol, ct))
        {
            return MirrorVenue.Spot;
        }

        return null;
    }

    private async Task ExecuteSpotAsync(
        MirrorSession session,
        CopyLeadOrder order,
        CopyMirrorSettings settings,
        MirrorIntent intent,
        IBinanceTradeExecutionService execution,
        int? detectDelayMs,
        DateTime execStart,
        CancellationToken ct)
    {
        if (intent.IsOpen)
        {
            var fill = await execution.MarketBuyAsync(order.Symbol, settings.QuotePerTradeUsdt, botId: null, ct);
            var execDelay = (int)(DateTime.UtcNow - execStart).TotalMilliseconds;
            if (fill is null)
            {
                Interlocked.Increment(ref session.Skipped);
                PushHubEvent(session, "error",
                    $"[{DisplayName(session)}] COMPRA Spot FALLIDA {order.Symbol}: {execution.GetLastExecutionError()}",
                    order.Symbol, "BUY", "live", null, null, settings.QuotePerTradeUsdt,
                    order.OrderTimeMs, detectDelayMs, execDelay);
                return;
            }

            session.OpenQty.AddOrUpdate(order.Symbol, fill.ExecutedQuantity, (_, cur) => cur + fill.ExecutedQuantity);
            PersistOpenPositions(session);
            Interlocked.Increment(ref session.MirroredBuys);
            PushHubEvent(session, "executed",
                $"[{DisplayName(session)}] COMPRA Spot {order.Symbol} qty={Fmt(fill.ExecutedQuantity)} @ {Fmt(fill.AveragePrice)}",
                order.Symbol, "BUY", "live", fill.ExecutedQuantity, fill.AveragePrice, settings.QuotePerTradeUsdt,
                order.OrderTimeMs, detectDelayMs, execDelay);
            return;
        }

        if (!session.OpenQty.TryGetValue(order.Symbol, out var qty) || qty <= 0m)
        {
            Interlocked.Increment(ref session.Skipped);
            PushHubEvent(session, "skipped",
                $"[{DisplayName(session)}] SKIP VENTA Spot: sin posicion mirror en {order.Symbol}",
                order.Symbol, "SKIP", "live", null, null, null, order.OrderTimeMs, detectDelayMs);
            return;
        }

        var sellFill = await execution.MarketSellAsync(order.Symbol, qty, botId: null, ct);
        var sellExecDelay = (int)(DateTime.UtcNow - execStart).TotalMilliseconds;
        if (sellFill is null)
        {
            Interlocked.Increment(ref session.Skipped);
            PushHubEvent(session, "error",
                $"[{DisplayName(session)}] VENTA Spot FALLIDA {order.Symbol}: {execution.GetLastExecutionError()}",
                order.Symbol, "SELL", "live", qty, null, null, order.OrderTimeMs, detectDelayMs, sellExecDelay);
            return;
        }

        session.OpenQty.TryRemove(order.Symbol, out _);
        PersistOpenPositions(session);
        Interlocked.Increment(ref session.MirroredSells);
        PushHubEvent(session, "executed",
            $"[{DisplayName(session)}] VENTA Spot {order.Symbol} qty={Fmt(sellFill.ExecutedQuantity)} @ {Fmt(sellFill.AveragePrice)}",
            order.Symbol, "SELL", "live", sellFill.ExecutedQuantity, sellFill.AveragePrice, null,
            order.OrderTimeMs, detectDelayMs, sellExecDelay);
    }

    private async Task ExecuteFuturesAsync(
        MirrorSession session,
        CopyLeadOrder order,
        CopyMirrorSettings settings,
        MirrorIntent intent,
        IBinanceFuturesExecutionService execution,
        int? detectDelayMs,
        DateTime execStart,
        CancellationToken ct)
    {
        var book = intent.PositionSide == "SHORT" ? session.OpenFutShort : session.OpenFutLong;
        decimal closeQty = 0m;
        if (!intent.IsOpen)
        {
            if (!book.TryGetValue(order.Symbol, out closeQty) || closeQty <= 0m)
            {
                // Sin OPEN previo (fallo notional/TradFi/lag): no cuenta como SKIP operativo.
                var blocked = session.BlockedFuturesSymbols.TryGetValue(order.Symbol, out var why);
                PushHubEvent(session, "info",
                    blocked
                        ? $"[{DisplayName(session)}] Cierre {order.Symbol} omitido (bloqueado: {why})."
                        : $"[{DisplayName(session)}] Cierre {intent.PositionSide} {order.Symbol} omitido: sin posicion mirror.",
                    order.Symbol, "SELL", "live", null, null, null, order.OrderTimeMs, detectDelayMs);
                return;
            }
        }
        else if (session.BlockedFuturesSymbols.TryGetValue(order.Symbol, out var blockReason))
        {
            PushHubEvent(session, "info",
                $"[{DisplayName(session)}] OPEN {order.Symbol} omitido (bloqueado: {blockReason}).",
                order.Symbol, "BUY", "live", null, null, settings.QuotePerTradeUsdt,
                order.OrderTimeMs, detectDelayMs);
            return;
        }

        var fill = await execution.MarketPositionAsync(
            order.Symbol,
            intent.PositionSide,
            open: intent.IsOpen,
            quoteUsdtOrCloseQty: intent.IsOpen ? settings.QuotePerTradeUsdt : closeQty,
            quoteIsNotional: intent.IsOpen,
            leverage: settings.FuturesLeverage,
            ct);
        var execDelay = (int)(DateTime.UtcNow - execStart).TotalMilliseconds;
        var action = intent.IsOpen ? "BUY" : "SELL";
        if (fill is null)
        {
            var err = execution.GetLastExecutionError();
            if (intent.IsOpen && IsTradFiAgreementError(err))
            {
                session.BlockedFuturesSymbols[order.Symbol] = "TradFi agreement (-4411)";
                PushHubEvent(session, "info",
                    $"[{DisplayName(session)}] {order.Symbol} bloqueado: firma TradFi-Perps en Demo Binance. " +
                    $"No se reintentará. ({err})",
                    order.Symbol, action, "live", null, null, settings.QuotePerTradeUsdt,
                    order.OrderTimeMs, detectDelayMs, execDelay);
                return;
            }

            Interlocked.Increment(ref session.Skipped);
            PushHubEvent(session, "error",
                $"[{DisplayName(session)}] Futures {(intent.IsOpen ? "OPEN" : "CLOSE")} {intent.PositionSide} " +
                $"FALLIDA {order.Symbol}: {err}",
                order.Symbol, action, "live",
                intent.IsOpen ? null : closeQty, null,
                intent.IsOpen ? settings.QuotePerTradeUsdt : null,
                order.OrderTimeMs, detectDelayMs, execDelay);
            return;
        }

        if (intent.IsOpen)
        {
            book.AddOrUpdate(order.Symbol, fill.ExecutedQuantity, (_, cur) => cur + fill.ExecutedQuantity);
            PersistOpenPositions(session);
            Interlocked.Increment(ref session.MirroredBuys);
            PushHubEvent(session, "executed",
                $"[{DisplayName(session)}] OPEN Futures {intent.PositionSide} {order.Symbol} " +
                $"qty={Fmt(fill.ExecutedQuantity)} @ {Fmt(fill.AveragePrice)} x{settings.FuturesLeverage}",
                order.Symbol, "BUY", "live", fill.ExecutedQuantity, fill.AveragePrice, settings.QuotePerTradeUsdt,
                order.OrderTimeMs, detectDelayMs, execDelay);
            return;
        }

        book.TryRemove(order.Symbol, out _);
        PersistOpenPositions(session);
        Interlocked.Increment(ref session.MirroredSells);
        PushHubEvent(session, "executed",
            $"[{DisplayName(session)}] CLOSE Futures {intent.PositionSide} {order.Symbol} " +
            $"qty={Fmt(fill.ExecutedQuantity)} @ {Fmt(fill.AveragePrice)}",
            order.Symbol, "SELL", "live", fill.ExecutedQuantity, fill.AveragePrice, null,
            order.OrderTimeMs, detectDelayMs, execDelay);
    }

    private static bool IsTradFiAgreementError(string? err) =>
        !string.IsNullOrWhiteSpace(err) &&
        (err.Contains("-4411", StringComparison.Ordinal) ||
         err.Contains("TradFi", StringComparison.OrdinalIgnoreCase));

    private int ComputeRecommendedDelayMsUnlocked()
    {
        var active = _sessions.Values.Where(s => s.Settings.Enabled).ToList();
        if (active.Count == 0)
        {
            return 3000;
        }

        var baseMs = active.Max(s => Math.Clamp(s.Settings.PollIntervalMs <= 0 ? 3500 : s.Settings.PollIntervalMs, 2500, 30_000));
        // Mas leads => mas delay base para no saturar Binance.
        baseMs += Math.Max(0, (active.Count - 1) * 800);
        var maxFail = active.Max(s => s.FailStreak);
        if (maxFail <= 0)
        {
            return Math.Min(45_000, baseMs);
        }

        var extra = Math.Min(40_000, (int)(1500 * Math.Pow(1.65, Math.Min(maxFail, 10))));
        return Math.Min(45_000, baseMs + extra);
    }

    private CopyMirrorSessionStatus ToSessionStatus(MirrorSession s)
    {
        var open = new Dictionary<string, decimal>(s.OpenQty, StringComparer.OrdinalIgnoreCase);
        foreach (var kv in s.OpenFutLong)
        {
            open[$"FUT-L:{kv.Key}"] = kv.Value;
        }

        foreach (var kv in s.OpenFutShort)
        {
            open[$"FUT-S:{kv.Key}"] = kv.Value;
        }

        return new CopyMirrorSessionStatus
        {
            Settings = CloneSettings(s.Settings),
            WarmingUp = s.WarmingUp,
            LastPollUtc = s.LastPollUtc,
            LastSuccessUtc = s.LastSuccessUtc,
            LastError = s.LastError,
            SeenOrders = s.Seen.Count,
            MirroredBuys = s.MirroredBuys,
            MirroredSells = s.MirroredSells,
            Skipped = s.Skipped,
            LastPollMs = s.LastPollMs,
            FailStreak = s.FailStreak,
            RecommendedDelayMs = ComputeSessionDelay(s),
            OpenSpotQty = open
        };
    }

    private static int ComputeSessionDelay(MirrorSession s)
    {
        var baseMs = Math.Clamp(s.Settings.PollIntervalMs <= 0 ? 3500 : s.Settings.PollIntervalMs, 2500, 30_000);
        if (!s.Settings.Enabled || s.FailStreak <= 0)
        {
            return baseMs;
        }

        var extra = Math.Min(40_000, (int)(1500 * Math.Pow(1.65, Math.Min(s.FailStreak, 10))));
        return Math.Min(45_000, baseMs + extra);
    }

    private void PushHubEvent(
        MirrorSession session,
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
        _hubEvents.Enqueue(new CopyMirrorEvent
        {
            Utc = DateTime.UtcNow,
            Kind = kind,
            Message = message,
            LeadPortfolioId = session.Settings.LeadPortfolioId,
            LeadNickname = session.Settings.LeadNickname,
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

        while (_hubEvents.Count > 250 && _hubEvents.TryDequeue(out _))
        {
        }
    }

    private static string DisplayName(MirrorSession s) =>
        string.IsNullOrWhiteSpace(s.Settings.LeadNickname)
            ? ShortId(s.Settings.LeadPortfolioId)
            : s.Settings.LeadNickname;

    private static string ShortId(string id) =>
        string.IsNullOrEmpty(id) ? "" : (id.Length <= 10 ? id : id[..6] + "…" + id[^4..]);

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

    private static CopyMirrorVenueMode ResolvePersistedVenue(CopyMirrorPersistedSession row)
    {
        if (Enum.IsDefined(typeof(CopyMirrorVenueMode), row.VenueMode) &&
            (row.VenueMode != 0 || !row.AllowFutures))
        {
            return (CopyMirrorVenueMode)row.VenueMode;
        }

        // Legacy: solo AllowFutures sin VenueMode real → Auto.
        return row.AllowFutures ? CopyMirrorVenueMode.Auto : CopyMirrorVenueMode.SpotOnly;
    }

    private static CopyMirrorSettings Normalize(CopyMirrorSettings s) => new()
    {
        Enabled = s.Enabled,
        DryRun = s.DryRun,
        LongOnly = s.LongOnly,
        VenueMode = Enum.IsDefined(typeof(CopyMirrorVenueMode), s.VenueMode)
            ? s.VenueMode
            : CopyMirrorVenueMode.Auto,
        FuturesLeverage = Math.Clamp(s.FuturesLeverage <= 0 ? 1 : s.FuturesLeverage, 1, 5),
        LeadPortfolioId = (s.LeadPortfolioId ?? string.Empty).Trim(),
        LeadNickname = (s.LeadNickname ?? string.Empty).Trim(),
        // Min 20: suelo Binance Futures; el ejecutor puede subir mas (p.ej. BTC min ~100).
        QuotePerTradeUsdt = Math.Clamp(s.QuotePerTradeUsdt <= 0 ? 30m : s.QuotePerTradeUsdt, 20m, 5000m),
        PollIntervalMs = Math.Clamp(s.PollIntervalMs <= 0 ? 3500 : s.PollIntervalMs, 2500, 30_000)
    };

    private static CopyMirrorSettings CloneSettings(CopyMirrorSettings s) => new()
    {
        Enabled = s.Enabled,
        DryRun = s.DryRun,
        LongOnly = s.LongOnly,
        VenueMode = s.VenueMode,
        FuturesLeverage = s.FuturesLeverage,
        LeadPortfolioId = s.LeadPortfolioId,
        LeadNickname = s.LeadNickname,
        QuotePerTradeUsdt = s.QuotePerTradeUsdt,
        PollIntervalMs = s.PollIntervalMs
    };

    private static string Fmt(decimal v) =>
        v.ToString("0.####", System.Globalization.CultureInfo.InvariantCulture);

    private static DateTime? MaxNullable(IEnumerable<DateTime?> values)
    {
        DateTime? max = null;
        foreach (var v in values)
        {
            if (v is null)
            {
                continue;
            }

            if (max is null || v > max)
            {
                max = v;
            }
        }

        return max;
    }
}
