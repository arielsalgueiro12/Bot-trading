using TradingBots.App.Models;

namespace TradingBots.App.Services.Strategies;

/// <summary>
/// Pullback 15m / tendencia 1h. Entradas selectivas en tendencia;
/// la gestion de salida (parcial 1.5R, hard BE 1R, trail amplio) vive en BotService/ExitProfiles.
/// Umbrales suavizados para evitar dias sin operaciones en mercado lateral.
/// </summary>
public sealed class PullbackHtfStrategySignal : IStrategySignalProvider
{
    public StrategyType Strategy => StrategyType.PullbackHtf;

    private const decimal MaxEntryRsi = 54m;
    private const decimal MinEntryRsi = 28m;
    private const decimal FailedBounceRsi = 22m;

    public bool ShouldBuy(TechnicalMarketSnapshot technical) =>
        technical.Rsi14 is >= MinEntryRsi and <= MaxEntryRsi &&
        technical.LastPrice > 0m &&
        technical.EmaSlow > 0m &&
        technical.LastPrice <= technical.EmaSlow * 1.002m &&
        technical.MacdHistogram >= technical.PreviousMacdHistogram &&
        technical.EmaFast >= technical.EmaSlow * 0.992m;

    /// <summary>
    /// tf1h = contexto superior. Solo invalidar rebote si la estructura se rompe de verdad.
    /// </summary>
    public bool ShouldSell(TechnicalMarketSnapshot technical, TechnicalMarketSnapshot? tf1h)
    {
        var hourlyTrendBroke = tf1h is not null &&
                               tf1h.EmaFast < tf1h.EmaSlow * 0.997m &&
                               technical.MacdHistogram < 0m;
        var rsiCollapsed = technical.Rsi14 < FailedBounceRsi;
        var deepMacdFail = technical.MacdHistogram < technical.PreviousMacdHistogram &&
                           technical.MacdHistogram < -0.00015m * Math.Max(1m, technical.LastPrice) &&
                           technical.MacdLine < technical.MacdSignal;
        var lostEmaHard = technical.LastPrice > 0m &&
                          technical.EmaSlow > 0m &&
                          technical.LastPrice < technical.EmaSlow * 0.980m &&
                          technical.MacdHistogram < 0m;
        return hourlyTrendBroke || (rsiCollapsed && deepMacdFail) || lostEmaHard;
    }

    public decimal ScoreBuyCandidate(TechnicalMarketSnapshot technical) =>
        (50m - technical.Rsi14) +
        (technical.MacdHistogram - technical.PreviousMacdHistogram) * 700m +
        (technical.RelativeVolume * 3m);

    /// <summary>Solo exige tendencia 1h; el 15m puede ir un poco rezagado en el pullback.</summary>
    public bool PassesMultiTimeframeTrend(TechnicalMarketSnapshot tf15, TechnicalMarketSnapshot tf1h) =>
        tf1h.EmaFast >= tf1h.EmaSlow * 0.998m &&
        tf15.EmaFast >= tf15.EmaSlow * 0.990m;

    public string DescribeBuySignalGap(TechnicalMarketSnapshot t)
    {
        if (t.Rsi14 < MinEntryRsi || t.Rsi14 > MaxEntryRsi)
        {
            return $"Pullback HTF 15m: RSI {t.Rsi14:0.#} fuera de zona ({MinEntryRsi:0}-{MaxEntryRsi:0}).";
        }

        if (t.LastPrice <= 0m || t.EmaSlow <= 0m || t.LastPrice > t.EmaSlow * 1.002m)
        {
            return $"Pullback HTF 15m: precio {t.LastPrice:0.####} lejos por encima de EMA21 {t.EmaSlow:0.####}.";
        }

        if (t.MacdHistogram < t.PreviousMacdHistogram)
        {
            return "Pullback HTF 15m: histograma MACD en contraccion.";
        }

        if (t.EmaFast < t.EmaSlow * 0.992m)
        {
            return "Pullback HTF 15m: EMA9 demasiado por debajo de EMA21.";
        }

        return "Condicion de entrada 15m no cumplida.";
    }

    public string? DescribeShortRegimeFailure(TechnicalMarketSnapshot technical, MarketTicker ticker)
    {
        if (technical.LastPrice <= 0m)
        {
            return "Regimen HTF: precio invalido.";
        }

        if (Math.Abs(ticker.PriceChangePercent24h) >= 8.0m)
        {
            return $"Regimen HTF: |Δ24h|={Math.Abs(ticker.PriceChangePercent24h):0.##}% extremo.";
        }

        var emaSpreadPct = Math.Abs(technical.EmaFast - technical.EmaSlow) / technical.LastPrice * 100m;
        if (emaSpreadPct > 5.0m)
        {
            return $"Regimen HTF: separacion EMA alta ({emaSpreadPct:0.###}%).";
        }

        if (technical.VolatilityPercent > 4.5m || technical.AtrPercent > 5.0m)
        {
            return $"Regimen HTF: vol/ATR altos (vol%={technical.VolatilityPercent:0.##}).";
        }

        return null;
    }

    public bool PassesShortRegimeFilter(TechnicalMarketSnapshot technical, MarketTicker ticker) =>
        DescribeShortRegimeFailure(technical, ticker) is null;

    public string? DescribeLongTermRegimeFailure(LongTermRegimeSnapshot? regime)
    {
        if (regime is null || !regime.HasData)
        {
            return null;
        }

        // Lateral/recuperacion: precio cerca de EMA200 D1 (tolerancia mas amplia).
        var aboveEma200 = regime.DailyEma200 > 0m && regime.LastClose >= regime.DailyEma200 * 0.97m;
        if (!regime.DailyTrendUp && !aboveEma200)
        {
            return "Regimen D1 HTF: tendencia diaria no alcista y precio bajo EMA200.";
        }

        if (regime.DailyAtrPercentileVsYear > StrategySignalConstants.LongTermMaxAtrPercentileVsYear)
        {
            return $"Regimen D1 HTF: volatilidad extrema (ATR percentil {regime.DailyAtrPercentileVsYear:0.#}).";
        }

        return null;
    }

    public bool PassesLongTermRegime(LongTermRegimeSnapshot? regime) =>
        DescribeLongTermRegimeFailure(regime) is null;
}
