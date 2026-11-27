namespace TradingBots.App.Models;

/// <summary>Traza persistente de copy-mirror (sobrevive redeploys).</summary>
public sealed class CopyMirrorPersistedEvent
{
    public long Id { get; set; }
    public DateTime Utc { get; set; } = DateTime.UtcNow;
    public string Kind { get; set; } = "info";
    public string Message { get; set; } = string.Empty;
    public string LeadPortfolioId { get; set; } = string.Empty;
    public string LeadNickname { get; set; } = string.Empty;
    public string? Symbol { get; set; }
    public string? Action { get; set; }
    public string? Mode { get; set; }
    public decimal? Qty { get; set; }
    public decimal? Price { get; set; }
    public decimal? QuoteUsdt { get; set; }
    public long? LeadOrderTimeMs { get; set; }
    public int? DetectDelayMs { get; set; }
    public int? ExecDelayMs { get; set; }
    public decimal? RealizedPnlUsdt { get; set; }
    public decimal? FeesUsdt { get; set; }
    public bool FeeIsEstimated { get; set; }
    public string? PnlResult { get; set; }
}
