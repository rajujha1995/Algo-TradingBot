using TradingBot.Domain;
namespace TradingBot.Application;

public sealed record ObservationEvidence(IReadOnlyList<Candle> Candles, decimal? Ema20, decimal? Rsi14, decimal? Atr14, decimal? RelativeVolume, decimal? Vwap, decimal? BreakoutLevel, string News = "Not checked: no verified news provider connected.", string Context = "Index/sector confirmation not implemented. Technical-only research.");
public sealed record ObservationDecision(bool Qualifies, string Reason, DateTime? CandleCloseUtc, decimal Entry, decimal Stop, decimal Target, ObservationEvidence Evidence);

public static class ObservationStrategy
{
  public const string Version = "Breakout-v1";
  public static bool NormalSession(DateTimeOffset utc)
  {
    var ist = TradingSessionPolicy.InIndia(utc);
    return ist.DayOfWeek is not (DayOfWeek.Saturday or DayOfWeek.Sunday) && ist.TimeOfDay >= new TimeSpan(9, 15, 0) && ist.TimeOfDay < new TimeSpan(15, 30, 0);
  }
  // Same-day NSE cash research model; no DP charge or delivery STT for assumed same-day exits.
  // NSE/FA/73061 effective 2026-03-01: exchange 306.99/crore + IPFT .01/crore each side.
  // API brokerage assumption Rs10/order. Rounding/account-specific charges remain indicative.
  public static decimal LegFees(decimal turnover, bool sell)
  {
    var exchange = turnover * .000030699m; var ipft = turnover * .000000001m; var sebi = turnover * .000001m;
    return decimal.Round(10m + exchange + ipft + sebi + (10m + exchange + sebi) * .18m + (sell ? turnover * .00025m : turnover * .00003m), 2, MidpointRounding.AwayFromZero);
  }
  public static decimal Fees(decimal entry, decimal exit, int quantity) => LegFees(entry * quantity, false) + LegFees(exit * quantity, true);
  public static decimal Net(decimal entry, decimal exit, int quantity) => (exit - entry) * quantity - Fees(entry, exit, quantity);

