using System.Text.Json;
using TradingBot.Application;
using TradingBot.Infrastructure;
namespace UnitTests;

public class AccountMonitorLifecycleTests
{
  static TradingOptions Options(string token = "fixture-token-not-real") => new() { AccessToken = token };
  static JsonElement Json(string text) { using var doc = JsonDocument.Parse(text); return doc.RootElement.Clone(); }
  [Fact] public void MissingTokenCannotEnableAnyMonitoring()
  {
    var options = Options(""); var fake = new FixtureBroker(); var prices = new ObservationPriceFeed(options, TimeProvider.System);
    var monitor = new BrokerAccountMonitor(fake, fake, prices, new BrokerOrderFeed(options, TimeProvider.System), options, TimeProvider.System);
    Assert.Throws<InvalidOperationException>(() => monitor.SetEnabled(true)); Assert.False(monitor.Enabled); Assert.Equal(0, fake.Calls);
  }
  [Fact] public async Task ReadsAccountSnapshotsWithoutSendingOrdersAndStopReleasesOnlyItsSubscriptions()
  {
    var options = Options(); var fake = new FixtureBroker(); var prices = new ObservationPriceFeed(options, TimeProvider.System);
    prices.Configure([new("NSE_999", "RESEARCH", "NSE", "")]);
    using var monitor = new BrokerAccountMonitor(fake, fake, prices, new BrokerOrderFeed(options, TimeProvider.System), options, TimeProvider.System);
    monitor.SetEnabled(true); await monitor.StartAsync(default);
    try {
      var deadline = DateTime.UtcNow.AddSeconds(5);
      JsonElement snapshot;
      do { await Task.Delay(25); snapshot = JsonSerializer.SerializeToElement(monitor.Snapshot()); } while (snapshot.GetProperty("mappingStatus").GetString() == "Not resolved" && DateTime.UtcNow < deadline);
      Assert.Equal(1, snapshot.GetProperty("holdingMarks").GetArrayLength());
      Assert.Equal(JsonValueKind.Null, snapshot.GetProperty("holdingMarks")[0].GetProperty("MarketValue").ValueKind);
      Assert.True(snapshot.GetProperty("sections").TryGetProperty("funds", out _));
      Assert.Equal(2, prices.SubscribedCount); Assert.Equal(0, fake.OrderCalls);
      monitor.SetEnabled(false); Assert.Equal(1, prices.SubscribedCount); Assert.False(monitor.Enabled);
    } finally { await monitor.StopAsync(default); }
  }
  sealed class FixtureBroker : ITradingBroker, IBrokerAccountReader
  {
    public int Calls, OrderCalls;
    Task<JsonElement> Response(string text) { Calls++; return Task.FromResult(Json(text)); }
    public Task<JsonElement> GetFundsAsync(CancellationToken ct) => Response("""{"status":"success","data":{"detailed_avl_balance":{"eq_cnc":100}}}""");
    public Task<JsonElement> GetHoldingsAsync(CancellationToken ct) => Response("""{"status":"success","data":[{"symbol":"RELIANCE","security_id":"1","isin":"TEST_ISIN","total_qty":10,"avg_price":100}]}""");
    public Task<JsonElement> GetPositionsAsync(CancellationToken ct) => Response("""{"status":"success","data":[]}""");
    public Task<JsonElement> GetOrderBookAsync(CancellationToken ct) => Response("""{"status":"success","data":[]}""");
    public Task<JsonElement> GetTradeBookAsync(CancellationToken ct) => Response("""{"status":"success","data":[]}""");
    public Task<string> GetInstrumentsAsync(CancellationToken ct) { Calls++; return Task.FromResult("EXCH,SEGMENT,SECURITY_ID,INSTRUMENT_NAME,TRADING_SYMBOL,ISIN\nNSE,E,1,EQUITY,RELIANCE,TEST_ISIN"); }
    public Task<JsonElement> GetProfileAsync(CancellationToken ct) => throw new NotSupportedException();
    public Task<JsonElement> GetQuotesAsync(string codes, CancellationToken ct) => throw new NotSupportedException();
    public Task<JsonElement> GetCandlesAsync(string code, long start, long end, CancellationToken ct) => throw new NotSupportedException();
    public Task PlaceOrderAsync(CancellationToken ct) { OrderCalls++; throw new NotSupportedException(); }
  }
}
