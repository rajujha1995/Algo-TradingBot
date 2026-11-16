using System.Net;
using System.Text.Json;
using TradingBot.Application;
using TradingBot.Infrastructure;
namespace UnitTests;

public class BrokerRealtimeTests
{
  static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-11-16T05:00:00Z");
  static TradingOptions Options(string token = "test-not-real") => new() { AccessToken = token, LocalPassword = "disposable-test-password", JournalPath = "realtime-journal.json" };
  static JsonElement Json(string text) { using var document = JsonDocument.Parse(text); return document.RootElement.Clone(); }
  static JsonElement Event(string status, long timestamp, string extra = "")
  {
    var data = new Dictionary<string, object> { ["order_id"] = "123", ["order_status"] = status, ["entity_name"] = "TEST", ["order_type"] = "BUY", ["req_quantity"] = 10 };
    if (extra.Length > 0) foreach (var field in Json("{" + extra.TrimStart(',') + "}").EnumerateObject()) data[field.Name] = field.Value.Clone();
    return JsonSerializer.SerializeToElement(new { mode = "order_update", timestamp, data });
  }

  [Fact] public async Task GainersSizedBatchPassesTheRealAdapterValidator()
  {
    var handler = new GetOnlyHandler(); var broker = new IndstocksBrokerAdapter(new HttpClient(handler), Options());
    await broker.GetQuotesAsync(string.Join(',', Enumerable.Range(1, 200).Select(i => $"NSE_{i}")), default);
    Assert.Single(handler.Paths); Assert.Contains("/market/quotes/full?", handler.Paths[0]);
  }
  [Fact] public async Task OversizedQuoteBatchFailsBeforeNetwork()
  {
    var handler = new GetOnlyHandler(); var broker = new IndstocksBrokerAdapter(new HttpClient(handler), Options());
    await Assert.ThrowsAsync<InvalidOperationException>(() => broker.GetQuotesAsync(string.Join(',', Enumerable.Range(1, 501).Select(i => $"NSE_{i}")), default));
    Assert.Empty(handler.Paths);
  }
  [Fact] public async Task AccountBookReadsUseDocumentedGetPathsOnly()
  {
    var handler = new GetOnlyHandler(); var broker = new IndstocksBrokerAdapter(new HttpClient(handler), Options());
    await broker.GetOrderBookAsync(default); await broker.GetTradeBookAsync(default);
    Assert.Equal(new[] { "/order-book", "/trade-book?segment=EQUITY" }, handler.Paths);
  }
  [Theory]
  [InlineData("R", "Received")]
  [InlineData("PF", "Partially filled")]
  [InlineData("PFC", "Partially filled; remainder cancelled")]
  [InlineData("RJ", "Rejected")]
  public void OrderStatusesDoNotBecomeAssumedSuccess(string code, string expected) => Assert.Equal(expected, BrokerOrderFeed.Merge(null, Event(code, Now.ToUnixTimeMilliseconds()), Now)!.Status);
  [Fact] public void MissingFillFieldsRemainUnknownAndPartialUpdatesRetainLatestKnownFill()
  {
    var initial = BrokerOrderFeed.Merge(null, Event("PF", Now.ToUnixTimeMilliseconds()), Now)!;
    Assert.Null(initial.ExecutionPrice); Assert.Null(initial.FilledLots);
    var executed = BrokerOrderFeed.Merge(initial, Event("S", Now.ToUnixTimeMilliseconds() + 1, ",\"lot\":2,\"executed_price\":100"), Now.AddMilliseconds(1))!;
    var repeated = BrokerOrderFeed.Merge(executed, Event("PFC", Now.ToUnixTimeMilliseconds() + 2), Now.AddMilliseconds(2))!;
    Assert.Equal(100, repeated.ExecutionPrice); Assert.Equal(2, repeated.FilledLots); Assert.Equal(10, repeated.RequestedQuantity);
  }
  [Fact] public void OlderOrderEventsCannotOverwriteNewerState()
  {
    var newer = BrokerOrderFeed.Merge(null, Event("S", Now.ToUnixTimeMilliseconds()), Now)!;
    Assert.Same(newer, BrokerOrderFeed.Merge(newer, Event("P", Now.AddSeconds(-1).ToUnixTimeMilliseconds()), Now));
    Assert.Null(BrokerOrderFeed.Merge(null, Event("S", Now.AddMinutes(5).ToUnixTimeMilliseconds()), Now));
  }
  [Fact] public void FramesDecodeDoubleEncodedOrderUpdatesAndIgnoreHeartbeats()
  {
    var raw = Event("P", Now.ToUnixTimeMilliseconds()).GetRawText();
    Assert.Equal("order_update", BrokerFrames.Decode(JsonSerializer.Serialize(raw)).GetProperty("mode").GetString());
    Assert.True(BrokerFrames.IsHeartbeat("\"heartbeat\"")); Assert.True(BrokerFrames.IsHeartbeat("pong"));
    Assert.Null(ObservationPriceFeed.ParseFrame("ping", [], Now.UtcDateTime, 1));
  }
  [Fact] public void SharedPriceOwnersDoNotRemoveEachOthersSubscriptions()
  {
    var feed = new ObservationPriceFeed(Options(), TimeProvider.System);
    feed.Configure([new("NSE_1", "RESEARCH", "NSE", "")]);
    feed.ConfigureFor("account", [new("NSE_2", "HOLDING", "NSE", "")]);
    Assert.Equal(2, feed.SubscribedCount);
    feed.Configure([]); Assert.Equal(1, feed.SubscribedCount);
    feed.ConfigureFor("account", []); Assert.False(feed.HasSubscriptions);
  }
  [Fact] public void AmbiguousNumericTokensAreNotSubscribedOrGuessed()
  {
    var feed = new ObservationPriceFeed(Options(), TimeProvider.System);
    feed.Configure([new("NSE_1", "A", "NSE", "")]);
    feed.ConfigureFor("account", [new("BSE_1", "B", "BSE", "")]);
    Assert.Equal(0, feed.SubscribedCount);
    var row = Json("""{"security_id":"1","symbol":"UNKNOWN"}""");
    Assert.Null(BrokerAccountMonitor.Resolve(row, [new("NSE_1", "A", "NSE", ""), new("BSE_1", "B", "BSE", "")]));
  }
  [Fact] public void DerivativeTokenCollisionCannotBecomeCashEquityValuation()
  {
    var row = Json("""{"security_id":"1","exchange":"NSE","segment":"DERIVATIVE"}""");
    Assert.Null(BrokerAccountMonitor.Resolve(row, [new("NSE_1", "A", "NSE", "")]));
  }
  [Fact] public void MissingFieldsOrPricesNeverBecomeZeroBalanceOrPnl()
  {
    var row = Json("""{"security_id":"1","symbol":"TEST","isin":"ISIN1","avg_price":100}""");
    var master = new[] { new EquityInstrument("NSE_1", "TEST", "NSE", "ISIN1") };
    var mark = BrokerAccountMonitor.Mark(row, master, _ => new("NSE_1", 110, Now.UtcDateTime, Now.UtcDateTime, 1));
    Assert.Null(mark.Quantity); Assert.Null(mark.MarketValue); Assert.Null(mark.UnrealizedPnl);
    mark = BrokerAccountMonitor.Mark(Json("""{"security_id":"1","symbol":"TEST","isin":"ISIN1","avg_price":100,"total_qty":10}"""), master, _ => null);
    Assert.False(mark.Fresh); Assert.Null(mark.Price); Assert.Null(mark.UnrealizedPnl);
  }
  [Fact] public void ActualPriceMarkUsesSnapshotQuantityWithoutInventingTotalAccountEquity()
  {
    var row = Json("""{"security_id":"1","exchange":"NSE","symbol":"TEST","avg_price":100,"total_qty":10}""");
    var mark = BrokerAccountMonitor.Mark(row, [new("NSE_1", "TEST", "NSE", "")], _ => new("NSE_1", 110, Now.UtcDateTime, Now.UtcDateTime, 1));
    Assert.Equal(1100, mark.MarketValue); Assert.Equal(100, mark.UnrealizedPnl);
  }
  [Fact] public void AccountParserRejectsUnsuccessfulAndWrongShapeResponses()
  {
    Assert.Throws<InvalidOperationException>(() => BrokerAccountMonitor.Validate(Json("""{"status":"error","data":{}}"""), JsonValueKind.Object));
    Assert.Throws<InvalidOperationException>(() => BrokerAccountMonitor.Validate(Json("""{"status":"success","data":{}}"""), JsonValueKind.Array));
  }
  sealed class GetOnlyHandler : HttpMessageHandler
  {
    public List<string> Paths = [];
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) {
      Assert.Equal(HttpMethod.Get, request.Method); Paths.Add(request.RequestUri!.PathAndQuery);
      return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{\"status\":\"success\",\"data\":[]}") });
    }
  }
}
