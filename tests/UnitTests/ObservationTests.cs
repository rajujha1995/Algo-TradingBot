using System.Text.Json;
using TradingBot.Application;
using TradingBot.Domain;
using TradingBot.Infrastructure;
namespace UnitTests;

public class ObservationTests
{
  static readonly DateTime Now = new(2026, 11, 16, 9, 0, 10, DateTimeKind.Utc); // 14:30 IST
  [Theory]
  [InlineData(-10, true)]
  [InlineData(-16, false)]
  [InlineData(3, false)]
  public void FeedRejectsStaleAndFutureTimestamps(int offset, bool expected) => Assert.Equal(expected, ObservationPriceFeed.IsFresh(Now.AddSeconds(offset), Now, Now));

  [Fact] public void ParsesDocumentedFrameAndMapsOnlyResolvedInstrument()
  {
    var frame = JsonSerializer.Serialize(new { mode = "ltp", instrument = "123", timestamp = new DateTimeOffset(Now).ToUnixTimeMilliseconds(), data = new { ltp = 100 } });
    var instruments = new[] { new EquityInstrument("NSE_123", "TEST", "NSE", "") };
    Assert.Equal("NSE_123", ObservationPriceFeed.ParseFrame(frame, instruments, Now, 2)!.Code);
    Assert.Equal(100m, ObservationPriceFeed.ParseFrame(JsonSerializer.Serialize(frame), instruments, Now, 2)!.Price);
    Assert.Null(ObservationPriceFeed.ParseFrame(frame, [], Now, 2));
    Assert.Null(ObservationPriceFeed.ParseFrame(frame, instruments, Now.AddMinutes(1), 2));
  }
  [Fact] public void HistoricalSecondsAreNotConfusedWithRequestMilliseconds()
  {
    using var json = JsonDocument.Parse("""{"success":true,"data":{"NSE_123":{"candles":[{"ts":1794819600,"o":100,"h":102,"l":99,"c":101,"v":500}]}}}""");
    var candle = Assert.Single(RealtimeResearchEngine.ParseCandles(json.RootElement, "NSE_123"));
    Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1794819600).UtcDateTime, candle.TimestampUtc);
    Assert.Empty(RealtimeResearchEngine.ParseCandles(json.RootElement, "NSE_456"));
  }
  static List<Candle> Bars()
  {
    // Synthetic volatility deliberately large enough to pass the conservative cost/risk filter.
    var bars = Enumerable.Range(0, 63).Select(i => new Candle(Now.AddSeconds(-10).AddMinutes(-315 + i * 5), i % 2 == 0 ? 995 : 1000, 1040, 950, i % 2 == 0 ? 1000 : 995, 1000)).ToList();
    bars[^1] = bars[^1] with { Open = 995, High = 1042, Low = 950, Close = 1041, Volume = 3000 };
    return bars;
  }
  [Fact] public void QualifyingSetupFreezesMeasurableReasonsAndRiskLevels()
  {
    var decision = ObservationStrategy.Evaluate(Bars(), new DateTimeOffset(Now));
    Assert.True(decision.Qualifies, decision.Reason);
    Assert.Contains("relative volume", decision.Reason); Assert.Contains("News/index context not checked", decision.Reason);
    Assert.True(decision.Stop < decision.Entry); Assert.True(decision.Target > decision.Entry);
    Assert.Equal(60, decision.Evidence.Candles.Count);
  }
  [Fact] public void FormingAndFutureCandlesCannotAlterTheConfirmedDecision()
  {
    var bars = Bars(); var first = ObservationStrategy.Evaluate(bars, new DateTimeOffset(Now));
    bars.Add(new Candle(Now.AddSeconds(-10), 1000, 9000, 900, 8000, 999999));
    var next = ObservationStrategy.Evaluate(bars, new DateTimeOffset(Now));
    Assert.Equal(first.Entry, next.Entry); Assert.Equal(first.Reason, next.Reason); Assert.Equal(first.CandleCloseUtc, next.CandleCloseUtc);
  }
  [Fact] public void MissingOrStaleCandlesBlockSetups()
  {
    var bars = Bars(); bars.RemoveAt(20);
    Assert.False(ObservationStrategy.Evaluate(bars, new DateTimeOffset(Now)).Qualifies);
    bars = Bars(); bars.RemoveAt(20); bars.Insert(0, bars[0] with { TimestampUtc = bars[0].TimestampUtc.AddMinutes(-5) });
    Assert.Contains("missing intraday", ObservationStrategy.Evaluate(bars, new DateTimeOffset(Now)).Reason);
    Assert.False(ObservationStrategy.Evaluate(Bars(), new DateTimeOffset(Now.AddMinutes(20))).Qualifies);
  }
}
