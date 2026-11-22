namespace TradingBots.App.Models;

/// <summary>Sesion de copy-mirror persistida para sobrevivir redeploys.</summary>
public sealed class CopyMirrorPersistedSession
{
    public string LeadPortfolioId { get; set; } = string.Empty;
    public string LeadNickname { get; set; } = string.Empty;
    public bool Enabled { get; set; }
    public bool DryRun { get; set; } = true;
    public bool LongOnly { get; set; } = true;
    public decimal QuotePerTradeUsdt { get; set; } = 15m;
    public int PollIntervalMs { get; set; } = 3500;
    public DateTime UpdatedAtUtc { get; set; } = DateTime.UtcNow;
}
