namespace TradingBots.App.Models;

/// <summary>Sesion de copy-mirror persistida para sobrevivir redeploys.</summary>
public sealed class CopyMirrorPersistedSession
{
    public string LeadPortfolioId { get; set; } = string.Empty;
    public string LeadNickname { get; set; } = string.Empty;
    public bool Enabled { get; set; }
    public bool DryRun { get; set; } = true;
    public bool LongOnly { get; set; }
    public bool AllowFutures { get; set; }
    /// <summary>0=SpotOnly, 1=FuturesOnly, 2=Auto. Si falta columna legacy, se deriva de AllowFutures.</summary>
    public int VenueMode { get; set; } = 2;
    public int FuturesLeverage { get; set; } = 1;
    public decimal QuotePerTradeUsdt { get; set; } = 15m;
    public int MaxOpenPositions { get; set; } = 3;
    public int PollIntervalMs { get; set; } = 3500;
    /// <summary>JSON posiciones abiertas: {"spot":{...},"futL":{...},"futS":{...}}.</summary>
    public string OpenPositionsJson { get; set; } = string.Empty;
    public int MirroredBuys { get; set; }
    public int MirroredSells { get; set; }
    public DateTime UpdatedAtUtc { get; set; } = DateTime.UtcNow;
}
