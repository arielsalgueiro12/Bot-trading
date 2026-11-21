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

    public string Fingerprint =>
        $"{Symbol}|{Side}|{PositionSide}|{OrderTimeMs}|{ExecutedQty.ToString(System.Globalization.CultureInfo.InvariantCulture)}|{AvgPrice.ToString(System.Globalization.CultureInfo.InvariantCulture)}";
}

public sealed class CopyMirrorSettings
{
    public bool Enabled { get; set; }
    public bool DryRun { get; set; } = true;
    public bool LongOnly { get; set; } = true;
    public string LeadPortfolioId { get; set; } = string.Empty;
    public string LeadNickname { get; set; } = string.Empty;
    public decimal QuotePerTradeUsdt { get; set; } = 15m;
    public int PollIntervalMs { get; set; } = 2000;
}

public sealed class CopyMirrorEvent
{
    public DateTime Utc { get; init; } = DateTime.UtcNow;
    public string Kind { get; init; } = "info";
    public string Message { get; init; } = string.Empty;
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

public sealed class CopyMirrorStatus
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
    public IReadOnlyList<CopyMirrorEvent> RecentEvents { get; init; } = [];
    /// <summary>Solo compras/ventas/skips del mirror (mas legible que el log completo).</summary>
    public IReadOnlyList<CopyMirrorEvent> RecentMovements { get; init; } = [];
    public IReadOnlyDictionary<string, decimal> OpenSpotQty { get; init; } =
        new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase);
}
