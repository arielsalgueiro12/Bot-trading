namespace TradingBots.App.Services.CopyMirror;

public sealed class CopyLeadSummary
{
    public string LeadPortfolioId { get; init; } = string.Empty;
    public string Nickname { get; init; } = string.Empty;
    public decimal Roi { get; init; }
    public decimal Pnl { get; init; }
    public decimal Aum { get; init; }
    public decimal Mdd { get; init; }
    public decimal WinRate { get; init; }
    public int CurrentCopyCount { get; init; }
    public int MaxCopyCount { get; init; }
    /// <summary>True si las ordenes recientes del lead cotizan en Spot mainnet.</summary>
    public bool SpotCompatible { get; init; }
    public int SpotSymbolCount { get; init; }
    public int FuturesOnlySymbolCount { get; init; }
    public string SpotSymbolsSample { get; init; } = string.Empty;
}

public sealed class CopyLeadBoardResult
{
    public IReadOnlyList<CopyLeadSummary> Leads { get; init; } = [];
    public int Scanned { get; init; }
    public int SpotCompatibleCount { get; init; }
    public string Note { get; init; } = string.Empty;
    public DateTime GeneratedAtUtc { get; init; } = DateTime.UtcNow;
}

public sealed class CopyLeadOrder
{
    public string Symbol { get; init; } = string.Empty;
    public string Side { get; init; } = string.Empty;
    public string PositionSide { get; init; } = string.Empty;
    public decimal ExecutedQty { get; init; }
    public decimal AvgPrice { get; init; }
    public long OrderTimeMs { get; init; }
    public long OrderUpdateTimeMs { get; init; }

    /// <summary>Clave estable (sin qty/precio: si cambian, no se re-ejecuta la misma orden).</summary>
    public string Fingerprint =>
        $"{Symbol}|{Side}|{PositionSide}|{OrderTimeMs}|{OrderUpdateTimeMs}";
}

/// <summary>Wallet/venue de ejecucion del mirror.</summary>
public enum CopyMirrorVenueMode
{
    /// <summary>Solo wallet Spot. Ignora shorts.</summary>
    SpotOnly = 0,
    /// <summary>Solo wallet Futures USD-M (longs y shorts 1:1 con el lead).</summary>
    FuturesOnly = 1,
    /// <summary>
    /// Match lead: copia LONG/SHORT igual que el trader.
    /// Prefiere Futures; LONG cae a Spot solo si Futures no cotiza el par.
    /// </summary>
    Auto = 2
}

public sealed class CopyMirrorSettings
{
    public bool Enabled { get; set; }
    public bool DryRun { get; set; } = true;
    /// <summary>Si true ignora SHORT. En modo Auto/Futures se recomienda false.</summary>
    public bool LongOnly { get; set; }
    /// <summary>Venue: SpotOnly / FuturesOnly / Auto (match lead).</summary>
    public CopyMirrorVenueMode VenueMode { get; set; } = CopyMirrorVenueMode.Auto;
    /// <summary>Compat: true si VenueMode permite Futures (Auto o FuturesOnly).</summary>
    public bool AllowFutures
    {
        get => VenueMode is CopyMirrorVenueMode.Auto or CopyMirrorVenueMode.FuturesOnly;
        set
        {
            if (value && VenueMode == CopyMirrorVenueMode.SpotOnly)
            {
                VenueMode = CopyMirrorVenueMode.Auto;
            }
            else if (!value)
            {
                VenueMode = CopyMirrorVenueMode.SpotOnly;
            }
        }
    }
    /// <summary>Apalancamiento USD-M acotado 1–5 (default 1 = notional ≈ margen).</summary>
    public int FuturesLeverage { get; set; } = 1;
    public string LeadPortfolioId { get; set; } = string.Empty;
    public string LeadNickname { get; set; } = string.Empty;
    public decimal QuotePerTradeUsdt { get; set; } = 15m;
    /// <summary>
    /// Maximo de posiciones abiertas a la vez (Spot+Fut L+Fut S).
    /// Si se alcanza, se omiten nuevos OPEN; los CLOSE siguen.
    /// </summary>
    public int MaxOpenPositions { get; set; } = 3;
    /// <summary>Minimo recomendado 3s: el order-history publico de Binance rate-limita fuerte.</summary>
    public int PollIntervalMs { get; set; } = 3500;
}

public sealed class CopyMirrorEvent
{
    public DateTime Utc { get; init; } = DateTime.UtcNow;
    public string Kind { get; init; } = "info";
    public string Message { get; init; } = string.Empty;
    public string? LeadPortfolioId { get; init; }
    public string? LeadNickname { get; init; }
    public string? Symbol { get; init; }
    /// <summary>BUY / SELL / SKIP / INFO</summary>
    public string? Action { get; init; }
    /// <summary>dryrun / live / lead / none</summary>
    public string? Mode { get; init; }
    public decimal? Qty { get; init; }
    public decimal? Price { get; init; }
    public decimal? QuoteUsdt { get; init; }
    public long? LeadOrderTimeMs { get; init; }
    public int? DetectDelayMs { get; init; }
    public int? ExecDelayMs { get; init; }

    public bool IsMovement =>
        Kind is "dryrun" or "executed" or "detected" or "skipped" or "error";
}

/// <summary>Estado de un mirror individual (un lead).</summary>
public sealed class CopyMirrorSessionStatus
{
    public CopyMirrorSettings Settings { get; init; } = new();
    public bool WarmingUp { get; init; }
    public DateTime? LastPollUtc { get; init; }
    public DateTime? LastSuccessUtc { get; init; }
    public string LastError { get; init; } = string.Empty;
    public int SeenOrders { get; init; }
    public int MirroredBuys { get; init; }
    public int MirroredSells { get; init; }
    public int Skipped { get; init; }
    public int LastPollMs { get; init; }
    public int FailStreak { get; init; }
    public int RecommendedDelayMs { get; init; }
    public IReadOnlyDictionary<string, decimal> OpenSpotQty { get; init; } =
        new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase);
}

/// <summary>Hub multi-mirror: varios leads a la vez.</summary>
public sealed class CopyMirrorStatus
{
    public const int MaxConcurrentMirrors = 5;

    public int ActiveCount { get; init; }
    public int MaxConcurrent { get; init; } = MaxConcurrentMirrors;
    public IReadOnlyList<CopyMirrorSessionStatus> Sessions { get; init; } = [];
    public IReadOnlyList<CopyMirrorEvent> RecentEvents { get; init; } = [];
    public IReadOnlyList<CopyMirrorEvent> RecentMovements { get; init; } = [];

    /// <summary>Compat: primera sesion activa o vacia (UI antigua / API).</summary>
    public CopyMirrorSettings Settings { get; init; } = new();
    public bool WarmingUp { get; init; }
    public DateTime? LastPollUtc { get; init; }
    public DateTime? LastSuccessUtc { get; init; }
    public string LastError { get; init; } = string.Empty;
    public int SeenOrders { get; init; }
    public int MirroredBuys { get; init; }
    public int MirroredSells { get; init; }
    public int Skipped { get; init; }
    public int LastPollMs { get; init; }
    public int FailStreak { get; init; }
    public int RecommendedDelayMs { get; init; }
    public IReadOnlyDictionary<string, decimal> OpenSpotQty { get; init; } =
        new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase);
}