  public static ObservationDecision Evaluate(IReadOnlyList<Candle> input, DateTimeOffset now)
  {
    var candles = input.Where(x => x.TimestampUtc.AddMinutes(5) <= now.UtcDateTime.AddSeconds(-2)).OrderBy(x => x.TimestampUtc).TakeLast(200).ToList();
    var empty = new ObservationEvidence(candles.TakeLast(60).ToList(), null, null, null, null, null, null);
    ObservationDecision Wait(string reason) => new(false, reason, candles.Count > 0 ? candles[^1].TimestampUtc.AddMinutes(5) : null, 0, 0, 0, empty);
    if (candles.Count < 60) return Wait("WAIT: need at least 60 completed five-minute candles for indicator warmup.");
    if (candles.Select(x => x.TimestampUtc).Distinct().Count() != candles.Count || candles.Any(x => x.Open <= 0 || x.Low <= 0 || x.High < Math.Max(x.Open, x.Close) || x.Low > Math.Min(x.Open, x.Close) || x.High < x.Low || x.Volume < 0)) return Wait("WAIT: invalid or duplicate OHLCV bars.");
    for (var i = 1; i < candles.Count; i++) {
      var before = TradingSessionPolicy.InIndia(new DateTimeOffset(candles[i - 1].TimestampUtc, TimeSpan.Zero));
      var after = TradingSessionPolicy.InIndia(new DateTimeOffset(candles[i].TimestampUtc, TimeSpan.Zero));
      if (before.Date == after.Date && after - before != TimeSpan.FromMinutes(5)) return Wait("WAIT: missing intraday candles; indicators are not evaluated across an unverified gap.");
    }
    var latest = candles[^1]; var closeUtc = latest.TimestampUtc.AddMinutes(5);
    if (!NormalSession(now) || TradingSessionPolicy.InIndia(new DateTimeOffset(closeUtc, TimeSpan.Zero)).Date != TradingSessionPolicy.InIndia(now).Date || now.UtcDateTime - closeUtc > TimeSpan.FromMinutes(8)) return Wait("WAIT: no recent completed candle in the current normal cash session. Holidays/special sessions are not inferred.");
    var ema = candles.Take(20).Average(x => x.Close);
    foreach (var c in candles.Skip(20)) ema += (c.Close - ema) * 2m / 21m;
    decimal gain = 0, loss = 0, atr = 0;
    for (var i = 1; i < candles.Count; i++) {
      var delta = candles[i].Close - candles[i - 1].Close;
      var tr = Math.Max(candles[i].High - candles[i].Low, Math.Max(Math.Abs(candles[i].High - candles[i - 1].Close), Math.Abs(candles[i].Low - candles[i - 1].Close)));
      if (i <= 14) { gain += Math.Max(delta, 0) / 14; loss += Math.Max(-delta, 0) / 14; atr += tr / 14; }
      else { gain = (gain * 13 + Math.Max(delta, 0)) / 14; loss = (loss * 13 + Math.Max(-delta, 0)) / 14; atr = (atr * 13 + tr) / 14; }
    }
    var rsi = loss == 0 ? gain == 0 ? 50 : 100 : 100 - 100 / (1 + gain / loss);
    var prior = candles.Skip(candles.Count - 21).Take(20).ToList();
    var level = prior.Max(x => x.High); var averageVolume = prior.Average(x => (decimal)x.Volume);
    var relativeVolume = averageVolume == 0 ? 0 : latest.Volume / averageVolume;
    var today = candles.Where(x => TradingSessionPolicy.InIndia(new DateTimeOffset(x.TimestampUtc, TimeSpan.Zero)).Date == TradingSessionPolicy.InIndia(now).Date).ToList();
    if (today.Count == 0 || TradingSessionPolicy.InIndia(new DateTimeOffset(today[0].TimestampUtc, TimeSpan.Zero)).TimeOfDay != new TimeSpan(9, 15, 0)) return Wait("WAIT: current-session opening candle is missing; session VWAP cannot be verified.");
    var volume = today.Sum(x => (decimal)x.Volume);
    var vwap = volume == 0 ? 0 : today.Sum(x => (x.High + x.Low + x.Close) / 3 * x.Volume) / volume;
    var evidence = new ObservationEvidence(candles.TakeLast(60).ToList(), ema, rsi, atr, relativeVolume, vwap, level);
    var failures = new List<string>();
    if (latest.Close <= level) failures.Add("close did not exceed the preceding 20-bar high");
    if (latest.Close <= ema || latest.Close <= vwap) failures.Add("close is not above both EMA20 and session VWAP");
    if (relativeVolume < 1.5m) failures.Add("volume is below 1.5× the preceding 20-bar average");
    if (rsi < 50 || rsi > 75) failures.Add("RSI14 is outside 50–75");
    if (latest.High == latest.Low || latest.Close < latest.Low + (latest.High - latest.Low) * .75m) failures.Add("close is not in the top quarter of its candle range");
    if (atr <= 0 || latest.Volume <= 0) failures.Add("volatility or volume is unusable");
    var entry = latest.Close; var stop = Math.Min(latest.Low, entry - atr); var risk = entry - stop; var target = entry + 2 * risk;
    if (stop <= 0 || risk <= 0 || Net(entry, target, 10) < 1.5m * Math.Abs(Net(entry, stop, 10))) failures.Add("estimated net reward/risk is below 1.5 after assumed costs");
    var reason = failures.Count > 0 ? "NO SETUP: " + string.Join("; ", failures) + "."
      : $"Completed 5-minute close {entry:F2} > prior 20-bar high {level:F2}; above EMA20 {ema:F2} and VWAP {vwap:F2}; relative volume {relativeVolume:F2}×; RSI14 {rsi:F1}; ATR14 {atr:F2}. Top-quarter close confirmed. Stop invalidates the setup; target is a 2R scenario, not a forecast. News/index context not checked.";
    return new(failures.Count == 0, reason, closeUtc, entry, stop, target, evidence);
  }
}
