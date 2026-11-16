using System.Text.Json;
using TradingBot.Infrastructure;

public class RealtimeWatchTests
{
  [Fact] public void OnlyFreshLaterMatchingPricesMoveTheReference()
  {
    var now = DateTime.UtcNow;
    var entry = new WatchEntry { Code = "NSE_1", ReferencePrice = 100, Quantity = 10, ReferenceUtc = new(now.AddSeconds(-5), TimeSpan.Zero), Stop = 95, Target = 110 };
    RealtimeResearchEngine.ApplyPrice(entry, new("NSE_2", 120, now, now, 1), now); Assert.Null(entry.LatestPrice);
    RealtimeResearchEngine.ApplyPrice(entry, new("NSE_1", 120, now.AddMinutes(-1), now, 1), now); Assert.Null(entry.LatestPrice);
    RealtimeResearchEngine.ApplyPrice(entry, new("NSE_1", 111, now, now, 1), now);
    Assert.Equal(110, entry.ObservedMove); Assert.NotNull(entry.TargetObservedUtc); Assert.Null(entry.StopObservedUtc);
    RealtimeResearchEngine.ApplyPrice(entry, new("NSE_1", 90, now.AddSeconds(-1), now, 1), now); Assert.Equal(111, entry.LatestPrice);
    entry.Status = "PAUSED"; RealtimeResearchEngine.ApplyPrice(entry, new("NSE_1", 90, now.AddSeconds(1), now.AddSeconds(1), 1), now.AddSeconds(1)); Assert.Null(entry.StopObservedUtc);
  }
  [Fact] public void TodaysCloseRequiresCurrentDatedCandleAfterClose()
  {
    var day = new DateTimeOffset(2026, 11, 17, 3, 45, 0, TimeSpan.Zero);
    JsonElement Bar(DateTimeOffset date) => JsonSerializer.SerializeToElement(new { success = true, data = new Dictionary<string, object> { ["NSE_1"] = new { candles = new[] { new { ts = date.ToUnixTimeSeconds(), o = 100, h = 120, l = 95, c = 115, v = 100 } } } } });
    Assert.Null(TopGainersService.ClosingPrice(Bar(day), "NSE_1", day.AddHours(4)).Price);
    var close = TopGainersService.ClosingPrice(Bar(day), "NSE_1", day.AddHours(7));
    Assert.Equal(115, close.Price); Assert.Equal("2026-11-17", close.Date);
    Assert.Null(TopGainersService.ClosingPrice(Bar(day.AddDays(-1)), "NSE_1", day.AddHours(7)).Price);
    Assert.Null(TopGainersService.ClosingPrice(Bar(day), "NSE_2", day.AddHours(7)).Price);
  }
}
