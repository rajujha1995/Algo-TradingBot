using TradingBot.Domain;
namespace TradingBot.Application;

public sealed record BacktestResult(decimal InitialCapital, decimal FinalEquity, decimal NetReturnPercent, decimal MaximumDrawdownPercent, int NumberOfTrades, decimal WinRate, decimal TotalCharges, IReadOnlyList<decimal> EquityCurve, string Assumptions);
public static class Backtest
{
  // Decisions use completed candles only; fills occur at the following candle's open.
  public static BacktestResult Run(IReadOnlyList<Candle> candles, decimal capital = 1000, int lookback = 5, decimal slippageBps = 5)
  {
    if (capital <= 0 || capital > 1000 || lookback < 2 || slippageBps < 0 || slippageBps > 100 || candles.Count < lookback + 2 || candles.Count > 10000) throw new InvalidOperationException("Provide 7–10000 ordered candles and valid parameters.");
    for (var i = 0; i < candles.Count; i++)
    {
      var c = candles[i];
      if (c.TimestampUtc.Kind != DateTimeKind.Utc || c.Open <= 0 || c.Low <= 0 || c.High < c.Low || c.High < Math.Max(c.Open, c.Close) || c.Low > Math.Min(c.Open, c.Close) || c.Volume < 0 || (i > 0 && c.TimestampUtc <= candles[i - 1].TimestampUtc)) throw new InvalidOperationException("Candles must have valid OHLCV values and strictly increasing UTC timestamps.");
    }
    decimal cash = capital, basis = 0, charges = 0, peak = capital, maxDd = 0;
    int quantity = 0, trades = 0, wins = 0;
    var curve = new List<decimal>();
    for (int i = lookback; i < candles.Count; i++)
    {
      var average = candles.Skip(i - lookback).Take(lookback).Average(x => x.Close);
      var signal = candles[i - 1].Close > average;
      var current = candles[i];
      if (quantity == 0 && signal && current.Volume >= 1000 && i < candles.Count - 1)
      {
        var price = current.Open * (1 + slippageBps / 10000);
        var qty = (int)Math.Min(decimal.Floor(cash / price), current.Volume / 100); // <=1% candle volume
        while (qty > 0 && qty * price + TradingCosts.Estimate(qty * price, false) > cash) qty--;
        if (qty > 0) { quantity = qty; var fee = TradingCosts.Estimate(qty * price, false); basis = qty * price + fee; cash -= basis; charges += fee; }
      }
      else if (quantity > 0 && (!signal || i == candles.Count - 1))
      {
        var price = current.Open * (1 - slippageBps / 10000);
        var fee = TradingCosts.Estimate(quantity * price, true); var proceeds = quantity * price - fee;
        cash += proceeds; charges += fee; trades++; if (proceeds > basis) wins++; quantity = 0;
      }
      var equity = cash + quantity * current.Close; curve.Add(equity); peak = Math.Max(peak, equity); maxDd = Math.Max(maxDd, (peak - equity) / peak * 100);
    }
    var final = cash + quantity * candles[^1].Close;
    return new(capital, decimal.Round(final, 2), decimal.Round((final - capital) / capital * 100, 2), decimal.Round(maxDd, 2), trades, trades == 0 ? 0 : decimal.Round(100m * wins / trades, 2), charges, curve, "SMA momentum; next-candle open; indicative NSE delivery costs; <=1% candle volume; input must already exclude non-session candles and reflect corporate actions. Research only.");
  }
}
